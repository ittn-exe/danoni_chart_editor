using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>speed_data/boost_data/BPM変化点で共通に扱うための種別タグ(仕様書9章・7.3)</summary>
public enum ValueEventKind { Speed, Boost, Bpm }

/// <summary>フリーズアローの範囲バリデーション共通ルール(仕様書6.3.1 リサイズ制約)</summary>
public static class FreezeRules
{
    /// <summary>start&lt;end・0以上を保証する(仕様書6.3.1 ドラッグリサイズのクランプ)</summary>
    public static (long Start, long End) ClampResize(long start, long end)
    {
        if (start < 0) start = 0;
        if (end <= start) end = start + 1;
        return (start, end);
    }
}

// =====================================================================
// ノート
// =====================================================================

/// <summary>通常ノートの配置(仕様書6.3.1: 空レーンクリック)</summary>
public sealed class PlaceNoteAction(int lane, long tick) : IEditAction
{
    public string Label => "ノート配置";
    public void Do(EditorDocument doc) => doc.CurrentTab.Lanes[lane].Notes.Add(tick);
    public void Undo(EditorDocument doc) => doc.CurrentTab.Lanes[lane].Notes.Remove(tick);
}

/// <summary>通常ノートの削除(仕様書6.3.1: オブジェクト右クリック)。
/// 2026-07-23: 色編集モードで指定した色(ColorOverrides)も一緒に削除・Undoで復元する
/// (実体を消したのに色だけ残る=別tickに幽霊の色指定が残る事故を防ぐ)。
/// 2026-07-26: コメント・警告(Annotations)も同様に一緒に削除・Undo復元する。</summary>
public sealed class DeleteNoteAction(int lane, long tick) : IEditAction
{
    private NColorEntry? _removedColor;
    private NoteAnnotation? _removedAnnotation;

    public string Label => "ノート削除";

    public void Do(EditorDocument doc)
    {
        doc.CurrentTab.Lanes[lane].Notes.Remove(tick);
        var colors = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _removedColor = colors.FirstOrDefault(c => c.Tick == tick);
        if (_removedColor is not null) colors.Remove(_removedColor);
        var annotations = doc.CurrentTab.Lanes[lane].Annotations;
        _removedAnnotation = annotations.FirstOrDefault(a => a.Tick == tick);
        if (_removedAnnotation is not null) annotations.Remove(_removedAnnotation);
    }

    public void Undo(EditorDocument doc)
    {
        doc.CurrentTab.Lanes[lane].Notes.Add(tick);
        if (_removedColor is not null) doc.CurrentTab.Lanes[lane].ColorOverrides.Add(_removedColor);
        if (_removedAnnotation is not null) doc.CurrentTab.Lanes[lane].Annotations.Add(_removedAnnotation);
    }
}

// =====================================================================
// フリーズアロー
// =====================================================================

/// <summary>フリーズアローの配置(仕様書6.3.1: Shift+クリック、既定長=現在スナップ間隔)</summary>
public sealed class PlaceFreezeAction : IEditAction
{
    private readonly int _lane;
    private readonly FreezeNote _freeze;

    public PlaceFreezeAction(int lane, long startTick, long endTick)
    {
        var (s, e) = FreezeRules.ClampResize(startTick, endTick);
        _lane = lane;
        _freeze = new FreezeNote(s, e);
    }

    public string Label => "フリーズ配置";
    public void Do(EditorDocument doc) => doc.CurrentTab.Lanes[_lane].Freezes.Add(_freeze);
    public void Undo(EditorDocument doc) => doc.CurrentTab.Lanes[_lane].Freezes.Remove(_freeze);
}

/// <summary>フリーズアローの削除(始点tickで同定、仕様書6.3.1)。
/// 2026-07-23: DeleteNoteActionと同様、ColorOverrides(端点色・帯色)も一緒に削除・Undo復元する。
/// 2026-07-26: コメント・警告(Annotations)も同様に一緒に削除・Undo復元する。</summary>
public sealed class DeleteFreezeAction(int lane, long startTick) : IEditAction
{
    private FreezeNote? _removed;
    private NColorEntry? _removedColor;
    private NoteAnnotation? _removedAnnotation;

    public string Label => "フリーズ削除";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes[lane];
        _removed = lanes.Freezes.FirstOrDefault(f => f.StartTick == startTick);
        if (_removed is not null) lanes.Freezes.Remove(_removed);
        _removedColor = lanes.ColorOverrides.FirstOrDefault(c => c.Tick == startTick);
        if (_removedColor is not null) lanes.ColorOverrides.Remove(_removedColor);
        _removedAnnotation = lanes.Annotations.FirstOrDefault(a => a.Tick == startTick);
        if (_removedAnnotation is not null) lanes.Annotations.Remove(_removedAnnotation);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.CurrentTab.Lanes[lane].Freezes.Add(_removed);
        if (_removedColor is not null) doc.CurrentTab.Lanes[lane].ColorOverrides.Add(_removedColor);
        if (_removedAnnotation is not null) doc.CurrentTab.Lanes[lane].Annotations.Add(_removedAnnotation);
    }
}

/// <summary>フリーズアローの端点ドラッグリサイズ(仕様書6.3.1: start&lt;end・lane内に収める)</summary>
public sealed class ResizeFreezeAction : IEditAction
{
    private readonly int _lane;
    private readonly FreezeNote _oldFreeze;
    private readonly FreezeNote _newFreeze;

    public ResizeFreezeAction(int lane, FreezeNote oldFreeze, long newStart, long newEnd)
    {
        _lane = lane;
        _oldFreeze = oldFreeze;
        var (s, e) = FreezeRules.ClampResize(newStart, newEnd);
        _newFreeze = new FreezeNote(s, e);
    }

    public string Label => "フリーズリサイズ";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes[_lane];
        lanes.Freezes.Remove(_oldFreeze);
        lanes.Freezes.Add(_newFreeze);
        // 2026-07-26: 始点tickが変わる場合、StartTickで同定しているサイドカー(色指定・コメント警告)も
        // 追随させる(従来は色指定が旧StartTickに取り残される潜在バグがあった)。
        if (_oldFreeze.StartTick != _newFreeze.StartTick)
            MoveObjectsAction.MoveSidecarEntriesForResize(doc.CurrentTab, _lane, _oldFreeze.StartTick, _newFreeze.StartTick);
    }

    public void Undo(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes[_lane];
        lanes.Freezes.Remove(_newFreeze);
        lanes.Freezes.Add(_oldFreeze);
        if (_oldFreeze.StartTick != _newFreeze.StartTick)
            MoveObjectsAction.MoveSidecarEntriesForResize(doc.CurrentTab, _lane, _newFreeze.StartTick, _oldFreeze.StartTick);
    }
}

