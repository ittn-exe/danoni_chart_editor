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

/// <summary>ノート/フリーズのコメント・警告フラグ・コメントお知らせアイコンフラグを設定する
/// (2026-07-26、③オブジェクトタブから編集。showIconは2026-07-30要望対応で追加)。
/// 対象はlane+tick(フリーズはStartTick)で同定。Comment=""かつWarning=falseかつshowIcon=falseに
/// なった場合はエントリ自体を削除する(空エントリを残さない規約、ChartProject.NoteAnnotation参照)。</summary>
public sealed class SetAnnotationAction(int lane, long tick, string comment, bool warning, bool showIcon = false) : IEditAction
{
    private NoteAnnotation? _before;

    public string Label => "コメント・警告編集";

    public void Do(EditorDocument doc)
    {
        var list = doc.CurrentTab.Lanes[lane].Annotations;
        _before = list.FirstOrDefault(a => a.Tick == tick);
        list.RemoveAll(a => a.Tick == tick);
        if (comment.Length > 0 || warning || showIcon) list.Add(new NoteAnnotation(tick, comment, warning, showIcon));
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

/// <summary>ノート/フリーズの色指定を解除する(色編集モードの右クリック/選択中Deleteキー/右ドラッグ
/// 連続解除)。resetColor/resetBand(通常サブモード)・resetShadow(塗りつぶし色サブモード)・
/// resetHit/resetHitBar/resetHitShadow(ヒット時色サブモード)のうち指定した部位のみクリアする
/// (2026-08-08不具合修正: 従来はresetColor/resetBandしか無く、Shadow/Hitサブモード中の右クリックが
/// 何も解除できなかった。PaintAt側のサブモード別振り分けと対称になるよう、SmartToolController側の
/// 呼び出し元でサブモードごとに適切なフラグだけをtrueにして渡す)。指定していない部位は元の値を保持する。
/// 全フィールドが空になった場合のみエントリ自体を削除する。</summary>
public sealed class ResetNoteColorAction(int lane, long tick,
    bool resetColor = false, bool resetBand = false,
    bool resetShadow = false, bool resetHit = false, bool resetHitBar = false, bool resetHitShadow = false) : IEditAction
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
            _before.AllFlag,
            resetShadow ? null : _before.ShadowColor,
            resetHit ? null : _before.HitColor,
            resetHitBar ? null : _before.HitBarColor,
            resetHitShadow ? null : _before.HitShadowColor);
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

/// <summary>speed/boost/BPMイベントの配置。BPMのtick0は不変条件のため配置不可(TimingEngine前提)。
/// linkGridDivision(2026-07-30追加): speed/boostの値編集時に既存のリンク設定を保持したまま
/// 削除→再配置するためのオプション引数(通常の新規配置時はnullのまま呼び出せば良い)。</summary>
public sealed class PlaceValueEventAction(ValueEventKind kind, long tick, double value, int? linkGridDivision = null) : IEditAction
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
                doc.CurrentTab.SpeedEvents.Add(new ValueEvent(tick, value, linkGridDivision));
                break;
            case ValueEventKind.Boost:
                doc.CurrentTab.BoostEvents.Add(new ValueEvent(tick, value, linkGridDivision));
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

/// <summary>speed/boost/BPMイベントの削除。tick0のBPMは削除不可(不変条件)。
/// 2026-07-30追記: speed/boostを削除する際、削除対象を「次」としてリンクしている直前のイベントが
/// あれば、そのリンクも一緒に解除する(リンク先が消滅した状態のまま、tick順で新たに隣り合った
/// 別の遠いイベントへ黙って再リンクされてしまう事故を防ぐ)。</summary>
public sealed class DeleteValueEventAction(ValueEventKind kind, long tick) : IEditAction
{
    private double? _removedValue;
    private int? _removedLink;
    private long? _clearedPredecessorTick;
    private int? _clearedPredecessorLink;

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
            case ValueEventKind.Boost:
                {
                    var list = kind == ValueEventKind.Speed ? doc.CurrentTab.SpeedEvents : doc.CurrentTab.BoostEvents;
                    var e = list.FirstOrDefault(x => x.Tick == tick);
                    if (e is null) break;
                    _removedValue = e.Value;
                    _removedLink = e.LinkGridDivision;

                    var sorted = list.OrderBy(x => x.Tick).ToList();
                    int idx = sorted.FindIndex(x => x.Tick == tick);
                    if (idx > 0 && sorted[idx - 1].LinkGridDivision is not null)
                    {
                        _clearedPredecessorTick = sorted[idx - 1].Tick;
                        _clearedPredecessorLink = sorted[idx - 1].LinkGridDivision;
                        int predIdx = list.FindIndex(x => x.Tick == _clearedPredecessorTick);
                        if (predIdx >= 0) list[predIdx] = list[predIdx] with { LinkGridDivision = null };
                    }

                    list.Remove(e);
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
            case ValueEventKind.Speed:
            case ValueEventKind.Boost:
                {
                    var list = kind == ValueEventKind.Speed ? doc.CurrentTab.SpeedEvents : doc.CurrentTab.BoostEvents;
                    list.Add(new ValueEvent(tick, v, _removedLink));
                    if (_clearedPredecessorTick is { } pt)
                    {
                        int predIdx = list.FindIndex(x => x.Tick == pt);
                        if (predIdx >= 0) list[predIdx] = list[predIdx] with { LinkGridDivision = _clearedPredecessorLink };
                    }
                    break;
                }
            case ValueEventKind.Bpm: doc.Project.BpmEvents.Add(new BpmEvent(tick, v)); break;
        }
    }
}

