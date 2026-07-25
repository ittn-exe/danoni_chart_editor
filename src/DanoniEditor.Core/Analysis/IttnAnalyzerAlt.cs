using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Analysis;

/// <summary>ALT計算結果(analyze.jsの`calculateAltLevel`戻り値のうちhighlights/segDebugを除いた部分)。</summary>
public readonly record struct IttnAltResult(double ValAlt, double AltPeakPm, double AltTotalPm, double TotalAltScore);

/// <summary>
/// このファイルは analyzer_and_viewer/analyze.js (2026-07-25時点) の calculateAltLevel を移植したもの。
/// 今後analyze.js側を更新した場合、このファイルへの片方向バックポート要否を確認すること
/// (docs/progress_and_tbd_2026-07-24_add.md §1-1「片方向運用」)。
///
/// ■ALT: 上下逆スクロールの見切り難 v3
/// ■このツールにおけるALTの定義:
///   「上下反対方向にスクロールしてくるノーツが、どの順番で/どのような同時押しで判定ラインに
///    到達するのかを見切る難しさ」(=視認難)。
/// ■第一原理: ALTはどんな形でも1方向スクロールより難しい。
///   → Base値には入れず、存在した時点でTotalに加算される特殊要素として扱う。
/// ■3軸構造:
///   軸1 量: ALT区間内の認知イベント数
///   軸2 速度: イベント間隔(IOI)による音価重み。8分より16分が重い
///   軸3 規則性: IOI変動係数と同時押しパターン多様性による上乗せ係数
/// ■集計: valALT = PEAK_ALPHA×局所max成分 + VOL_BETA×総量成分
/// </summary>
public static class IttnAnalyzerAlt
{
    private static readonly IttnAltResult Empty = new(0, 0, 0, 0);

    private readonly record struct DirNote(long Time, long Tick, string Lane, bool IsUp);

    private sealed class BundledEvent
    {
        public required long Time { get; init; }
        public required long Tick { get; init; }
        public List<string> UpLanes { get; } = [];
        public List<string> DownLanes { get; } = [];
        public List<string> Lanes { get; } = [];
        public bool Insane { get; set; }
    }

    /// <summary>laneGroup名から上下・左右を取得(例: "down_left" → (ud:"down", lr:"left"))</summary>
    private static (string Ud, string Lr) ParseGroupDir(string? grpName)
    {
        var parts = (grpName ?? "").Split('_');
        string ud = parts.Length > 0 ? parts[0] : "";
        string lr = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "center";
        return (ud, lr);
    }

    private static int LowerBound(IReadOnlyList<DirNote> arr, long t)
    {
        int l = 0, r = arr.Count;
        while (l < r)
        {
            int m = (l + r) >> 1;
            if (arr[m].Time < t) l = m + 1; else r = m;
        }
        return l;
    }

