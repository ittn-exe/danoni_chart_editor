using DanoniEditor.Core.Models;

namespace DanoniEditor.Editing;

/// <summary>
/// フレーム情報モード(仕様書7.6、2026-07-17i)のセッション状態。
/// 内部データは常にtickが真。本クラスは「全オブジェクト(ノート/フリーズ/speed/boost/マーカー)の
/// 絶対フレームのスナップショット」を保持する振る舞いレイヤで、BPM構成が変わるたびに
/// スナップショットのフレーム値から全tickを逆算し直すことで「絶対フレーム固定」を実現する。
/// 逆算は毎回スナップショット(固定値)から行うため、BPMを何度調整しても丸め誤差は累積しない。
/// オブジェクト自体の編集(配置・移動等)が行われた場合はスナップショットを取り直す。
/// プロジェクトファイルには保存しない(モードはセッション限り。仕様書7.6「明示操作でのみ切替」)。
/// 拍子(TimeSignature)はtick⇔frame変換に関与しない(変換はBPMのみ依存)ため、拍子変更は
/// 元々オブジェクトのフレームを動かさず、本モードの特別扱いは不要である点に注意。
/// </summary>
public sealed class FrameEditState
{
    private sealed class TabFrames
    {
        public List<double>[] NoteFrames = [];
        public List<(double Start, double End)>[] FreezeFrames = [];
        public List<double> SpeedFrames = [];
        public List<double> BoostFrames = [];
    }

    private List<TabFrames> _tabs = [];
    private List<double> _markerFrames = [];

    public static FrameEditState Capture(EditorDocument doc)
    {
        var s = new FrameEditState();
        s.Rebuild(doc);
        return s;
    }

    /// <summary>現在のtick+タイミングからスナップショットを取り直す(モードON時・オブジェクト編集後)</summary>
    public void Rebuild(EditorDocument doc)
    {
        var timing = doc.Project.CreateTimingEngine();
        _tabs = doc.Project.Tabs.Select(tab => new TabFrames
        {
            NoteFrames = tab.Lanes
                .Select(l => l.Notes.Select(t => timing.TickToFrame(t)).ToList()).ToArray(),
            FreezeFrames = tab.Lanes
                .Select(l => l.Freezes.Select(f => (timing.TickToFrame(f.StartTick), timing.TickToFrame(f.EndTick))).ToList()).ToArray(),
            SpeedFrames = tab.SpeedEvents.Select(e => timing.TickToFrame(e.Tick)).ToList(),
            BoostFrames = tab.BoostEvents.Select(e => timing.TickToFrame(e.Tick)).ToList(),
        }).ToList();
        _markerFrames = doc.Project.Markers.Select(m => timing.TickToFrame(m.Tick)).ToList();
    }

    /// <summary>スナップショットと現在のコレクションの件数が一致しているか(不一致=想定外の経路で
    /// スナップショット更新が漏れた状態。RetickAllは安全側に倒してRebuildしてから処理する)</summary>
    private bool IsAligned(ChartProject p)
    {
        if (p.Tabs.Count != _tabs.Count || p.Markers.Count != _markerFrames.Count) return false;
        for (int i = 0; i < p.Tabs.Count; i++)
        {
            var tab = p.Tabs[i];
            var s = _tabs[i];
            if (tab.Lanes.Count != s.NoteFrames.Length) return false;
            if (tab.SpeedEvents.Count != s.SpeedFrames.Count || tab.BoostEvents.Count != s.BoostFrames.Count) return false;
            for (int l = 0; l < tab.Lanes.Count; l++)
                if (tab.Lanes[l].Notes.Count != s.NoteFrames[l].Count || tab.Lanes[l].Freezes.Count != s.FreezeFrames[l].Count)
                    return false;
        }
        return true;
    }