/// <summary>speed/boost/BPMイベントの左ドラッグ移動(単体)。tick0のBPMは対象外(不変条件)。
/// 2026-07-30追記: speed/boostのLinkGridDivision(リンク設定)は移動後もそのまま保持する(要望対応
/// 「リンク状態のマーカーでも動かせるようにする」)。リンク相手はtick順で常に「直後の同種イベント」の
/// ため、移動によって隣接関係が変わった場合はリンク先も自動的に切り替わる(都度計算方式)。</summary>
public sealed class MoveValueEventAction(ValueEventKind kind, long oldTick, long newTick) : IEditAction
{
    private double _value;
    private int? _link;

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
                    _link = e.LinkGridDivision;
                    doc.CurrentTab.SpeedEvents.Remove(e);
                    doc.CurrentTab.SpeedEvents.Add(new ValueEvent(newTick, _value, _link));
                    break;
                }
            case ValueEventKind.Boost:
                {
                    var e = doc.CurrentTab.BoostEvents.First(x => x.Tick == oldTick);
                    _value = e.Value;
                    _link = e.LinkGridDivision;
                    doc.CurrentTab.BoostEvents.Remove(e);
                    doc.CurrentTab.BoostEvents.Add(new ValueEvent(newTick, _value, _link));
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
                doc.CurrentTab.SpeedEvents.Add(new ValueEvent(oldTick, _value, _link));
                break;
            case ValueEventKind.Boost:
                doc.CurrentTab.BoostEvents.RemoveAll(x => x.Tick == newTick && x.Value == _value);
                doc.CurrentTab.BoostEvents.Add(new ValueEvent(oldTick, _value, _link));
                break;
            case ValueEventKind.Bpm:
                doc.Project.BpmEvents.RemoveAll(x => x.Tick == newTick && x.Bpm == _value);
                doc.Project.BpmEvents.Add(new BpmEvent(oldTick, _value));
                break;
        }
    }
}