// =====================================================================
// コメント・警告(Annotations、2026-07-26)
// =====================================================================

/// <summary>ノート/フリーズのコメント・警告フラグを設定する(2026-07-26、③オブジェクトタブから編集)。
/// 対象はlane+tick(フリーズはStartTick)で同定。Comment=""かつWarning=falseになった場合は
/// エントリ自体を削除する(空エントリを残さない規約、ChartProject.NoteAnnotation参照)。</summary>
public sealed class SetAnnotationAction(int lane, long tick, string comment, bool warning) : IEditAction
{
    private NoteAnnotation? _before;

    public string Label => "コメント・警告編集";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].Annotations;
        _before = list.FirstOrDefault(a => a.Tick == tick);
        list.RemoveAll(a => a.Tick == tick);
        if (comment.Length > 0 || warning) list.Add(new NoteAnnotation(tick, comment, warning));
    }

    public void Undo(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].Annotations;
        list.RemoveAll(a => a.Tick == tick);
        if (_before is not null) list.Add(_before);
    }
}

// =====================================================================
// 色編集モード(ncolor_data、2026-07-23)
// =====================================================================

/// <summary>NColorEntryの一部フィールドだけを書き換えつつ、他の全フィールドは_beforeの値を保持した
/// 新しいエントリを組み立てる(2026-07-24、Shadow/Hit系フィールド追加に伴う共通ヘルパー)。
/// set*=falseの項目は_beforeの値をそのまま引き継ぐ(allFlagのみnull=保持、値ありで上書き)。</summary>
internal static class NColorEntryMerge
{
    public static NColorEntry Merge(NColorEntry? before, long tick,
        string? color = null, bool setColor = false,
        string? band = null, bool setBand = false,
        string? shadow = null, bool setShadow = false,
        string? hit = null, bool setHit = false,
        string? hitBar = null, bool setHitBar = false,
        string? hitShadow = null, bool setHitShadow = false,
        bool? allFlag = null) =>
        new(tick,
            setColor ? color : before?.Color,
            setBand ? band : before?.BandColor,
            allFlag ?? before?.AllFlag ?? false,
            setShadow ? shadow : before?.ShadowColor,
            setHit ? hit : before?.HitColor,
            setHitBar ? hitBar : before?.HitBarColor,
            setHitShadow ? hitShadow : before?.HitShadowColor);

    /// <summary>全フィールドがnull/falseかどうか(エントリ自体を削除してよいかの判定に使う)</summary>
    public static bool IsEmpty(NColorEntry e) =>
        e.Color is null && e.BandColor is null && e.ShadowColor is null &&
        e.HitColor is null && e.HitBarColor is null && e.HitShadowColor is null;
}

/// <summary>NColorEntryをスナップショット(ClipboardColor、全フィールド分)から丸ごと1件追加する
/// (2026-07-26、Ctrl+C/V・Ctrl+ドラッグ複製でColorOverridesを保持したままコピーするために新設)。
/// SetNoteColorAction等の個別フィールド更新とは異なり、「元のエントリの値をそのまま複製先へ再現する」
/// 専用の単純な追加/削除ペア。対象位置(lane+tick)に既存エントリが無い前提(コピペ/複製の貼り付け先は
/// 常に空セルであることが呼び出し元で保証されている)。</summary>
public sealed class AddColorOverrideAction(int lane, long tick, ClipboardColor color) : IEditAction
{
    public string Label => "色情報の複製";

    public void Do(EditorDocument doc) => doc.CurrentTab.Lanes[lane].ColorOverrides.Add(
        new NColorEntry(tick, color.Color, color.BandColor, color.AllFlag,
            color.ShadowColor, color.HitColor, color.HitBarColor, color.HitShadowColor));

    public void Undo(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        var e = list.FirstOrDefault(x => x.Tick == tick);
        if (e is not null) list.Remove(e);
    }
}

/// <summary>ノート/フリーズへ色を設定する(色編集モード「通常」サブモードの左クリック/Shift+クリック/
/// ホイールクリック)。通常ノートはsetColor=true・setBand=falseで固定(BandColorは常にnull)。
/// フリーズは端点(Normal)と帯(NormalBar)を独立に指定でき、Shift/ホイールクリック時はsetColor・
/// setBand両方trueで同一値を渡す。Shadow/Hit系フィールドは変更しない(既存値を保持)。</summary>
public sealed class SetNoteColorAction(int lane, long tick, string value, bool setColor, bool setBand, bool allFlag = false) : IEditAction
{
    private NColorEntry? _before;

    public string Label => "色指定";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _before = list.FirstOrDefault(e => e.Tick == tick);
        list.RemoveAll(e => e.Tick == tick);
        list.Add(NColorEntryMerge.Merge(_before, tick, value, setColor, value, setBand, allFlag: allFlag));
    }

    public void Undo(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        list.RemoveAll(e => e.Tick == tick);
        if (_before is not null) list.Add(_before);
    }
}

/// <summary>ノート/フリーズの色指定を解除する(色編集モード「通常」サブモードの右クリック/選択中
/// Deleteキー)。resetColor/resetBandで指定した部位のみクリアする。Shadow/Hit系フィールドが
/// 残っていればエントリ自体は削除しない(それらも0件になった場合のみエントリを削除)。</summary>
public sealed class ResetNoteColorAction(int lane, long tick, bool resetColor, bool resetBand) : IEditAction
{
    private NColorEntry? _before;

    public string Label => "色指定解除";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _before = list.FirstOrDefault(e => e.Tick == tick);
        if (_before is null) return;
        list.Remove(_before);
        var merged = new NColorEntry(tick,
            resetColor ? null : _before.Color,
            resetBand ? null : _before.BandColor,
            _before.AllFlag, _before.ShadowColor, _before.HitColor, _before.HitBarColor, _before.HitShadowColor);
        if (!NColorEntryMerge.IsEmpty(merged)) list.Add(merged);
    }

    public void Undo(EditorDocument doc)
    {
        if (_before is null) return;
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        list.RemoveAll(e => e.Tick == tick);
        list.Add(_before);
    }
}

/// <summary>ノート/フリーズの塗りつぶし色(ArrowShadow/NormalShadow)を設定する(2026-07-24、
/// ShadowColor編集モード)。ShadowColorフィールド1つを通常ノート・フリーズ共通で使う
/// (dos.txt出力時に対象実体の種別でArrowShadow/NormalShadowへ振り分ける、DosExporter側の責務)。</summary>
public sealed class SetShadowColorAction(int lane, long tick, string value) : IEditAction
{
    private NColorEntry? _before;