    /// <summary>スナップショットのフレーム値から全オブジェクトのtickを逆算して書き戻す
    /// (BPM/StartNumber変更後に呼ぶ)。差し替え前のコレクション一式を返す(Undo用)。</summary>
    public RetickBackup RetickAll(EditorDocument doc)
    {
        if (!IsAligned(doc.Project)) Rebuild(doc); // 防御的: 実質no-op retickになる

        var timing = doc.Project.CreateTimingEngine();
        var backup = RetickBackup.Capture(doc.Project);
        long T(double f) => Math.Max(0, (long)Math.Round(timing.FrameToTick(f)));

        for (int ti = 0; ti < doc.Project.Tabs.Count; ti++)
        {
            var tab = doc.Project.Tabs[ti];
            var snap = _tabs[ti];
            for (int li = 0; li < tab.Lanes.Count; li++)
            {
                var lane = tab.Lanes[li];
                lane.Notes = snap.NoteFrames[li].Select(T).ToList();
                // フリーズは最低1tickの長さを保証(start<endの不変条件維持)
                lane.Freezes = snap.FreezeFrames[li]
                    .Select(p => { long s = T(p.Start); return new FreezeNote(s, Math.Max(s + 1, T(p.End))); })
                    .ToList();
            }
            tab.SpeedEvents = tab.SpeedEvents.Zip(snap.SpeedFrames, (e, f) => new ValueEvent(T(f), e.Value)).ToList();
            tab.BoostEvents = tab.BoostEvents.Zip(snap.BoostFrames, (e, f) => new ValueEvent(T(f), e.Value)).ToList();
        }
        doc.Project.Markers = doc.Project.Markers.Zip(_markerFrames, (m, f) => new Marker(T(f), m.Comment)).ToList();
        return backup;
    }

    /// <summary>丸め込みによる同一tickへの重複を検出し、説明文の一覧を返す(空=衝突なし)。
    /// モードOFF前の検査に使う(2026-07-17i、ユーザー確定仕様: 警告して続行/中止を選択)。</summary>
    public static IReadOnlyList<string> FindCollisions(ChartProject p)
    {
        var result = new List<string>();
        for (int ti = 0; ti < p.Tabs.Count; ti++)
        {
            var tab = p.Tabs[ti];
            for (int li = 0; li < tab.Lanes.Count; li++)
            {
                foreach (var g in tab.Lanes[li].Notes.GroupBy(t => t).Where(g => g.Count() > 1))
                    result.Add($"タブ{ti + 1} レーン{li + 1}: tick {g.Key} にノート{g.Count()}件");
                foreach (var g in tab.Lanes[li].Freezes.GroupBy(f => f.StartTick).Where(g => g.Count() > 1))
                    result.Add($"タブ{ti + 1} レーン{li + 1}: tick {g.Key} にフリーズ{g.Count()}件");
            }
            foreach (var g in tab.SpeedEvents.GroupBy(e => e.Tick).Where(g => g.Count() > 1))
                result.Add($"タブ{ti + 1}: tick {g.Key} に速度変更{g.Count()}件");
            foreach (var g in tab.BoostEvents.GroupBy(e => e.Tick).Where(g => g.Count() > 1))
                result.Add($"タブ{ti + 1}: tick {g.Key} にブースト変更{g.Count()}件");
        }
        foreach (var g in p.Markers.GroupBy(m => m.Tick).Where(g => g.Count() > 1))
            result.Add($"tick {g.Key} にマーカー{g.Count()}件");
        return result;
    }
}

/// <summary>RetickAllで差し替える前のコレクション一式(Undo用、2026-07-17i)。
/// RetickAllは各コレクションを新しいListインスタンスへ差し替えるため、
/// 旧インスタンスへの参照を保持して戻すだけで完全に復元できる。</summary>
public sealed class RetickBackup
{
    private sealed record TabBackup(
        List<long>[] Notes, List<FreezeNote>[] Freezes,
        List<ValueEvent> Speeds, List<ValueEvent> Boosts);

    private readonly List<TabBackup> _tabs = [];
    private List<Marker> _markers = [];

    public static RetickBackup Capture(ChartProject p)
    {
        var b = new RetickBackup { _markers = p.Markers };
        foreach (var tab in p.Tabs)
            b._tabs.Add(new TabBackup(
                tab.Lanes.Select(l => l.Notes).ToArray(),
                tab.Lanes.Select(l => l.Freezes).ToArray(),
                tab.SpeedEvents, tab.BoostEvents));
        return b;
    }

