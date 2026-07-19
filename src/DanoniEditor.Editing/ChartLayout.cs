using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>レーン列の種別(仕様書6.1: マーカー|小節|キーレーン群|speed|boost|BPM)</summary>
public enum ColumnKind { Marker, Measure, Note, Speed, Boost, Bpm }

/// <summary>1列分のレイアウト情報</summary>
public sealed record ColumnInfo(ColumnKind Kind, int NoteLaneIndex, double X, double Width)
{
    public double CenterX => X + Width / 2;
    public bool Contains(double x) => x >= X && x < X + Width;
}

/// <summary>ヒットテスト対象のオブジェクト参照(選択状態の保持にも使う)</summary>
public enum ObjectKind { Note, FreezeStart, FreezeEnd, FreezeBody, Speed, Boost, Bpm, TimeSignature, Marker }

public readonly record struct ObjectRef(ObjectKind Kind, int Lane, long Tick)
{
    /// <summary>同一実体か(FreezeStart/End/Bodyは始点tickで同一フリーズを指す)</summary>
    public bool SameEntity(ObjectRef other) =>
        Lane == other.Lane && Tick == other.Tick && KindGroup(Kind) == KindGroup(other.Kind);
    private static int KindGroup(ObjectKind k) =>
        k is ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody ? 1 : (int)k + 10;
}

/// <summary>
/// 譜面編集エリアの座標系(仕様書6.1)。縦=時間軸(上=tick0、下方向へ増加)、横=レーン。
/// WPF非依存。ズーム(pxPerTick)とノート表示サイズ(noteSize)は4.3のグローバル表示設定。
/// </summary>
public sealed class ChartLayout
{
    public const double MarkerColWidth = 30;
    public const double MeasureColWidth = 46;
    public const double EventColWidth = 46;
    public const double TopMargin = 24;
    public const double BaseNoteSize = 34;

    /// <summary>1tickあたりのピクセル数(Shift+スクロールで可変、仕様書4.3)</summary>
    /// <summary>tickあたり表示px。2026-07-19g: 分解能×35に伴い既定値・可動域も1/35へスケール
    /// (見た目の密度は従来と同一)。</summary>
    public double PxPerTick { get; set; } = DefaultPxPerTick;

    public const double DefaultPxPerTick = 0.5 * 48.0 / TimingEngine.TicksPerBeat;
    public const double MinPxPerTick = 0.05 * 48.0 / TimingEngine.TicksPerBeat;
    public const double MaxPxPerTick = 5.0 * 48.0 / TimingEngine.TicksPerBeat;

    /// <summary>
    /// 作業エリア全体のズーム倍率(Ctrl+スクロールで可変)。カラム幅・ノート表示サイズは
    /// すべてこの倍率に連動して再計算される(「ノート画像だけでなくレーン幅も含めた
    /// 作業エリア全体のズーム」という設計、2026-07-16修正)。
    /// </summary>
    public double ZoomScale { get; private set; } = 1.0;

    /// <summary>ノート画像の表示サイズpx(ZoomScaleに連動、仕様書4.3)</summary>
    public double NoteSize => BaseNoteSize * ZoomScale;

    /// <summary>キーレーン1本の幅(ノートサイズ+余白)</summary>
    public double NoteLaneWidth => NoteSize + 8;

    public KeyTemplate Template { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; private set; }
    public double TotalWidth { get; private set; }

    public ChartLayout(KeyTemplate template)
    {
        Template = template;
        Columns = Array.Empty<ColumnInfo>();
        RebuildColumns();
    }

    /// <summary>
    /// 作業エリア全体のズーム倍率を変更し、全カラムの幅・位置を再計算する
    /// (Ctrl+スクロール用。0.35~2.8倍にクランプ、BaseNoteSize基準で12~96px相当)。
    /// </summary>
    public void SetZoom(double scale)
    {
        ZoomScale = Math.Clamp(scale, 0.35, 2.8);
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        var cols = new List<ColumnInfo>();
        double x = 0;
        void Add(ColumnKind kind, double w, int lane = -1)
        { cols.Add(new ColumnInfo(kind, lane, x, w)); x += w; }

        Add(ColumnKind.Marker, MarkerColWidth * ZoomScale);
        Add(ColumnKind.Measure, MeasureColWidth * ZoomScale);
        for (int i = 0; i < Template.KeyCount; i++)
            Add(ColumnKind.Note, NoteLaneWidth, i);
        Add(ColumnKind.Speed, EventColWidth * ZoomScale);
        Add(ColumnKind.Boost, EventColWidth * ZoomScale);
        Add(ColumnKind.Bpm, EventColWidth * ZoomScale);
        Columns = cols;
        TotalWidth = x;
    }

    // --- 座標変換 ---
    public double TickToY(double tick) => TopMargin + tick * PxPerTick;
    public double YToTick(double y) => (y - TopMargin) / PxPerTick;

    public ColumnInfo? ColumnAt(double x) => Columns.FirstOrDefault(c => c.Contains(x));

    /// <summary>キーレーンindex→列(displayOrder順に並んでいるlanes[]のindexそのまま)</summary>
    public ColumnInfo NoteColumn(int laneIndex) => Columns.First(c => c.Kind == ColumnKind.Note && c.NoteLaneIndex == laneIndex);
    public ColumnInfo Column(ColumnKind kind) => Columns.First(c => c.Kind == kind);

    /// <summary>コンテンツ全体の高さ(最終オブジェクト+4小節ぶんの余白)</summary>
    public double ContentHeight(long maxTick) => TickToY(maxTick + 4L * TimingEngine.TicksPerBeat * 4);