    public string Label => "塗りつぶし色指定";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _before = list.FirstOrDefault(e => e.Tick == tick);
        list.RemoveAll(e => e.Tick == tick);
        list.Add(NColorEntryMerge.Merge(_before, tick, shadow: value, setShadow: true));
    }

    public void Undo(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        list.RemoveAll(e => e.Tick == tick);
        if (_before is not null) list.Add(_before);
    }
}

/// <summary>フリーズアローのヒット時(判定中)色を設定する(2026-07-24、frzHitColor編集モード)。
/// Hit/HitBar/HitShadowのうちチェックが入っている項目だけをまとめて1アクションで設定する
/// (ユーザー確定仕様: 1クリックでチェック済み項目を全て同時に塗る)。</summary>
public sealed class SetFrzHitColorsAction(int lane, long tick,
    bool setHit, string? hitValue, bool setHitBar, string? hitBarValue, bool setHitShadow, string? hitShadowValue) : IEditAction
{
    private NColorEntry? _before;

    public string Label => "ヒット時色指定";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _before = list.FirstOrDefault(e => e.Tick == tick);
        list.RemoveAll(e => e.Tick == tick);
        list.Add(NColorEntryMerge.Merge(_before, tick,
            hit: hitValue, setHit: setHit,
            hitBar: hitBarValue, setHitBar: setHitBar,
            hitShadow: hitShadowValue, setHitShadow: setHitShadow));
    }

    public void Undo(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].ColorOverrides;
        list.RemoveAll(e => e.Tick == tick);
        if (_before is not null) list.Add(_before);
    }
}

/// <summary>現在のタブの全ncolor_data指定を削除する(右パネル「ncolor_dataを全て削除」ボタン)。</summary>
public sealed class ClearAllNoteColorsAction : IEditAction
{
    private List<(int Lane, List<NColorEntry> Saved)>? _before;

    public string Label => "色指定全削除";

    public void Do(EditorDocument doc)
    {
        var tab = doc.CurrentTab;
        _before = [];
        for (int i = 0; i < tab.Lanes.Count; i++)
        {
            if (tab.Lanes[i].ColorOverrides.Count == 0) continue;
            _before.Add((i, [.. tab.Lanes[i].ColorOverrides]));
            tab.Lanes[i].ColorOverrides.Clear();
        }
    }

    public void Undo(EditorDocument doc)
    {
        if (_before is null) return;
        var tab = doc.CurrentTab;
        foreach (var (laneIdx, saved) in _before)
            tab.Lanes[laneIdx].ColorOverrides = [.. saved];
    }
}

// =====================================================================
// speed / boost / BPM(値イベント、仕様書7.3/9章)
// =====================================================================

/// <summary>speed/boost/BPMイベントの配置。BPMのtick0は不変条件のため配置不可(TimingEngine前提)。</summary>
public sealed class PlaceValueEventAction(ValueEventKind kind, long tick, double value) : IEditAction
{
    public string Label => kind switch
    {
        ValueEventKind.Speed => "速度変更配置",
        ValueEventKind.Boost => "ブースト変更配置",
        _ => "BPM変更配置",
    };

    public void Do(EditorDocument doc)
    {
        switch (kind)
        {
            case ValueEventKind.Speed:
                doc.CurrentTab.SpeedEvents.Add(new ValueEvent(tick, value));
                break;
            case ValueEventKind.Boost:
                doc.CurrentTab.BoostEvents.Add(new ValueEvent(tick, value));
                break;
            case ValueEventKind.Bpm:
                if (tick == 0) throw new InvalidOperationException("tick0のBPMイベントは変更できません(エンジン不変条件)");
                doc.Project.BpmEvents.Add(new BpmEvent(tick, value));
                break;
        }
    }

    public void Undo(EditorDocument doc)
    {
        switch (kind)
        {
            case ValueEventKind.Speed:
                doc.CurrentTab.SpeedEvents.RemoveAll(e => e.Tick == tick && e.Value == value);
                break;
            case ValueEventKind.Boost:
                doc.CurrentTab.BoostEvents.RemoveAll(e => e.Tick == tick && e.Value == value);
                break;
            case ValueEventKind.Bpm:
                doc.Project.BpmEvents.RemoveAll(e => e.Tick == tick && e.Bpm == value);
                break;
        }
    }
}

/// <summary>speed/boost/BPMイベントの削除。tick0のBPMは削除不可(不変条件)。</summary>
public sealed class DeleteValueEventAction(ValueEventKind kind, long tick) : IEditAction
{
    private double? _removedValue;

    public string Label => kind switch
    {
        ValueEventKind.Speed => "速度変更削除",
        ValueEventKind.Boost => "ブースト変更削除",
        _ => "BPM変更削除",
    };

    public void Do(EditorDocument doc)
    {
        if (kind == ValueEventKind.Bpm && tick == 0)
            throw new InvalidOperationException("tick0のBPMイベントは削除できません(エンジン不変条件)");

        switch (kind)
        {
            case ValueEventKind.Speed:
                {
                    var e = doc.CurrentTab.SpeedEvents.FirstOrDefault(x => x.Tick == tick);
                    if (e is not null) { _removedValue = e.Value; doc.CurrentTab.SpeedEvents.Remove(e); }
                    break;
                }
            case ValueEventKind.Boost:
                {
                    var e = doc.CurrentTab.BoostEvents.FirstOrDefault(x => x.Tick == tick);
                    if (e is not null) { _removedValue = e.Value; doc.CurrentTab.BoostEvents.Remove(e); }
                    break;
                }
            case ValueEventKind.Bpm:
                {
                    var e = doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == tick);
                    if (e is not null) { _removedValue = e.Bpm; doc.Project.BpmEvents.Remove(e); }
                    break;
                }
        }
    }

    public void Undo(EditorDocument doc)
    {
        if (_removedValue is not { } v) return;
        switch (kind)
        {
            case ValueEventKind.Speed: doc.CurrentTab.SpeedEvents.Add(new ValueEvent(tick, v)); break;
            case ValueEventKind.Boost: doc.CurrentTab.BoostEvents.Add(new ValueEvent(tick, v)); break;
            case ValueEventKind.Bpm: doc.Project.BpmEvents.Add(new BpmEvent(tick, v)); break;
        }
    }
}

/// <summary>speed/boost/BPMイベントの左ドラッグ移動(単体)。tick0のBPMは対象外(不変条件)。</summary>
public sealed class MoveValueEventAction(ValueEventKind kind, long oldTick, long newTick) : IEditAction
{
    private double _value;