    public void Restore(ChartProject p)
    {
        for (int ti = 0; ti < p.Tabs.Count && ti < _tabs.Count; ti++)
        {
            var tab = p.Tabs[ti];
            var b = _tabs[ti];
            for (int li = 0; li < tab.Lanes.Count && li < b.Notes.Length; li++)
            {
                tab.Lanes[li].Notes = b.Notes[li];
                tab.Lanes[li].Freezes = b.Freezes[li];
            }
            tab.SpeedEvents = b.Speeds;
            tab.BoostEvents = b.Boosts;
        }
        p.Markers = _markers;
    }
}

/// <summary>
/// フレーム情報モード中のアクションラッパ(2026-07-17i)。EditorDocument.Executeが自動で適用する。
/// 内側のアクション実行後、BPM構成が変化していたら全オブジェクトをスナップショットから
/// 逆算再配置(retick)し、内側アクションと合わせて1つのUndo単位にする。
/// BPM以外の編集の場合はスナップショットを取り直す。
/// モードOFF後にUndo/Redoされても正しく動く(retickの巻き戻しは旧コレクション復元、
/// スナップショット更新はモード継続中のみ行う)ため、モード切替を跨いだUndoが成立する。
/// </summary>
public sealed class FrameModeAction(IEditAction inner, FrameEditState state) : IEditAction
{
    private RetickBackup? _retickBackup;
    private bool _decided;
    private bool _bpmChanged;

    public string Label => inner.Label + " (フレーム固定)";

    public void Do(EditorDocument doc)
    {
        var bpmBefore = doc.Project.BpmEvents.ToList();
        double startNumberBefore = doc.Project.StartNumber;
        inner.Do(doc);
        if (!_decided)
        {
            _bpmChanged = startNumberBefore != doc.Project.StartNumber
                          || !bpmBefore.SequenceEqual(doc.Project.BpmEvents);
            _decided = true;
        }

        if (_bpmChanged)
            _retickBackup = state.RetickAll(doc); // スナップショットは不変(誤差非累積の要)
        else if (ReferenceEquals(doc.FrameEdit, state))
            state.Rebuild(doc); // オブジェクト編集: フレームの現在値を新たな真とする
    }

    public void Undo(EditorDocument doc)
    {
        _retickBackup?.Restore(doc.Project);
        _retickBackup = null;
        inner.Undo(doc);
        if (!_bpmChanged && ReferenceEquals(doc.FrameEdit, state))
            state.Rebuild(doc); // オブジェクト編集の巻き戻しに合わせてスナップショットも戻す
    }
}

/// <summary>フレーム情報モード終了時の重複統合(1Undoアクション、2026-07-17i)。
/// ノート/フリーズ/speed/boost/マーカーの同一tick重複を1件に統合する。</summary>
public sealed class MergeDuplicateTicksAction : IEditAction
{
    private RetickBackup? _backup;

    public string Label => "重複オブジェクトの統合";

    public void Do(EditorDocument doc)
    {
        var p = doc.Project;
        _backup = RetickBackup.Capture(p);
        foreach (var tab in p.Tabs)
        {
            foreach (var lane in tab.Lanes)
            {
                lane.Notes = lane.Notes.Distinct().ToList();
                lane.Freezes = lane.Freezes.GroupBy(f => f.StartTick).Select(g => g.First()).ToList();
            }
            tab.SpeedEvents = tab.SpeedEvents.GroupBy(e => e.Tick).Select(g => g.First()).ToList();
            tab.BoostEvents = tab.BoostEvents.GroupBy(e => e.Tick).Select(g => g.First()).ToList();
        }
        p.Markers = p.Markers.GroupBy(m => m.Tick).Select(g => g.First()).ToList();
    }

    public void Undo(EditorDocument doc)
    {
        _backup?.Restore(doc.Project);
        _backup = null;
    }
}
