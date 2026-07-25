using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>レーン列の種別(仕様書6.1: 時間情報|マーカー|小節|キーレーン群|speed|boost|BPM|歌詞)</summary>
public enum ColumnKind { TimeInfo, Marker, Measure, Note, Speed, Boost, Bpm, Word }

/// <summary>1列分のレイアウト情報。NoteLaneIndexはKind=NoteならtabのLanes[]index、
/// Kind=WordならtabのWordLanes[]indexを指す(2026-07-23、TBD 4、Note用フィールドを流用)。</summary>
public sealed record ColumnInfo(ColumnKind Kind, int NoteLaneIndex, double X, double Width)
{
    public double CenterX => X + Width / 2;
    public bool Contains(double x) => x >= X && x < X + Width;
}

/// <summary>ヒットテスト対象のオブジェクト参照(選択状態の保持にも使う)</summary>
public enum ObjectKind { Note, FreezeStart, FreezeEnd, FreezeBody, Speed, Boost, Bpm, TimeSignature, Marker, Word }

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
    /// <summary>時間情報表示レーン(2026-07-23、TBD 1-1)。マーカー・小節番号レーンの左隣に置く
    /// 表示専用レーン(frame/time/小節番号)。ノートレーンと幅を揃える必要は無いという確定仕様のため、
    /// 3行の短いテキストが収まる程度の専用幅を割り当てる。</summary>
    public const double TimeInfoColWidth = 54;
    public const double MarkerColWidth = 30;
    public const double MeasureColWidth = 46;
    public const double EventColWidth = 46;
    // 2026-08-05: 「0小節目頭を画面中央までスクロールできるようにしたい」との要望対応で24→400へ拡大。
    // tick0の描画位置(RawTickToY(0)=TopMargin)がそのままスクロール可能範囲の先頭側の余白にもなるため、
    // ここを広げるだけで先頭を画面中央付近まで持ってこられるようになる(一般的なウィンドウ高さを想定した値)。
    public const double TopMargin = 400;
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

    /// <summary>現在タブが持つ歌詞レーン(WordLanes)の本数(2026-07-23、TBD 4)。
    /// タブごとに可変のため、EditorDocument.CurrentLayoutがタブ切替・レーン追加/削除のたびに
    /// SyncWordLaneCountで同期する。テンプレートのみに依存する他カラムと異なり、この値だけは
    /// レイアウト外部(DifficultyTab.WordLanes)から都度反映する必要がある。</summary>
    public int WordLaneCount { get; private set; }

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

    /// <summary>歌詞レーン本数を最新化する(変化があった場合のみ再構築、2026-07-23、TBD 4)。
    /// EditorDocument.CurrentLayoutから、タブ切替時・歌詞レーン追加/削除の直後に呼ぶ想定。</summary>
    public void SyncWordLaneCount(int count)
    {
        if (count == WordLaneCount) return;
        WordLaneCount = count;
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        var cols = new List<ColumnInfo>();
        double x = 0;
        void Add(ColumnKind kind, double w, int lane = -1)
        { cols.Add(new ColumnInfo(kind, lane, x, w)); x += w; }

        Add(ColumnKind.TimeInfo, TimeInfoColWidth * ZoomScale);
        Add(ColumnKind.Marker, MarkerColWidth * ZoomScale);
        Add(ColumnKind.Measure, MeasureColWidth * ZoomScale);
        for (int i = 0; i < Template.KeyCount; i++)
            Add(ColumnKind.Note, NoteLaneWidth, i);
        Add(ColumnKind.Speed, EventColWidth * ZoomScale);
        Add(ColumnKind.Boost, EventColWidth * ZoomScale);
        Add(ColumnKind.Bpm, EventColWidth * ZoomScale);
        for (int i = 0; i < WordLaneCount; i++)
            Add(ColumnKind.Word, NoteLaneWidth, i);
        Columns = cols;
        TotalWidth = x;
    }

    // --- 座標変換 ---

    /// <summary>譜面ビューのReverse表示(2026-07-22追加、環境設定のみで切替、プレイテストには非適用)。
    /// trueの場合、tick0がコンテンツ下端・末尾が上端になるよう座標変換を反転する
    /// (frameの増減ロジック自体には触れず、TickToY/YToTickの写像のみを反転させる設計)。</summary>
    public bool Reverse { get; set; }

    /// <summary>Reverse時の反転基準となるコンテンツ全体高さ(直近のRefreshContentHeightで更新)。
    /// 初期値は空プロジェクト相当(RefreshContentHeightが呼ばれる前の保険)。</summary>
    private double _reverseContentHeight = 600;

    /// <summary>コンテンツの最終tickが変わりうるタイミング(MeasureOverride/OnRenderの先頭)で呼び、
    /// Reverse反転の基準高さを更新する。Reverse=false時は参照されないが、常に呼んでおいて問題ない。</summary>
    public void RefreshContentHeight(long maxTick) => _reverseContentHeight = ContentHeight(maxTick);

    /// <summary>Reverseの影響を受けない、素の座標変換(tick0=TopMargin、下方向へ増加)</summary>
    private double RawTickToY(double tick) => TopMargin + tick * PxPerTick;

    public double TickToY(double tick)
    {
        double y = RawTickToY(tick);
        return Reverse ? _reverseContentHeight - y : y;
    }

    public double YToTick(double y)
    {
        double rawY = Reverse ? _reverseContentHeight - y : y;
        return (rawY - TopMargin) / PxPerTick;
    }

    public ColumnInfo? ColumnAt(double x) => Columns.FirstOrDefault(c => c.Contains(x));

    /// <summary>キーレーンindex→列(displayOrder順に並んでいるlanes[]のindexそのまま)</summary>
    public ColumnInfo NoteColumn(int laneIndex) => Columns.First(c => c.Kind == ColumnKind.Note && c.NoteLaneIndex == laneIndex);
    /// <summary>歌詞レーンindex(DifficultyTab.WordLanesのindex)→列(2026-07-23、TBD 4)</summary>
    public ColumnInfo WordColumn(int laneIndex) => Columns.First(c => c.Kind == ColumnKind.Word && c.NoteLaneIndex == laneIndex);
    public ColumnInfo Column(ColumnKind kind) => Columns.First(c => c.Kind == kind);

    /// <summary>コンテンツ全体の高さ(最終オブジェクト+4小節ぶんの余白)。Reverseの影響を受けない
    /// 素の高さ(RawTickToY基準)であり、RefreshContentHeightの反転基準そのものでもある。</summary>
    public double ContentHeight(long maxTick) => RawTickToY(maxTick + 4L * TimingEngine.TicksPerBeat * 4);

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
                    // 2026-07-25: フリーズ端点の判定半径は通常ノートの半分に縮小する。
                    // 端点半径を通常ノートと同じ(NoteSize/2)にすると、NoteSize未満の長さの
                    // フリーズノーツ(既定ズームではごく普通に発生する)で始点・終点の判定域が
                    // 重なり合い、帯(FreezeBody)を掴む隙間が事実上消えてしまう
                    // (=帯ドラッグでの全体移動が実質不可能になる)ため。
                    double freezeEndpointHalf = half / 2;
                    // フリーズ端点 > 通常ノート > フリーズ胴体 の優先順
                    foreach (var f in lane.Freezes)
                    {
                        if (Math.Abs(TickToY(f.StartTick) - y) <= freezeEndpointHalf)
                            return new ObjectRef(ObjectKind.FreezeStart, col.NoteLaneIndex, f.StartTick);
                        if (Math.Abs(TickToY(f.EndTick) - y) <= freezeEndpointHalf)
                            return new ObjectRef(ObjectKind.FreezeEnd, col.NoteLaneIndex, f.StartTick);
                    }
                    foreach (var t in lane.Notes)
                        if (Math.Abs(TickToY(t) - y) <= half)
                            return new ObjectRef(ObjectKind.Note, col.NoteLaneIndex, t);
                    foreach (var f in lane.Freezes)
                    {
                        // 2026-07-22: Reverse時はStartTick側のYがEndTick側より大きくなるため、
                        // 順序を仮定しないMin/Max判定にする(仕様書外の座標系反転対応)。
                        double ys = TickToY(f.StartTick), ye = TickToY(f.EndTick);
                        if (y > Math.Min(ys, ye) && y < Math.Max(ys, ye))
                            return new ObjectRef(ObjectKind.FreezeBody, col.NoteLaneIndex, f.StartTick);
                    }
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
            case ColumnKind.Word:
                {
                    var entries = tab.WordLanes[col.NoteLaneIndex].Entries;
                    foreach (var w in entries)
                        if (Math.Abs(TickToY(w.Tick) - y) <= evH)
                            return new ObjectRef(ObjectKind.Word, col.NoteLaneIndex, w.Tick);
                    return null;
                }
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
        // 2026-07-22: Reverse時はy(画面座標)の大小とtickの大小が逆転するため、
        // YToTick後にMin/Maxを取り直す(y1<y2でもYToTick(y1)>YToTick(y2)になりうる)。
        double tA = YToTick(y1), tB = YToTick(y2);
        long tMin = (long)Math.Floor(Math.Min(tA, tB)), tMax = (long)Math.Ceiling(Math.Max(tA, tB));

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
                case ColumnKind.Word:
                    foreach (var w in tab.WordLanes[col.NoteLaneIndex].Entries)
                        if (w.Tick >= tMin && w.Tick <= tMax)
                            yield return new ObjectRef(ObjectKind.Word, col.NoteLaneIndex, w.Tick);
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