    public string Label => kind switch
    {
        ValueEventKind.Speed => "速度変更移動",
        ValueEventKind.Boost => "ブースト変更移動",
        _ => "BPM変更移動",
    };

    public void Do(EditorDocument doc)
    {
        if (kind == ValueEventKind.Bpm && (oldTick == 0 || newTick == 0))
            throw new InvalidOperationException("tick0のBPMイベントは移動できません(エンジン不変条件)");

        switch (kind)
        {
            case ValueEventKind.Speed:
                {
                    var e = doc.CurrentTab.SpeedEvents.First(x => x.Tick == oldTick);
                    _value = e.Value;
                    doc.CurrentTab.SpeedEvents.Remove(e);
                    doc.CurrentTab.SpeedEvents.Add(new ValueEvent(newTick, _value));
                    break;
                }
            case ValueEventKind.Boost:
                {
                    var e = doc.CurrentTab.BoostEvents.First(x => x.Tick == oldTick);
                    _value = e.Value;
                    doc.CurrentTab.BoostEvents.Remove(e);
                    doc.CurrentTab.BoostEvents.Add(new ValueEvent(newTick, _value));
                    break;
                }
            case ValueEventKind.Bpm:
                {
                    var e = doc.Project.BpmEvents.First(x => x.Tick == oldTick);
                    _value = e.Bpm;
                    doc.Project.BpmEvents.Remove(e);
                    doc.Project.BpmEvents.Add(new BpmEvent(newTick, _value));
                    break;
                }
        }
    }

    public void Undo(EditorDocument doc)
    {
        switch (kind)
        {
            case ValueEventKind.Speed:
                doc.CurrentTab.SpeedEvents.RemoveAll(x => x.Tick == newTick && x.Value == _value);
                doc.CurrentTab.SpeedEvents.Add(new ValueEvent(oldTick, _value));
                break;
            case ValueEventKind.Boost:
                doc.CurrentTab.BoostEvents.RemoveAll(x => x.Tick == newTick && x.Value == _value);
                doc.CurrentTab.BoostEvents.Add(new ValueEvent(oldTick, _value));
                break;
            case ValueEventKind.Bpm:
                doc.Project.BpmEvents.RemoveAll(x => x.Tick == newTick && x.Bpm == _value);
                doc.Project.BpmEvents.Add(new BpmEvent(oldTick, _value));
                break;
        }
    }
}

// =====================================================================
// マーカー(仕様書7.4)
// =====================================================================

public sealed class PlaceMarkerAction(long tick, string comment = "") : IEditAction
{
    public string Label => "マーカー配置";
    public void Do(EditorDocument doc) => doc.Project.Markers.Add(new Marker(tick, comment));
    public void Undo(EditorDocument doc) => doc.Project.Markers.RemoveAll(m => m.Tick == tick && m.Comment == comment);
}

public sealed class MoveMarkerAction(long oldTick, long newTick) : IEditAction
{
    private string _comment = "";

    public string Label => "マーカー移動";

    public void Do(EditorDocument doc)
    {
        var m = doc.Project.Markers.First(x => x.Tick == oldTick);
        _comment = m.Comment;
        doc.Project.Markers.Remove(m);
        doc.Project.Markers.Add(new Marker(newTick, _comment));
    }

    public void Undo(EditorDocument doc)
    {
        doc.Project.Markers.RemoveAll(m => m.Tick == newTick && m.Comment == _comment);
        doc.Project.Markers.Add(new Marker(oldTick, _comment));
    }
}

public sealed class DeleteMarkerAction(long tick) : IEditAction
{
    private Marker? _removed;

    public string Label => "マーカー削除";

    public void Do(EditorDocument doc)
    {
        _removed = doc.Project.Markers.FirstOrDefault(m => m.Tick == tick);
        if (_removed is not null) doc.Project.Markers.Remove(_removed);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.Project.Markers.Add(_removed);
    }
}

// =====================================================================
// 歌詞表示(word_data、2026-07-23、TBD 4)
// =====================================================================

/// <summary>歌詞レーンへの新規配置(既定値: Position=0, Kind=Lyrics, Text="")。
/// 詳細(Position/種別/本文/FadeFrame)は右パネルでEditWordEntryActionにより編集する想定。</summary>
public sealed class PlaceWordEntryAction(int laneIndex, long tick) : IEditAction
{
    public string Label => "歌詞配置";
    public void Do(EditorDocument doc) =>
        doc.CurrentTab.WordLanes[laneIndex].Entries.Add(new WordEntry(tick, 0, WordEntryKind.Lyrics, ""));
    public void Undo(EditorDocument doc) =>
        doc.CurrentTab.WordLanes[laneIndex].Entries.RemoveAll(e => e.Tick == tick);
}

public sealed class DeleteWordEntryAction(int laneIndex, long tick) : IEditAction
{
    private WordEntry? _removed;

    public string Label => "歌詞削除";

    public void Do(EditorDocument doc)
    {
        var lane = doc.CurrentTab.WordLanes[laneIndex];
        _removed = lane.Entries.FirstOrDefault(e => e.Tick == tick);
        if (_removed is not null) lane.Entries.Remove(_removed);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.CurrentTab.WordLanes[laneIndex].Entries.Add(_removed);
    }
}

/// <summary>歌詞エントリのプロパティ編集(Position/種別/本文/FadeFrame、右パネルから呼ぶ)。
/// tickは変更しない(位置移動はMoveObjectsAction、既存のドラッグ移動と共通の仕組みを使う)。</summary>
public sealed class EditWordEntryAction(int laneIndex, long tick, WordEntry newEntry) : IEditAction
{
    private WordEntry? _old;

    public string Label => "歌詞編集";

    public void Do(EditorDocument doc)
    {
        var lane = doc.CurrentTab.WordLanes[laneIndex];
        _old = lane.Entries.FirstOrDefault(e => e.Tick == tick);
        if (_old is not null) lane.Entries.Remove(_old);
        lane.Entries.Add(newEntry);
    }

    public void Undo(EditorDocument doc)
    {
        var lane = doc.CurrentTab.WordLanes[laneIndex];
        lane.Entries.RemoveAll(e => e.Tick == newEntry.Tick);
        if (_old is not null) lane.Entries.Add(_old);
    }
}

/// <summary>歌詞レーンの追加(WordLaneManagerWindow「+ 歌詞レーンを追加」、2026-07-26)。末尾に1本追加する。</summary>
public sealed class AddWordLaneAction(string name) : IEditAction
{
    private int _addedIndex = -1;

