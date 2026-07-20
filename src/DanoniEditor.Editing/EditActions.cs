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
/// (実体を消したのに色だけ残る=別tickに幽霊の色指定が残る事故を防ぐ)。</summary>
public sealed class DeleteNoteAction(int lane, long tick) : IEditAction
{
    private NColorEntry? _removedColor;

    public string Label => "ノート削除";

    public void Do(EditorDocument doc)
    {
        doc.CurrentTab.Lanes[lane].Notes.Remove(tick);
        var colors = doc.CurrentTab.Lanes[lane].ColorOverrides;
        _removedColor = colors.FirstOrDefault(c => c.Tick == tick);
        if (_removedColor is not null) colors.Remove(_removedColor);
    }

    public void Undo(EditorDocument doc)
    {
        doc.CurrentTab.Lanes[lane].Notes.Add(tick);
        if (_removedColor is not null) doc.CurrentTab.Lanes[lane].ColorOverrides.Add(_removedColor);
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
/// 2026-07-23: DeleteNoteActionと同様、ColorOverrides(端点色・帯色)も一緒に削除・Undo復元する。</summary>
public sealed class DeleteFreezeAction(int lane, long startTick) : IEditAction
{
    private FreezeNote? _removed;
    private NColorEntry? _removedColor;

    public string Label => "フリーズ削除";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes[lane];
        _removed = lanes.Freezes.FirstOrDefault(f => f.StartTick == startTick);
        if (_removed is not null) lanes.Freezes.Remove(_removed);
        _removedColor = lanes.ColorOverrides.FirstOrDefault(c => c.Tick == startTick);
        if (_removedColor is not null) lanes.ColorOverrides.Remove(_removedColor);
    }

    public void Undo(EditorDocument doc)
    {
        if (_removed is not null) doc.CurrentTab.Lanes[lane].Freezes.Add(_removed);
        if (_removedColor is not null) doc.CurrentTab.Lanes[lane].ColorOverrides.Add(_removedColor);
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
    }

    public void Undo(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes[_lane];
        lanes.Freezes.Remove(_newFreeze);
        lanes.Freezes.Add(_oldFreeze);
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
                    MoveColorEntry(tab, r.Lane, r.Tick, newLane, newTick);
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
                    MoveColorEntry(tab, r.Lane, f.StartTick, newLane, moved.StartTick);
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
                MoveColorEntry(tab, from.Lane, from.Tick, to.Lane, to.Tick);
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
                    MoveColorEntry(tab, from.Lane, from.Tick, to.Lane, to.Tick);
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
            default:
                return false;
        }
    }

    /// <summary>色編集モードで指定したColorOverridesエントリを、ノート/フリーズ本体の移動に追随させる
    /// (2026-07-23)。無ければ何もしない(色未指定のオブジェクトの移動では発生しない)。</summary>
    private static void MoveColorEntry(DifficultyTab tab, int fromLane, long fromTick, int toLane, long toTick)
    {
        var src = tab.Lanes[fromLane].ColorOverrides;
        var entry = src.FirstOrDefault(c => c.Tick == fromTick);
        if (entry is null) return;
        src.Remove(entry);
        tab.Lanes[toLane].ColorOverrides.Add(entry with { Tick = toTick });
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