/// <summary>speed/boostの「始点終点オートスムージング出力」用リンク設定/解除(2026-07-30要望対応)。
/// 対象イベント(kind, tick)のLinkGridDivisionを書き換える。gridDivision=nullでリンク解除、非nullで
/// 「tick順で直後の同種イベントとの間を、指定した設置間隔(4/8/16/32)で自動補間する」設定を行う。
/// BPMイベントは対象外。</summary>
public sealed class SetValueEventLinkAction(ValueEventKind kind, long tick, int? gridDivision) : IEditAction
{
    private int? _previous;

    public string Label => gridDivision is null ? "速度/ブーストのリンク解除" : "速度/ブーストのリンク設定";

    public void Do(EditorDocument doc)
    {
        var list = kind == ValueEventKind.Speed ? doc.CurrentTab.SpeedEvents : doc.CurrentTab.BoostEvents;
        int idx = list.FindIndex(e => e.Tick == tick);
        if (idx < 0) return;
        _previous = list[idx].LinkGridDivision;
        list[idx] = list[idx] with { LinkGridDivision = gridDivision };
    }

    public void Undo(EditorDocument doc)
    {
        var list = kind == ValueEventKind.Speed ? doc.CurrentTab.SpeedEvents : doc.CurrentTab.BoostEvents;
        int idx = list.FindIndex(e => e.Tick == tick);
        if (idx < 0) return;
        list[idx] = list[idx] with { LinkGridDivision = _previous };
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

    /// <summary>2026-08-08不具合修正(第三者報告への追加対応): 移動先が既存ノートで塞がっていたため
    /// 移動をパスしたノートの記録。ユーザー確定仕様により、パスしたノートは元の位置へ戻さず削除する
    /// (カット&amp;ペーストで移動先が塞がっていた場合と同じ結果に揃える)。付随データ(色指定・コメント)も
    /// DeleteNoteActionと同じ要領で一緒に削除し、Undoでまとめて復元する(実体だけ消えて付随データが
    /// 別tickの幽霊として残る事故を防ぐ)。</summary>
    private readonly record struct BlockedNoteRecord(ObjectRef Before, NColorEntry? Color, NoteAnnotation? Annotation);

    private readonly IReadOnlyList<ObjectRef> _originalTargets;
    private readonly int _laneDelta;
    private readonly long _tickDelta;
    private List<MoveRecord>? _applied;
    private List<BlockedNoteRecord>? _blockedNotes;

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
        var blockedNotes = new List<BlockedNoteRecord>();
        var blockerRefs = new List<ObjectRef>(); // パスする原因になった既存ノート(選択対象に加える)
        var tab = doc.CurrentTab;

        // 2026-08-08不具合修正(第三者報告): 通常ノートは他の種別と異なり、移動先に「この操作で
        // 移動中でない」既存ノートがあれば衝突として弾く必要がある(従来はチェックが無く、選択→
        // ドラッグ移動で移動先に既にノートがあってもそのまま重ねて置けてしまっていた)。
        // 単純に1件ずつMove()を呼ぶと、選択内で連鎖的に位置がずれる塊移動(例: 連続する3音を
        // まとめて1つ分ずらす)まで自分自身との衝突として誤検知してしまうため、対象ノートを
        // 先に全てレーンから取り除いてから配置し直す(選択内で行き先が空くケースは衝突扱いに
        // せず、選択外のノートとの衝突・選択内での行き先重複だけを検出する)。
        //
        // 衝突した場合は移動をパスする。ユーザー確定仕様により、パスしたノートは元の位置へは
        // 戻さず削除し(「移動した」という結果を直感的に受け取れるよう、取り残されたノートを
        // 残さない)、移動後の選択対象は「移動が成功したノート」+「パスする原因になった既存
        // ノート」とする(なぜ止まったのかが一目で分かるようにする)。
        var noteTargets = _originalTargets.Where(r => r.Kind == ObjectKind.Note).ToList();
        foreach (var r in noteTargets) tab.Lanes[r.Lane].Notes.Remove(r.Tick);
        foreach (var r in noteTargets)
        {
            int newLane = Math.Clamp(r.Lane + _laneDelta, 0, Math.Max(0, laneCount - 1));
            long newTick = r.Tick + _tickDelta;
            var destLane = tab.Lanes[newLane];
            if (newTick < 0 || destLane.Notes.Contains(newTick))
            {
                var srcLane = tab.Lanes[r.Lane];
                var color = srcLane.ColorOverrides.FirstOrDefault(c => c.Tick == r.Tick);
                if (color is not null) srcLane.ColorOverrides.Remove(color);
                var annotation = srcLane.Annotations.FirstOrDefault(a => a.Tick == r.Tick);
                if (annotation is not null) srcLane.Annotations.Remove(annotation);
                blockedNotes.Add(new BlockedNoteRecord(r, color, annotation));
                if (newTick >= 0) blockerRefs.Add(new ObjectRef(ObjectKind.Note, newLane, newTick));
                continue;
            }
            destLane.Notes.Add(newTick);
            MoveSidecarEntries(tab, r.Lane, r.Tick, newLane, newTick);
            records.Add(new MoveRecord(r, new ObjectRef(ObjectKind.Note, newLane, newTick)));
        }

        foreach (var r in _originalTargets.Where(r => r.Kind != ObjectKind.Note))
        {
            var after = Move(doc, r, _laneDelta, _tickDelta, laneCount);
            if (after is { } a) records.Add(new MoveRecord(r, a));
        }

        _applied = records;
        _blockedNotes = blockedNotes;
        RefreshSelection(doc, records.Select(x => x.After).Concat(blockerRefs));
    }