    public string Label => "歌詞レーン追加";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.WordLanes;
        _addedIndex = lanes.Count;
        lanes.Add(new WordLane { Name = name });
    }

    public void Undo(EditorDocument doc)
    {
        if (_addedIndex < 0) return;
        doc.CurrentTab.WordLanes.RemoveAt(_addedIndex);
        doc.Selection.RemoveWhere(s => s.Kind == ObjectKind.Word);
    }
}

/// <summary>歌詞レーンの削除(WordLaneManagerWindow、2026-07-26)。削除時点のレーン内容(歌詞エントリを
/// 含む全体)をそのまま保持し、Undoで元のindexへ丸ごと復元する(=削除操作そのものを取り消す)。
/// 後続レーンのindexが詰まる/戻る関係上、Do・Undoいずれの直後もWord系の選択状態はクリアする
/// (削除前後で他の歌詞エントリのindex対応が変わり得るため、選択の連続性までは保証しない)。</summary>
public sealed class DeleteWordLaneAction(int index) : IEditAction
{
    private WordLane? _removed;

    public string Label => "歌詞レーン削除";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.WordLanes;
        _removed = lanes[index];
        lanes.RemoveAt(index);
        doc.Selection.RemoveWhere(s => s.Kind == ObjectKind.Word);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is null) return;
        doc.CurrentTab.WordLanes.Insert(index, _removed);
        doc.Selection.RemoveWhere(s => s.Kind == ObjectKind.Word);
    }
}

/// <summary>歌詞レーンの名前変更(WordLaneManagerWindow、2026-07-26)。</summary>
public sealed class RenameWordLaneAction(int index, string newName) : IEditAction
{
    private string _old = "";

    public string Label => "歌詞レーン名変更";

    public void Do(EditorDocument doc)
    {
        var lane = doc.CurrentTab.WordLanes[index];
        _old = lane.Name;
        lane.Name = newName;
    }

    public void Undo(EditorDocument doc) => doc.CurrentTab.WordLanes[index].Name = _old;
}

/// <summary>歌詞レーンのReverse専用フラグ切替(WordLaneManagerWindow、2026-07-26)。</summary>
public sealed class SetWordLaneReverseAction(int index, bool value) : IEditAction
{
    private bool _old;

    public string Label => "歌詞レーンReverse切替";

    public void Do(EditorDocument doc)
    {
        var lane = doc.CurrentTab.WordLanes[index];
        _old = lane.IsReverse;
        lane.IsReverse = value;
    }

    public void Undo(EditorDocument doc) => doc.CurrentTab.WordLanes[index].IsReverse = _old;
}

// =====================================================================
// 拍子(仕様書7.5: 物理小節頭にのみ配置可能)
// =====================================================================

/// <summary>拍子オブジェクトの配置(既存の同一小節への上書きも兼ねる)</summary>
public sealed class PlaceTimeSignatureAction(int measureIndex, int numerator, int denominator) : IEditAction
{
    private TimeSignatureEvent? _replaced;
    private readonly TimeSignatureEvent _newSig = new(measureIndex, numerator, denominator);

    public string Label => "拍子配置";

    public void Do(EditorDocument doc)
    {
        _replaced = doc.Project.TimeSignatures.FirstOrDefault(s => s.MeasureIndex == measureIndex);
        if (_replaced is not null) doc.Project.TimeSignatures.Remove(_replaced);
        doc.Project.TimeSignatures.Add(_newSig);
    }

    public void Undo(EditorDocument doc)
    {
        doc.Project.TimeSignatures.Remove(_newSig);
        if (_replaced is not null) doc.Project.TimeSignatures.Add(_replaced);
    }
}

public sealed class DeleteTimeSignatureAction(int measureIndex) : IEditAction
{
    private TimeSignatureEvent? _removed;

    public string Label => "拍子削除";

    public void Do(EditorDocument doc)
    {
        _removed = doc.Project.TimeSignatures.FirstOrDefault(s => s.MeasureIndex == measureIndex);
        if (_removed is not null) doc.Project.TimeSignatures.Remove(_removed);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.Project.TimeSignatures.Add(_removed);
    }
}

// =====================================================================
// 選択オブジェクトのグループ移動(仕様書6.3.2)
// =====================================================================

/// <summary>
/// 選択オブジェクト集合のドラッグ移動(仕様書6.3.2: スマートツールON/OFFに関わらず共通)。
/// ノートはlaneDelta+tickDelta、イベント系(speed/boost/BPM/marker)はtickDeltaのみ。
/// tick0のBPMは移動対象から除外(不変条件)。拍子オブジェクトは物理小節頭固定のため本アクションの対象外
/// (小節頭への再配置は個別にPlace/Deleteで行う、仕様書7.5)。
/// Do実行時に実際に適用された「移動前→移動後」の対応を記録し、Undoはその記録をそのまま逆再生する
/// (デルタの再計算をしないことで、レーン端クランプが起きた場合でも正確に戻せる)。
/// EditorDocument.Selectionもここで移動後の参照集合に更新する。
/// </summary>
public sealed class MoveObjectsAction : IEditAction
{
    private readonly record struct MoveRecord(ObjectRef Before, ObjectRef After);

    private readonly IReadOnlyList<ObjectRef> _originalTargets;
    private readonly int _laneDelta;
    private readonly long _tickDelta;
    private List<MoveRecord>? _applied;

    public MoveObjectsAction(IEnumerable<ObjectRef> targets, int laneDelta, long tickDelta)
    {
        _originalTargets = DistinctEntities(targets).ToList();
        _laneDelta = laneDelta;
        _tickDelta = tickDelta;
    }

    public string Label => "グループ移動";

    public void Do(EditorDocument doc)
    {
        int laneCount = doc.CurrentTemplate.KeyCount;
        var records = new List<MoveRecord>();
        foreach (var r in _originalTargets)
        {
            var after = Move(doc, r, _laneDelta, _tickDelta, laneCount);
            if (after is { } a) records.Add(new MoveRecord(r, a));
        }
        _applied = records;
        RefreshSelection(doc, records.Select(x => x.After));
    }

    public void Undo(EditorDocument doc)
    {
        if (_applied is null) return;
        var restored = new List<ObjectRef>();
        foreach (var rec in _applied)
        {
            // After→Beforeへ、実際に記録された絶対位置で正確に戻す(デルタ再適用ではない)
            var back = MoveExact(doc, rec.After, rec.Before);
            if (back) restored.Add(rec.Before);
        }
        RefreshSelection(doc, restored);
    }

    private static void RefreshSelection(EditorDocument doc, IEnumerable<ObjectRef> refs)
    {
        doc.Selection.Clear();
        foreach (var r in refs) doc.Selection.Add(r);
    }