    // --- ヒットテスト ---
    /// <summary>点(x,y)にあるオブジェクトを返す(上に描画されるもの優先)。hitScale=0.5でドラッグ削除用の縮小判定(6.3.1)</summary>
    public ObjectRef? HitTest(DifficultyTab tab, ChartProject project, double x, double y, double hitScale = 1.0)
    {
        var col = ColumnAt(x);
        if (col is null) return null;
        double half = NoteSize / 2 * hitScale;
        double evH = 9 * hitScale; // イベントタグの縦半径

        switch (col.Kind)
        {
            case ColumnKind.Note:
                {
                    var lane = tab.Lanes[col.NoteLaneIndex];
                    // フリーズ端点 > 通常ノート > フリーズ胴体 の優先順
                    foreach (var f in lane.Freezes)
                    {
                        if (Math.Abs(TickToY(f.StartTick) - y) <= half)
                            return new ObjectRef(ObjectKind.FreezeStart, col.NoteLaneIndex, f.StartTick);
                        if (Math.Abs(TickToY(f.EndTick) - y) <= half)
                            return new ObjectRef(ObjectKind.FreezeEnd, col.NoteLaneIndex, f.StartTick);
                    }
                    foreach (var t in lane.Notes)
                        if (Math.Abs(TickToY(t) - y) <= half)
                            return new ObjectRef(ObjectKind.Note, col.NoteLaneIndex, t);
                    foreach (var f in lane.Freezes)
                        if (y > TickToY(f.StartTick) && y < TickToY(f.EndTick))
                            return new ObjectRef(ObjectKind.FreezeBody, col.NoteLaneIndex, f.StartTick);
                    return null;
                }
            case ColumnKind.Speed:
                return HitEvents(tab.SpeedEvents.Select(e => e.Tick), ObjectKind.Speed, y, evH);
            case ColumnKind.Boost:
                return HitEvents(tab.BoostEvents.Select(e => e.Tick), ObjectKind.Boost, y, evH);
            case ColumnKind.Bpm:
                return HitEvents(project.BpmEvents.Select(e => e.Tick), ObjectKind.Bpm, y, evH);
            case ColumnKind.Measure:
                {
                    var engine = project.CreateTimingEngine();
                    foreach (var s in project.TimeSignatures)
                        if (Math.Abs(TickToY(engine.MeasureStartTick(s.MeasureIndex)) - y) <= evH)
                            return new ObjectRef(ObjectKind.TimeSignature, -1, s.MeasureIndex);
                    return null;
                }
            case ColumnKind.Marker:
                return HitEvents(project.Markers.Select(m => m.Tick), ObjectKind.Marker, y, evH);
            default:
                return null;
        }
    }

    private ObjectRef? HitEvents(IEnumerable<long> ticks, ObjectKind kind, double y, double evH)
    {
        foreach (var t in ticks)
            if (Math.Abs(TickToY(t) - y) <= evH)
                return new ObjectRef(kind, -1, t);
        return null;
    }

    /// <summary>矩形範囲内のオブジェクト列挙(範囲選択用、当たり判定は中心基準=仕様書6.3.1)</summary>
    public IEnumerable<ObjectRef> ObjectsInRect(DifficultyTab tab, ChartProject project,
        double x1, double y1, double x2, double y2)
    {
        if (x1 > x2) (x1, x2) = (x2, x1);
        if (y1 > y2) (y1, y2) = (y2, y1);
        long tMin = (long)Math.Floor(YToTick(y1)), tMax = (long)Math.Ceiling(YToTick(y2));

        foreach (var col in Columns)
        {
            if (col.CenterX < x1 || col.CenterX > x2) continue;
            switch (col.Kind)
            {
                case ColumnKind.Note:
                    var lane = tab.Lanes[col.NoteLaneIndex];
                    foreach (var t in lane.Notes)
                        if (t >= tMin && t <= tMax) yield return new ObjectRef(ObjectKind.Note, col.NoteLaneIndex, t);
                    foreach (var f in lane.Freezes)
                        if (f.StartTick >= tMin && f.StartTick <= tMax)
                            yield return new ObjectRef(ObjectKind.FreezeStart, col.NoteLaneIndex, f.StartTick);
                    break;
                case ColumnKind.Speed:
                    foreach (var e in tab.SpeedEvents)
                        if (e.Tick >= tMin && e.Tick <= tMax) yield return new ObjectRef(ObjectKind.Speed, -1, e.Tick);
                    break;
                case ColumnKind.Boost:
                    foreach (var e in tab.BoostEvents)
                        if (e.Tick >= tMin && e.Tick <= tMax) yield return new ObjectRef(ObjectKind.Boost, -1, e.Tick);
                    break;
                case ColumnKind.Bpm:
                    foreach (var e in project.BpmEvents)
                        if (e.Tick >= tMin && e.Tick <= tMax && e.Tick != 0)
                            yield return new ObjectRef(ObjectKind.Bpm, -1, e.Tick);
                    break;
            }
        }
    }
}

/// <summary>スナップ(仕様書6.5+2026-07-19g拡張: 4/8/12/16/20/24/28/32/40/48/56/64分、ON/OFF)</summary>
public sealed class SnapService
{
    public bool Enabled { get; set; } = true;
    /// <summary>N分音符のN(4分=1680tick, ..., 64分=105tick)</summary>
    public int Division { get; set; } = 16;

    public long GridTicks => 4L * TimingEngine.TicksPerBeat / Division; // 1小節(4拍)をN分割

    public long Snap(double tick)
    {
        if (tick < 0) tick = 0;
        if (!Enabled) return (long)Math.Round(tick);
        long g = GridTicks;
        return (long)Math.Round(tick / g) * g;
    }

    public static readonly int[] Divisions = [4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64]; // 2026-07-19g: 20/28/40/56分(5連・7連系)追加
}