    public static IttnAltResult Calculate(IReadOnlyList<IttnTimelineEvent> timeline, string keyTypeId, TimingEngine engine)
    {
        if (!IttnAnalyzerKeyMapData.Entries.TryGetValue(keyTypeId, out var currentKeyMap) || !currentKeyMap.LaneAlt)
            return Empty;

        var laneMap = currentKeyMap.Lanes;

        double PairWeight(string upLane, string downLane)
        {
            var uDir = ParseGroupDir(laneMap.TryGetValue(upLane, out var u) ? u.Group : null);
            var dDir = ParseGroupDir(laneMap.TryGetValue(downLane, out var d) ? d.Group : null);
            if (uDir.Ud == dDir.Ud) return 0;
            if (uDir.Lr == "center" || dDir.Lr == "center") return IttnAnalyzerConfig.Alt.WCenter;
            return uDir.Lr != dDir.Lr ? IttnAnalyzerConfig.Alt.WMain : IttnAnalyzerConfig.Alt.WSub;
        }

        // インセイン判定: 縦隣接キー(同列で行が1違い)の絡みか
        bool IsInsanePair(string laneA, string laneB)
        {
            var pa = laneMap.TryGetValue(laneA, out var la) ? la.Pos : null;
            var pb = laneMap.TryGetValue(laneB, out var lb) ? lb.Pos : null;
            if (pa is null || pb is null) return false;
            return Math.Abs(pa.Value.X - pb.Value.X) <= 0.6 && Math.Abs(pa.Value.Y - pb.Value.Y) == 1;
        }

        var noteList = timeline.Where(n => n.Type is not (IttnTimelineEventType.SpeedChange or IttnTimelineEventType.FreezeEnd)).ToList();
        if (noteList.Count == 0) return Empty;

        var upNotes = new List<DirNote>();
        var downNotes = new List<DirNote>();
        foreach (var n in noteList)
        {
            var scroll = laneMap.TryGetValue(n.Lane!, out var info) ? info.Scroll : null;
            if (scroll == "up") upNotes.Add(new DirNote(n.Time, n.Tick, n.Lane!, true));
            else if (scroll == "down") downNotes.Add(new DirNote(n.Time, n.Tick, n.Lane!, false));
        }
        if (upNotes.Count == 0 || downNotes.Count == 0) return Empty;

        // --- ALT区間検出 ---
        var altZoneFrames = new SortedSet<long>();
        foreach (var n in upNotes)
        {
            int dIdx = LowerBound(downNotes, n.Time - (long)IttnAnalyzerConfig.Alt.AltGap);
            if (dIdx < downNotes.Count && downNotes[dIdx].Time <= n.Time) altZoneFrames.Add(n.Time);
        }
        foreach (var n in downNotes)
        {
            int uIdx = LowerBound(upNotes, n.Time - (long)IttnAnalyzerConfig.Alt.SegGap);
            if (uIdx < upNotes.Count && upNotes[uIdx].Time <= n.Time) altZoneFrames.Add(n.Time);
        }

        // ALTフレームをSEG_GAPで連結して区間リスト化
        var altSegments = new List<(long Start, long End)>();
        {
            var zFrames = altZoneFrames.ToList(); // SortedSetなので既に昇順
            if (zFrames.Count > 0)
            {
                long segStart = zFrames[0], segEnd = zFrames[0];
                for (int i = 1; i < zFrames.Count; i++)
                {
                    if (zFrames[i] - segEnd <= IttnAnalyzerConfig.Alt.SegGap)
                    {
                        segEnd = zFrames[i];
                    }
                    else
                    {
                        altSegments.Add((segStart, segEnd));
                        segStart = zFrames[i];
                        segEnd = zFrames[i];
                    }
                }
                altSegments.Add((segStart, segEnd));
            }
        }

        // --- 各区間の採点 ---
        double totalAltScore = 0;
        var scoredEvents = new List<IttnPeakScoredEvent>();

        foreach (var seg in altSegments)
        {
            var segNotes = new List<DirNote>();
            int uStart = LowerBound(upNotes, seg.Start);
            int dStart = LowerBound(downNotes, seg.Start);
            for (int i = uStart; i < upNotes.Count && upNotes[i].Time <= seg.End; i++) segNotes.Add(upNotes[i]);
            for (int i = dStart; i < downNotes.Count && downNotes[i].Time <= seg.End; i++) segNotes.Add(downNotes[i]);
            if (segNotes.Count == 0) continue;
            segNotes = segNotes.OrderBy(n => n.Time).ToList(); // 安定ソート(JSのArray.sortと同じ)

            // --- 認知イベント束ね: BUNDLE_WINDOW以内の近接ノーツを1イベントに ---
            var events = new List<BundledEvent>();
            {
                int i = 0;
                while (i < segNotes.Count)
                {
                    long t0 = segNotes[i].Time;
                    var ev = new BundledEvent { Time = t0, Tick = segNotes[i].Tick };
                    while (i < segNotes.Count && segNotes[i].Time - t0 <= IttnAnalyzerConfig.Alt.BundleWindow)
                    {
                        var n = segNotes[i];
                        ev.Lanes.Add(n.Lane);
                        (n.IsUp ? ev.UpLanes : ev.DownLanes).Add(n.Lane);
                        i++;
                    }
                    events.Add(ev);
                }
            }

            // --- ペア種別重み: 区間内の混合イベント重みの平均 + インセイン判定 ---
            int insaneEvCount = 0;
            var weightSamples = new List<double>();
            foreach (var ev in events)
            {
                if (ev.UpLanes.Count > 0 && ev.DownLanes.Count > 0)
                {
                    double w = 0;
                    foreach (var ul in ev.UpLanes)
                        foreach (var dl in ev.DownLanes)
                        {
                            double pw = PairWeight(ul, dl);
                            if (pw > w) w = pw;
                            if (IsInsanePair(ul, dl)) ev.Insane = true;
                        }
                    if (w > 0) weightSamples.Add(w);
                    if (ev.Insane) insaneEvCount++;
                }
            }
            if (weightSamples.Count == 0)
            {
                // 混合イベントが無い場合: 隣接する逆方向イベントのペアから採取
                for (int k = 1; k < events.Count; k++)
                {
                    var a = events[k - 1];
                    var b = events[k];
                    var ups = a.UpLanes.Count > 0 ? a.UpLanes : b.UpLanes;
                    var downs = a.DownLanes.Count > 0 ? a.DownLanes : b.DownLanes;
                    if (ups.Count == 0 || downs.Count == 0) continue;
                    double w = 0;
                    foreach (var ul in ups)
                        foreach (var dl in downs)
                        {
                            double pw = PairWeight(ul, dl);
                            if (pw > w) w = pw;
                        }
                    if (w > 0) weightSamples.Add(w);
                }
            }
            double segPairW = weightSamples.Count > 0 ? weightSamples.Average() : IttnAnalyzerConfig.Alt.WMain;

            // --- 規則性係数 ---
            var iois = new List<double>();
            for (int k = 1; k < events.Count; k++) iois.Add(events[k].Time - events[k - 1].Time);
            var patterns = events.Select(ev => string.Join("+", ev.Lanes.OrderBy(x => x, StringComparer.Ordinal))).ToList();
            var reg = IttnAnalyzerCommon.RegularityCoef(iois, patterns, IttnAnalyzerConfig.Alt.RegMax, IttnAnalyzerConfig.Alt.RegCvFull, IttnAnalyzerConfig.Alt.RegPatBase);

            // --- イベント採点: 基礎点 × 音価重み × ペア種別 × 規則性 (× インセイン) ---
            double segScore = 0;
            long? prevT = null;
            foreach (var ev in events)
            {
                double wIoi = 1.0;
                if (prevT is { } pt)
                {
                    long ioi = ev.Time - pt;
                    if (ioi > 0)
                    {
                        wIoi = Math.Pow(IttnAnalyzerConfig.Alt.RefIoi / ioi, IttnAnalyzerConfig.Alt.IoiExp);
                        wIoi = Math.Min(IttnAnalyzerConfig.Alt.IoiWMax, Math.Max(IttnAnalyzerConfig.Alt.IoiWMin, wIoi));
                    }
                }
                prevT = ev.Time;
                double evScore = IttnAnalyzerConfig.Alt.BaseAlt * wIoi * segPairW * reg.Reg;
                if (ev.Insane) evScore *= IttnAnalyzerConfig.Alt.InsaneMult;
                segScore += evScore;
                scoredEvents.Add(new IttnPeakScoredEvent(ev.Time, ev.Tick, evScore));
            }
            totalAltScore += segScore;
        }

        // --- 集計: 局所max成分 + 総量成分 ---
        // 2026-07-25 BPM強化パート: PEAK_WINDOW(600F固定)→PeakWindowMeasures(8小節、[仮])。
        // per-minute正規化は「実際に採用された窓の実測フレーム経過時間」を使う(VOLTAGEと同じ理由。
        // IttnAnalyzerCommon.PeakWindowScoreByMeasureのdocコメント参照)。
        var pk = IttnAnalyzerCommon.PeakWindowScoreByMeasure(scoredEvents, engine, IttnAnalyzerConfig.Alt.PeakWindowMeasures);
        double peakWindowFrameSpan = Math.Max(1, pk.End - pk.Start);
        double altPeakPm = pk.Peak * 3600 / peakWindowFrameSpan;
        long lastFrame = noteList[^1].Time != 0 ? noteList[^1].Time : 1;
        double altTotalPm = totalAltScore / lastFrame * 3600;

        double rawVal = IttnAnalyzerConfig.Alt.PeakAlpha * altPeakPm + IttnAnalyzerConfig.Alt.VolBeta * altTotalPm;
        double valAlt = IttnAnalyzerCommon.FinalizeRadarValue(rawVal, IttnAnalyzerConfig.Alt.AltAt100, IttnAnalyzerConfig.Alt.AltAt200);

        return new IttnAltResult(valAlt, altPeakPm, altTotalPm, totalAltScore);
    }
}