    /// <summary>refで指定されたオブジェクトを(laneDelta,tickDelta)だけ動かす。移動後の新ObjectRefを返す(移動不能ならnull)。</summary>
    private static ObjectRef? Move(EditorDocument doc, ObjectRef r, int laneDelta, long tickDelta, int laneCount)
    {
        var tab = doc.CurrentTab;
        switch (r.Kind)
        {
            case ObjectKind.Note:
                {
                    if (!tab.Lanes[r.Lane].Notes.Remove(r.Tick)) return null;
                    int newLane = Math.Clamp(r.Lane + laneDelta, 0, Math.Max(0, laneCount - 1));
                    long newTick = r.Tick + tickDelta;
                    tab.Lanes[newLane].Notes.Add(newTick);
                    MoveSidecarEntries(tab,r.Lane, r.Tick, newLane, newTick);
                    return new ObjectRef(ObjectKind.Note, newLane, newTick);
                }
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var lane = tab.Lanes[r.Lane];
                    var f = lane.Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                    if (f is null) return null;
                    lane.Freezes.Remove(f);
                    int newLane = Math.Clamp(r.Lane + laneDelta, 0, Math.Max(0, laneCount - 1));
                    var moved = new FreezeNote(f.StartTick + tickDelta, f.EndTick + tickDelta);
                    tab.Lanes[newLane].Freezes.Add(moved);
                    MoveSidecarEntries(tab,r.Lane, f.StartTick, newLane, moved.StartTick);
                    return new ObjectRef(ObjectKind.FreezeStart, newLane, moved.StartTick);
                }
            case ObjectKind.Speed:
                {
                    var e = tab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    tab.SpeedEvents.Remove(e);
                    var moved = new ValueEvent(r.Tick + tickDelta, e.Value);
                    tab.SpeedEvents.Add(moved);
                    return new ObjectRef(ObjectKind.Speed, -1, moved.Tick);
                }
            case ObjectKind.Boost:
                {
                    var e = tab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    tab.BoostEvents.Remove(e);
                    var moved = new ValueEvent(r.Tick + tickDelta, e.Value);
                    tab.BoostEvents.Add(moved);
                    return new ObjectRef(ObjectKind.Boost, -1, moved.Tick);
                }
            case ObjectKind.Bpm:
                {
                    if (r.Tick == 0) return null; // tick0は不変条件により移動対象外
                    var e = doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    var newTick = r.Tick + tickDelta;
                    if (newTick == 0) return null; // tick0への移動も不可
                    doc.Project.BpmEvents.Remove(e);
                    var moved = new BpmEvent(newTick, e.Bpm);
                    doc.Project.BpmEvents.Add(moved);
                    return new ObjectRef(ObjectKind.Bpm, -1, moved.Tick);
                }
            case ObjectKind.Marker:
                {
                    var m = doc.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                    if (m is null) return null;
                    doc.Project.Markers.Remove(m);
                    var moved = new Marker(r.Tick + tickDelta, m.Comment);
                    doc.Project.Markers.Add(moved);
                    return new ObjectRef(ObjectKind.Marker, -1, moved.Tick);
                }
            case ObjectKind.Word:
                {
                    // 2026-07-23(TBD 4): 歌詞はレーン(r.Lane=WordLanesのindex)を跨いだ移動をサポートしない
                    // (laneDeltaは無視、tickのみ移動)。ノートレーンのようにレーン列がキー数固定ではなく
                    // ユーザーが任意本追加するため、隣接レーンへ機械的に移すのは意図しない結果になりやすい。
                    var lane = tab.WordLanes[r.Lane];
                    var w = lane.Entries.FirstOrDefault(x => x.Tick == r.Tick);
                    if (w is null) return null;
                    lane.Entries.Remove(w);
                    var moved = w with { Tick = r.Tick + tickDelta };
                    lane.Entries.Add(moved);
                    return new ObjectRef(ObjectKind.Word, r.Lane, moved.Tick);
                }
            case ObjectKind.TimeSignature:
            default:
                return null; // 拍子は本アクションの対象外(仕様書7.5)
        }
    }

    /// <summary>fromの位置にある実体を、toの絶対位置(lane/tick)へそのまま付け替える(Undo専用、デルタ不使用)</summary>
    private static bool MoveExact(EditorDocument doc, ObjectRef from, ObjectRef to)
    {
        var tab = doc.CurrentTab;
        switch (from.Kind)
        {
            case ObjectKind.Note:
                if (!tab.Lanes[from.Lane].Notes.Remove(from.Tick)) return false;
                tab.Lanes[to.Lane].Notes.Add(to.Tick);
                MoveSidecarEntries(tab,from.Lane, from.Tick, to.Lane, to.Tick);
                return true;
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var lane = tab.Lanes[from.Lane];
                    var f = lane.Freezes.FirstOrDefault(x => x.StartTick == from.Tick);
                    if (f is null) return false;
                    lane.Freezes.Remove(f);
                    long len = f.EndTick - f.StartTick;
                    tab.Lanes[to.Lane].Freezes.Add(new FreezeNote(to.Tick, to.Tick + len));
                    MoveSidecarEntries(tab,from.Lane, from.Tick, to.Lane, to.Tick);
                    return true;
                }
            case ObjectKind.Speed:
                {
                    var e = tab.SpeedEvents.FirstOrDefault(x => x.Tick == from.Tick);
                    if (e is null) return false;
                    tab.SpeedEvents.Remove(e);
                    tab.SpeedEvents.Add(new ValueEvent(to.Tick, e.Value));
                    return true;
                }
            case ObjectKind.Boost:
                {
                    var e = tab.BoostEvents.FirstOrDefault(x => x.Tick == from.Tick);
                    if (e is null) return false;
                    tab.BoostEvents.Remove(e);
                    tab.BoostEvents.Add(new ValueEvent(to.Tick, e.Value));
                    return true;
                }
            case ObjectKind.Bpm:
                {
                    var e = doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == from.Tick);
                    if (e is null) return false;
                    doc.Project.BpmEvents.Remove(e);
                    doc.Project.BpmEvents.Add(new BpmEvent(to.Tick, e.Bpm));
                    return true;
                }
            case ObjectKind.Marker:
                {
                    var m = doc.Project.Markers.FirstOrDefault(x => x.Tick == from.Tick);
                    if (m is null) return false;
                    doc.Project.Markers.Remove(m);
                    doc.Project.Markers.Add(new Marker(to.Tick, m.Comment));
                    return true;
                }
            case ObjectKind.Word:
                {
                    var lane = tab.WordLanes[from.Lane];
                    var w = lane.Entries.FirstOrDefault(x => x.Tick == from.Tick);
                    if (w is null) return false;
                    lane.Entries.Remove(w);
                    tab.WordLanes[to.Lane].Entries.Add(w with { Tick = to.Tick });
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>ResizeFreezeAction用の公開ラッパー(同一レーン内での始点tick変更追随、2026-07-26)</summary>
    internal static void MoveSidecarEntriesForResize(DifficultyTab tab, int lane, long fromTick, long toTick) =>
        MoveSidecarEntries(tab, lane, fromTick, lane, toTick);

    /// <summary>ノート/フリーズ本体に付随するサイドカーエントリ(色指定ColorOverrides=2026-07-23、
    /// コメント・警告Annotations=2026-07-26)を、本体の移動に追随させる。無ければ何もしない。</summary>
    private static void MoveSidecarEntries(DifficultyTab tab, int fromLane, long fromTick, int toLane, long toTick)
    {
        var colors = tab.Lanes[fromLane].ColorOverrides;
        var colorEntry = colors.FirstOrDefault(c => c.Tick == fromTick);
        if (colorEntry is not null)
        {
            colors.Remove(colorEntry);
            tab.Lanes[toLane].ColorOverrides.Add(colorEntry with { Tick = toTick });
        }

        var annotations = tab.Lanes[fromLane].Annotations;
        var annotation = annotations.FirstOrDefault(a => a.Tick == fromTick);
        if (annotation is not null)
        {
            annotations.Remove(annotation);
            tab.Lanes[toLane].Annotations.Add(annotation with { Tick = toTick });
        }
    }

    private static IEnumerable<ObjectRef> DistinctEntities(IEnumerable<ObjectRef> refs)
    {
        var seen = new HashSet<(int Lane, long Tick, int Group)>();
        foreach (var r in refs)
        {
            int group = r.Kind is ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody
                ? 1 : (int)r.Kind + 10;
            if (seen.Add((r.Lane, r.Tick, group))) yield return r;
        }
    }
}