    public void Undo(EditorDocument doc)
    {
        var tab = doc.CurrentTab;
        var restored = new List<ObjectRef>();

        if (_applied is not null)
        {
            foreach (var rec in _applied)
            {
                // After→Beforeへ、実際に記録された絶対位置で正確に戻す(デルタ再適用ではない)
                var back = MoveExact(doc, rec.After, rec.Before);
                if (back) restored.Add(rec.Before);
            }
        }

        if (_blockedNotes is not null)
        {
            foreach (var b in _blockedNotes)
            {
                tab.Lanes[b.Before.Lane].Notes.Add(b.Before.Tick);
                if (b.Color is not null) tab.Lanes[b.Before.Lane].ColorOverrides.Add(b.Color);
                if (b.Annotation is not null) tab.Lanes[b.Before.Lane].Annotations.Add(b.Annotation);
                restored.Add(b.Before);
            }
        }

        RefreshSelection(doc, restored);
    }

    private static void RefreshSelection(EditorDocument doc, IEnumerable<ObjectRef> refs)
    {
        doc.Selection.Clear();
        foreach (var r in refs) doc.Selection.Add(r);
    }

    /// <summary>refで指定されたオブジェクトを(laneDelta,tickDelta)だけ動かす。移動後の新ObjectRefを返す(移動不能ならnull)。
    /// 2026-08-08: 通常ノート(ObjectKind.Note)は衝突回避(元の位置に留める)が必要になったためDo()側で
    /// 個別に処理するようになり、ここには来ない(呼び出し元でKind != Noteへ絞り込み済み)。</summary>
    private static ObjectRef? Move(EditorDocument doc, ObjectRef r, int laneDelta, long tickDelta, int laneCount)
    {
        var tab = doc.CurrentTab;
        switch (r.Kind)
        {
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
        var tab = doc.CurrentTab;

        // 2026-08-08追加対応(ユーザー確定仕様): 通常ノートは複製先に既存ノートがあれば複製をパスし、
        // その原因になった既存ノートを選択対象に加える(Move/Pasteと同じ考え方、なぜ複製されなかった
        // かが一目で分かるようにする)。複製はMoveと異なり元のノートを削除しないため、Contains判定
        // だけでよい(先に取り除いてから配置し直す必要が無い)。
        var blockerRefs = new List<ObjectRef>();
        foreach (var r in _originalTargets.Where(r => r.Kind == ObjectKind.Note))
        {
            if (!tab.Lanes[r.Lane].Notes.Contains(r.Tick)) continue;
            int newLane = Math.Clamp(r.Lane + _laneDelta, 0, Math.Max(0, laneCount - 1));
            long newTick = r.Tick + _tickDelta;
            if (newTick < 0) continue;
            if (tab.Lanes[newLane].Notes.Contains(newTick))
            {
                blockerRefs.Add(new ObjectRef(ObjectKind.Note, newLane, newTick));
                continue;
            }
            tab.Lanes[newLane].Notes.Add(newTick);
            CopySidecar(tab, r.Lane, r.Tick, newLane, newTick);
            created.Add(new ObjectRef(ObjectKind.Note, newLane, newTick));
        }

        foreach (var r in _originalTargets.Where(r => r.Kind != ObjectKind.Note))
        {
            var c = Create(doc, r, _laneDelta, _tickDelta, laneCount);
            if (c is { } cc) created.Add(cc);
        }

        _created = created;
        doc.Selection.Clear();
        foreach (var c in created) doc.Selection.Add(c);
        foreach (var b in blockerRefs) doc.Selection.Add(b);
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
    /// 作成後の新ObjectRefを返す(元が見つからない/作成不能なら null)。
    /// 2026-08-08: 通常ノート(ObjectKind.Note)は複製先衝突時に「パスする原因になった既存ノートを
    /// 選択対象へ加える」処理が必要になったためDo()側で個別に処理するようになり、ここには来ない
    /// (呼び出し元でKind != Noteへ絞り込み済み)。</summary>
    private static ObjectRef? Create(EditorDocument doc, ObjectRef r, int laneDelta, long tickDelta, int laneCount)
    {
        var tab = doc.CurrentTab;
        switch (r.Kind)
        {
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
    private Dictionary<int, int>? _oldToNewLane;

    public string Label => $"マクロ適用: {macroName}";

    public void Do(EditorDocument doc)
    {
        var lanes = doc.CurrentTab.Lanes;
        var snapshot = new List<LaneNotes>(lanes);
        _before = snapshot;

        var oldToNew = new Dictionary<int, int>();
        for (int i = 0; i < lanes.Count && i < laneMapping.Count; i++)
        {
            int from = laneMapping[i];
            if (from < 0 || from >= snapshot.Count) continue;
            lanes[i] = snapshot[from];
            oldToNew[from] = i;
        }
        _oldToNewLane = oldToNew;
        RemapSelection(doc, oldToNew);
    }

    public void Undo(EditorDocument doc)
    {
        if (_before is null) return;
        var lanes = doc.CurrentTab.Lanes;
        for (int i = 0; i < lanes.Count && i < _before.Count; i++)
            lanes[i] = _before[i];

        if (_oldToNewLane is not null)
        {
            var newToOld = _oldToNewLane.ToDictionary(kv => kv.Value, kv => kv.Key);
            RemapSelection(doc, newToOld);
        }
    }

    /// <summary>選択中のObjectRefのLaneを、指定されたマッピング(旧Lane→新Lane)に従って更新する。
    /// マッピングに含まれないLaneの選択はそのまま維持する(2026-08-01: マクロでレーンを入れ替えた後も
    /// 選択範囲表示が移動元の位置に表示されっぱなしになる不具合の修正)。</summary>
    private static void RemapSelection(EditorDocument doc, IReadOnlyDictionary<int, int> laneRemap)
    {
        if (doc.Selection.Count == 0) return;
        var updated = doc.Selection
            .Select(r => laneRemap.TryGetValue(r.Lane, out var newLane) ? r with { Lane = newLane } : r)
            .ToList();
        doc.Selection.Clear();
        foreach (var r in updated) doc.Selection.Add(r);
    }
}

/// <summary>
/// レーン入替マクロの範囲選択適用(2026-07-26要望対応)。ApplyLaneSwapMacroActionの
/// 「レーン丸ごとポインタ差し替え」とは異なり、各レーンのLaneNotes(Notes/Freezes/
/// ColorOverrides/Annotations)を「範囲内」「範囲外」の要素単位で分割し、範囲内サブセットだけを
/// laneMappingに従って入れ替える(範囲外の要素はそのレーンに残したまま)。
/// フリーズは境界(範囲の始点・終点)をまたぐ場合の扱いをincludeStraddlingFreezesで指定する
/// (true=フリーズごと範囲内として移動、false=フリーズごと範囲外として据え置き)。
/// ColorOverrides/Annotationsは対応するNotes/Freezesと同じtick(フリーズはStartTick)で
/// 同定し、本体の移動先へ追随させる(LaneNotesの規約通り)。
/// </summary>
public sealed class ApplyLaneSwapMacroRangeAction(
    IReadOnlyList<int> laneMapping, string macroName, long rangeStart, long rangeEnd, bool includeStraddlingFreezes)
    : IEditAction
{
    private List<LaneNotes>? _before;
    private List<(int FromLane, long Tick, int ToLane)>? _movedObjects;

    public string Label => $"マクロ適用(範囲選択): {macroName}";

    public void Do(EditorDocument doc)
    {
        long lo = Math.Min(rangeStart, rangeEnd), hi = Math.Max(rangeStart, rangeEnd);
        var lanes = doc.CurrentTab.Lanes;
        var snapshot = new List<LaneNotes>(lanes.Select(CloneLaneNotes));
        _before = new List<LaneNotes>(lanes.Select(CloneLaneNotes));

        var inRange = new LaneNotes[snapshot.Count];
        var outRange = new LaneNotes[snapshot.Count];
        for (int i = 0; i < snapshot.Count; i++)
            SplitByRange(snapshot[i], lo, hi, includeStraddlingFreezes, out inRange[i], out outRange[i]);

        // 2026-08-01: 範囲内で実際に移動する(移動元レーン,tick,移動先レーン)を記録し、
        // 選択範囲表示(ObjectRef)を移動先へ追従させるのに使う(選択追従バグの修正)。
        var movedObjects = new List<(int FromLane, long Tick, int ToLane)>();
        for (int i = 0; i < lanes.Count && i < laneMapping.Count; i++)
        {
            int from = laneMapping[i];
            if (from < 0 || from >= inRange.Length) continue;
            lanes[i] = MergeLaneNotes(outRange[i], inRange[from]);
            if (from == i) continue; // 同一レーンへの入替(移動なし)は対象外
            foreach (var t in inRange[from].Notes) movedObjects.Add((from, t, i));
            foreach (var f in inRange[from].Freezes) movedObjects.Add((from, f.StartTick, i));
        }
        _movedObjects = movedObjects;
        RemapSelection(doc, movedObjects, forward: true);
    }

    public void Undo(EditorDocument doc)
    {
        if (_before is null) return;
        var lanes = doc.CurrentTab.Lanes;
        for (int i = 0; i < lanes.Count && i < _before.Count; i++)
            lanes[i] = _before[i];

        if (_movedObjects is not null) RemapSelection(doc, _movedObjects, forward: false);
    }

    /// <summary>選択中のObjectRefのうち、実際に移動した(Lane,Tick)に一致するものをLaneのみ
    /// 付け替える(Tick・種別は不変のため、ここではLaneの対応表だけで足りる)。</summary>
    private static void RemapSelection(EditorDocument doc, List<(int FromLane, long Tick, int ToLane)> moved, bool forward)
    {
        if (doc.Selection.Count == 0 || moved.Count == 0) return;
        var map = new Dictionary<(int Lane, long Tick), int>();
        foreach (var m in moved)
        {
            var key = forward ? (m.FromLane, m.Tick) : (m.ToLane, m.Tick);
            map[key] = forward ? m.ToLane : m.FromLane;
        }
        var updated = doc.Selection
            .Select(r => map.TryGetValue((r.Lane, r.Tick), out var newLane) ? r with { Lane = newLane } : r)
            .ToList();
        doc.Selection.Clear();
        foreach (var r in updated) doc.Selection.Add(r);
    }

    private static LaneNotes CloneLaneNotes(LaneNotes source) => new()
    {
        Notes = new List<long>(source.Notes),
        Freezes = new List<FreezeNote>(source.Freezes),
        ColorOverrides = new List<NColorEntry>(source.ColorOverrides),
        Annotations = new List<NoteAnnotation>(source.Annotations),
    };

    private static void SplitByRange(LaneNotes source, long lo, long hi, bool includeStraddling,
        out LaneNotes inRange, out LaneNotes outRange)
    {
        bool InRangeTick(long t) => t >= lo && t <= hi;

        var movedNotes = source.Notes.Where(InRangeTick).ToList();
        var keptNotes = source.Notes.Where(t => !InRangeTick(t)).ToList();

        var movedFreezes = new List<FreezeNote>();
        var keptFreezes = new List<FreezeNote>();
        foreach (var f in source.Freezes)
        {
            bool startIn = InRangeTick(f.StartTick);
            bool endIn = InRangeTick(f.EndTick);
            // startIn==endInの場合(完全内包/完全外包)はstartInの値がそのままmoves判定になる。
            // 一致しない場合(境界をまたぐ)はユーザー選択(includeStraddling)に従う。
            bool moves = startIn == endIn ? startIn : includeStraddling;
            (moves ? movedFreezes : keptFreezes).Add(f);
        }

        var movedTicks = new HashSet<long>(movedNotes);
        foreach (var f in movedFreezes) movedTicks.Add(f.StartTick);

        inRange = new LaneNotes
        {
            Notes = movedNotes,
            Freezes = movedFreezes,
            ColorOverrides = source.ColorOverrides.Where(c => movedTicks.Contains(c.Tick)).ToList(),
            Annotations = source.Annotations.Where(a => movedTicks.Contains(a.Tick)).ToList(),
        };
        outRange = new LaneNotes
        {
            Notes = keptNotes,
            Freezes = keptFreezes,
            ColorOverrides = source.ColorOverrides.Where(c => !movedTicks.Contains(c.Tick)).ToList(),
            Annotations = source.Annotations.Where(a => !movedTicks.Contains(a.Tick)).ToList(),
        };
    }

    private static LaneNotes MergeLaneNotes(LaneNotes outRangePart, LaneNotes inRangePartFromOtherLane) => new()
    {
        Notes = [.. outRangePart.Notes, .. inRangePartFromOtherLane.Notes],
        Freezes = [.. outRangePart.Freezes, .. inRangePartFromOtherLane.Freezes],
        ColorOverrides = [.. outRangePart.ColorOverrides, .. inRangePartFromOtherLane.ColorOverrides],
        Annotations = [.. outRangePart.Annotations, .. inRangePartFromOtherLane.Annotations],
    };
}

/// <summary>レーン入替マクロの範囲選択適用に関するヘルパー(2026-07-26要望対応)。
/// UIが「適用前の警告ダイアログを出すべきか」を判定するために使う。</summary>
public static class LaneSwapMacroRangeHelper
{
    /// <summary>指定範囲(tick、順不同で渡してよい)の境界をまたぐフリーズ(始点・終点の
    /// 範囲内外が一致しないもの)が、いずれかのレーンに1件でも存在するかを調べる。</summary>
    public static bool HasStraddlingFreezes(DifficultyTab tab, long rangeStart, long rangeEnd)
    {
        long lo = Math.Min(rangeStart, rangeEnd), hi = Math.Max(rangeStart, rangeEnd);
        bool InRange(long t) => t >= lo && t <= hi;
        foreach (var lane in tab.Lanes)
            foreach (var f in lane.Freezes)
                if (InRange(f.StartTick) != InRange(f.EndTick)) return true;
        return false;
    }
}