/// <summary>選択中オブジェクトを、指定した位置ずらし(lane/tick)の場所へ複製する(Ctrl+ドラッグ、
/// 2026-07-26要望対応)。MoveObjectsActionと対になる実装だが、対象を元の場所から取り除かず
/// (laneDelta,tickDelta)ずらした新しい実体を追加するだけの点が異なる。2026-07-26: 「frame情報以外は
/// 全て保持してコピペしたい」との要望対応で、通常ノート/フリーズのColorOverrides(ncolor_data個別色)・
/// Annotations(コメント・警告)も複製先へコピーする(CopySidecar参照、Ctrl+C/VのBuildClipboardEntriesと
/// 同じ方針)。複製に成功した新オブジェクト群を選択状態にする。</summary>
public sealed class CopyObjectsAction : IEditAction
{
    private readonly IReadOnlyList<ObjectRef> _originalTargets;
    private readonly int _laneDelta;
    private readonly long _tickDelta;
    private List<ObjectRef>? _created;

    public CopyObjectsAction(IEnumerable<ObjectRef> targets, int laneDelta, long tickDelta)
    {
        var unique = new List<ObjectRef>();
        foreach (var r in targets)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);
        _originalTargets = unique;
        _laneDelta = laneDelta;
        _tickDelta = tickDelta;
    }

    public string Label => "複製";

    public void Do(EditorDocument doc)
    {
        int laneCount = doc.CurrentTemplate.KeyCount;
        var created = new List<ObjectRef>();
        foreach (var r in _originalTargets)
        {
            var c = Create(doc, r, _laneDelta, _tickDelta, laneCount);
            if (c is { } cc) created.Add(cc);
        }
        _created = created;
        doc.Selection.Clear();
        foreach (var c in created) doc.Selection.Add(c);
    }

    public void Undo(EditorDocument doc)
    {
        if (_created is null) return;
        foreach (var c in _created) RemoveExact(doc, c);
        doc.Selection.Clear();
        foreach (var r in _originalTargets) doc.Selection.Add(r);
    }

    /// <summary>rで指定されたオブジェクトの複製を(laneDelta,tickDelta)ずらした位置に作る。
    /// 元のオブジェクトはそのまま残す(MoveObjectsAction.Moveと異なりRemoveしない)。
    /// 作成後の新ObjectRefを返す(元が見つからない/作成不能なら null)。</summary>
    private static ObjectRef? Create(EditorDocument doc, ObjectRef r, int laneDelta, long tickDelta, int laneCount)
    {
        var tab = doc.CurrentTab;
        switch (r.Kind)
        {
            case ObjectKind.Note:
                {
                    if (!tab.Lanes[r.Lane].Notes.Contains(r.Tick)) return null;
                    int newLane = Math.Clamp(r.Lane + laneDelta, 0, Math.Max(0, laneCount - 1));
                    long newTick = r.Tick + tickDelta;
                    if (newTick < 0) return null;
                    tab.Lanes[newLane].Notes.Add(newTick);
                    CopySidecar(tab, r.Lane, r.Tick, newLane, newTick);
                    return new ObjectRef(ObjectKind.Note, newLane, newTick);
                }
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var f = tab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                    if (f is null) return null;
                    int newLane = Math.Clamp(r.Lane + laneDelta, 0, Math.Max(0, laneCount - 1));
                    long newStart = f.StartTick + tickDelta;
                    if (newStart < 0) return null;
                    var created = new FreezeNote(newStart, f.EndTick + tickDelta);
                    tab.Lanes[newLane].Freezes.Add(created);
                    CopySidecar(tab, r.Lane, f.StartTick, newLane, created.StartTick);
                    return new ObjectRef(ObjectKind.FreezeStart, newLane, created.StartTick);
                }
            case ObjectKind.Speed:
                {
                    var e = tab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    long newTick = r.Tick + tickDelta;
                    if (newTick < 0) return null;
                    var created = new ValueEvent(newTick, e.Value);
                    tab.SpeedEvents.Add(created);
                    return new ObjectRef(ObjectKind.Speed, -1, created.Tick);
                }
            case ObjectKind.Boost:
                {
                    var e = tab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    long newTick = r.Tick + tickDelta;
                    if (newTick < 0) return null;
                    var created = new ValueEvent(newTick, e.Value);
                    tab.BoostEvents.Add(created);
                    return new ObjectRef(ObjectKind.Boost, -1, created.Tick);
                }
            case ObjectKind.Bpm:
                {
                    if (r.Tick == 0) return null; // tick0は不変条件(既存イベントが常に存在)
                    var e = doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is null) return null;
                    long newTick = r.Tick + tickDelta;
                    if (newTick <= 0) return null; // tick0への複製も不可(Moveと同じ扱い)
                    var created = new BpmEvent(newTick, e.Bpm);
                    doc.Project.BpmEvents.Add(created);
                    return new ObjectRef(ObjectKind.Bpm, -1, created.Tick);
                }
            case ObjectKind.Marker:
                {
                    var m = doc.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                    if (m is null) return null;
                    long newTick = r.Tick + tickDelta;
                    if (newTick < 0) return null;
                    var created = new Marker(newTick, m.Comment);
                    doc.Project.Markers.Add(created);
                    return new ObjectRef(ObjectKind.Marker, -1, created.Tick);
                }
            case ObjectKind.Word:
                {
                    var lane = tab.WordLanes[r.Lane];
                    var w = lane.Entries.FirstOrDefault(x => x.Tick == r.Tick);
                    if (w is null) return null;
                    long newTick = r.Tick + tickDelta;
                    if (newTick < 0) return null;
                    var created = w with { Tick = newTick };
                    lane.Entries.Add(created);
                    return new ObjectRef(ObjectKind.Word, r.Lane, created.Tick);
                }
            case ObjectKind.TimeSignature:
            default:
                return null; // 拍子は本アクションの対象外(MoveObjectsActionと同じ、仕様書7.5)
        }
    }

    /// <summary>Do()で作成した複製をUndo時に取り除く(絶対位置での厳密削除)</summary>
    private static void RemoveExact(EditorDocument doc, ObjectRef r)
    {
        var tab = doc.CurrentTab;
        switch (r.Kind)
        {
            case ObjectKind.Note:
                tab.Lanes[r.Lane].Notes.Remove(r.Tick);
                RemoveSidecar(tab, r.Lane, r.Tick);
                break;
            case ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody:
                {
                    var f = tab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                    if (f is not null) tab.Lanes[r.Lane].Freezes.Remove(f);
                    RemoveSidecar(tab, r.Lane, r.Tick);
                    break;
                }
            case ObjectKind.Speed:
                {
                    var e = tab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is not null) tab.SpeedEvents.Remove(e);
                    break;
                }
            case ObjectKind.Boost:
                {
                    var e = tab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is not null) tab.BoostEvents.Remove(e);
                    break;
                }
            case ObjectKind.Bpm:
                {
                    var e = doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    if (e is not null) doc.Project.BpmEvents.Remove(e);
                    break;
                }
            case ObjectKind.Marker:
                {
                    var m = doc.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                    if (m is not null) doc.Project.Markers.Remove(m);
                    break;
                }
            case ObjectKind.Word:
                {
                    var lane = tab.WordLanes[r.Lane];
                    var w = lane.Entries.FirstOrDefault(x => x.Tick == r.Tick);
                    if (w is not null) lane.Entries.Remove(w);
                    break;
                }
        }
    }

    /// <summary>ColorOverrides/Annotations(付随データ)を、複製元のtickから複製先のtickへコピーする
    /// (2026-07-26)。同じ趣旨のMoveSidecarEntries(EditActions.cs内、MoveObjectsAction用)と異なり、
    /// 元のエントリは削除しない(複製なので両方に残す)。どちらも無ければ何もしない。</summary>
    private static void CopySidecar(DifficultyTab tab, int fromLane, long fromTick, int toLane, long toTick)
    {
        var color = tab.Lanes[fromLane].ColorOverrides.FirstOrDefault(c => c.Tick == fromTick);
        if (color is not null) tab.Lanes[toLane].ColorOverrides.Add(color with { Tick = toTick });

        var annotation = tab.Lanes[fromLane].Annotations.FirstOrDefault(a => a.Tick == fromTick);
        if (annotation is not null) tab.Lanes[toLane].Annotations.Add(annotation with { Tick = toTick });
    }

    /// <summary>CopySidecarで複製したColorOverrides/Annotationsを、Undo時に取り除く(2026-07-26)。</summary>
    private static void RemoveSidecar(DifficultyTab tab, int lane, long tick)
    {
        var color = tab.Lanes[lane].ColorOverrides.FirstOrDefault(c => c.Tick == tick);
        if (color is not null) tab.Lanes[lane].ColorOverrides.Remove(color);

        var annotation = tab.Lanes[lane].Annotations.FirstOrDefault(a => a.Tick == tick);
        if (annotation is not null) tab.Lanes[lane].Annotations.Remove(annotation);
    }
}

/// <summary>
/// 複数の編集を1ジェスチャ=1Undoアクションにまとめる(仕様書13章)。ドラッグ削除(6.3.1)で使用。
/// Undoは逆順に適用する。
/// </summary>
public sealed class CompositeEditAction(IReadOnlyList<IEditAction> actions, string label) : IEditAction
{
    public string Label => label;

    public void Do(EditorDocument doc)
    {
        foreach (var a in actions) a.Do(doc);
    }

    public void Undo(EditorDocument doc)
    {
        for (int i = actions.Count - 1; i >= 0; i--) actions[i].Undo(doc);
    }
}

/// <summary>
/// レーン入替マクロの適用(仕様書11.1、2026-07-26)。現在の難易度タブの全レーンの
/// ノート配置データ(LaneNotes = Notes/Freezes/ColorOverrides/Annotations一式)を、
/// 順列配列(laneMapping。インデックス=適用後の位置、値=どのレーン位置のデータを持ってくるか)
/// に従って一括で入れ替える。レーンの定義(dataName・keyAssign・colorGroup等、テンプレート由来の
/// 固定情報)は一切変更しない(テンプレート側のLaneDefには触れず、Tabs[].Lanesの並びだけを動かす)。
/// 適用前に全レーンをスナップショットしてから書き込むため、逐次swapによる参照順序バグが起きない。
/// </summary>
public sealed class ApplyLaneSwapMacroAction(IReadOnlyList<int> laneMapping, string macroName) : IEditAction
{
    private List<LaneNotes>? _before;

    public string Label => $"マクロ適用: {macroName}";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes;
        var snapshot = new List<LaneNotes>(lanes);
        _before = snapshot;
        for (int i = 0; i < lanes.Count && i < laneMapping.Count; i++)
        {
            int from = laneMapping[i];
            if (from < 0 || from >= snapshot.Count) continue;
            lanes[i] = snapshot[from];
        }
    }

    public void Undo(EditorDocument doc)
    {
        if (_before is null) return;
        var lanes = doc.CurrentTab.Lanes;
        for (int i = 0; i < lanes.Count && i < _before.Count; i++)
            lanes[i] = _before[i];
    }
}
