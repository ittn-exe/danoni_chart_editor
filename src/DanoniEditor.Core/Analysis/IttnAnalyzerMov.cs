using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Analysis;

/// <summary>MOV計算結果(analyze.jsの`calculateMovLevel`戻り値のうちhighlights/segDebug/discardCandidatesを
/// 除いた部分。DISCARD(捨て予算解析)はスコープ外のためmissTarget収集自体を行わない)。</summary>
public readonly record struct IttnMovResult(
    double ValMov, double MovPeakPm, double MovTotalPm, double TotalMovScore,
    int MoveCount, int ExcursionCount, int StretchCount, int FingerCount);

/// <summary>
/// このファイルは analyzer_and_viewer/analyze.js (2026-07-25時点) の calculateMovLevel を移植したもの。
/// 今後analyze.js側を更新した場合、このファイルへの片方向バックポート要否を確認すること
/// (docs/progress_and_tbd_2026-07-24_add.md §1-1「片方向運用」)。
/// DISCARD(捨て予算解析、実験的機能でbaseRating/totalRatingに不算入)向けのmissTarget収集・
/// ハイライト収集(highlightsExt)はスコープ外のため移植していない。
///
/// ■MOV: 手の移動負荷 v5「想定運指の破壊」統一モデル(出張⊂移動先)
/// ■このツールにおけるMOVの定義:
///   「縦1列=1本指、ホームに手」という想定運指からの逸脱コスト。
/// ■破壊の段階と検出:
///   1) 指衝突: 縦隣接キー(列差≤0.6, 行差1)の同時押し = 同指担当の衝突
///   2)〜3) 段シフト/腕移動: 手ごとのポジション追跡。移動コストは物理距離の連続関数
///   4) 変則姿勢(ストレッチ): 同一手の複数持ち場が真に同時(時間差≤MOVE_TIME)要求され、
///      実ノーツ間距離がEXC_MIN_DIST〜STRETCH_MAXの帯域なら高コスト移動として課金
///   5) 出張: 実ノーツ間距離がSTRETCH_MAX超 = 片手で捌けない
/// ■集計: valMOV = PEAK_ALPHA×局所max成分 + VOL_BETA×総量成分
/// </summary>
public static class IttnAnalyzerMov
{
    private static readonly IttnMovResult Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class RawEvent
    {
        public required long Time { get; init; }
        public required long Tick { get; init; }
        public required double BaseScore { get; init; }
        public required string Type { get; init; }
        public required string Pattern { get; init; }
        public double Score { get; set; }
    }

    private sealed class HandState
    {
        public required string Pos { get; set; }
        public long? LastNoteFrame { get; set; }
        public string? LastSimulKey { get; set; }
        public long? LastSimulFrame { get; set; }
        public bool HadEventSinceSimul { get; set; }
        public bool SimulJustEnded { get; set; }
        public Dictionary<string, long> ShiftLast { get; } = [];
    }

    private static (string Ud, string Lr) ParseGroupDir(string? grpName)
    {
        var parts = (grpName ?? "").Split('_');
        string ud = parts.Length > 0 ? parts[0] : "";
        string lr = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "center";
        return (ud, lr);
    }

    public static IttnMovResult Calculate(IReadOnlyList<IttnTimelineEvent> timeline, string keyTypeId, TimingEngine engine)
    {
        if (!IttnAnalyzerKeyMapData.Entries.TryGetValue(keyTypeId, out var currentKeyMap) || !currentKeyMap.LaneMov)
            return Empty;

        var laneInfo = currentKeyMap.Lanes; // lane -> {Group, Pos}(Scrollは未使用)

        // --- グループ重心と左右属性の構築 ---
        var groupNames = laneInfo.Values.Select(v => v.Group).Where(g => g != null).Select(g => g!).Distinct().ToList();
        var groupInfo = new Dictionary<string, (string Lr, (double X, double Y)? Centroid)>();
        foreach (var grpName in groupNames)
        {
            var dir = ParseGroupDir(grpName);
            var poss = laneInfo.Values.Where(v => v.Group == grpName && v.Pos != null).Select(v => v.Pos!.Value).ToList();
            (double X, double Y)? centroid = poss.Count > 0 ? (poss.Average(p => p.X), poss.Average(p => p.Y)) : null;
            groupInfo[grpName] = (dir.Lr, centroid);
        }

        double GDist(string ga, string gb)
        {
            var a = groupInfo.TryGetValue(ga, out var gia) ? gia.Centroid : null;
            var b = groupInfo.TryGetValue(gb, out var gib) ? gib.Centroid : null;
            if (a is null || b is null) return IttnAnalyzerConfig.Mov.RefArmDist; // pos欠落時は基準距離扱い(係数1.0)
            return Math.Sqrt(Math.Pow(a.Value.X - b.Value.X, 2) + Math.Pow(a.Value.Y - b.Value.Y, 2));
        }

        double LandCoefOf(string grp)
        {
            var centroid = groupInfo.TryGetValue(grp, out var gi) ? gi.Centroid : null;
            if (centroid is null) return 1.0;
            return (centroid.Value.Y >= 2 && centroid.Value.Y <= 4.5 && centroid.Value.X < 14) ? IttnAnalyzerConfig.Mov.LandLetter : 1.0;
        }

        var ownGroups = new Dictionary<string, List<string>> { ["left"] = [], ["right"] = [] };
        var centerGroups = new List<string>();
        foreach (var (grp, info) in groupInfo)
        {
            if (info.Lr == "left") ownGroups["left"].Add(grp);
            else if (info.Lr == "right") ownGroups["right"].Add(grp);
            else centerGroups.Add(grp);
        }
        var activeSides = new[] { "left", "right" }.Where(s => ownGroups[s].Count > 0 || centerGroups.Count > 0).ToList();
        if (activeSides.Count == 0) return Empty;
        string SideOf(string grp) => groupInfo.TryGetValue(grp, out var gi) ? gi.Lr : "center";

        var noteList = timeline.Where(n => n.Type is not (IttnTimelineEventType.SpeedChange or IttnTimelineEventType.FreezeEnd)).ToList();
        if (noteList.Count == 0) return Empty;

        // --- フレーム索引の構築 ---
        var frameGroups = new Dictionary<long, HashSet<string>>();
        var frameLanes = new Dictionary<long, List<(string Lane, string? Group, (double X, double Y)? Pos)>>();
        // frameTick: 同一フレームに丸められたノーツ群の代表tick(2026-07-25 BPM強化パートで追加。
        // SIMUL_WINDOWのtick基準判定に使う。同一フレームに複数tickが丸め込まれる稀なケースでは
        // 最初に出現したノーツのtickを代表値とする(実用上、フレーム内での差は無視できる))。
        var frameTick = new Dictionary<long, long>();
        foreach (var n in noteList)
        {
            var info = laneInfo.TryGetValue(n.Lane!, out var li) ? li : new AnalyzerLaneInfo(null, "down", null);
            if (!frameLanes.TryGetValue(n.Time, out var list)) { list = []; frameLanes[n.Time] = list; }
            list.Add((n.Lane!, info.Group, info.Pos));
            if (!frameTick.ContainsKey(n.Time)) frameTick[n.Time] = n.Tick;
            if (info.Group is null) continue;
            if (!frameGroups.TryGetValue(n.Time, out var set)) { set = []; frameGroups[n.Time] = set; }
            set.Add(info.Group);
        }
        var frameArr = frameGroups.Keys.OrderBy(t => t).ToList();
        var grpArr = frameArr.Select(f => frameGroups[f]).ToList();
        var tickArr = frameArr.Select(f => frameTick[f]).ToList();

        int LbFrame(long lo)
        {
            int l = 0, r = frameArr.Count;
            while (l < r) { int m = (l + r) >> 1; if (frameArr[m] < lo) l = m + 1; else r = m; }
            return l;
        }

        // 2026-07-25 BPM強化パート: SIMUL_WINDOW(15F固定)→SimulWindowBeats(2拍、[仮])。
        // 拍=四分音符=TicksPerBeat tickという固定単位のため(拍子の分母に依存しない一定長)、
        // ここは小節基準のPeakWindow等と異なりTimingEngineの拍子参照が不要。
        int LbTick(long lo)
        {
            int l = 0, r = tickArr.Count;
            while (l < r) { int m = (l + r) >> 1; if (tickArr[m] < lo) l = m + 1; else r = m; }
            return l;
        }

        // RC4(v5): 出張/ストレッチのゲート距離は「実際にその時刻に要求されたノーツ同士の
        // レーン間最小距離」で測る(グループ重心では変則クラスタの実距離を見誤るため)。
        double GDistNotes(string groupA, long frameA, string groupB, long frameB)
        {
            var lanesA = (frameLanes.TryGetValue(frameA, out var la) ? la : []).Where(n => n.Group == groupA && n.Pos != null).ToList();
            var lanesB = (frameLanes.TryGetValue(frameB, out var lb) ? lb : []).Where(n => n.Group == groupB && n.Pos != null).ToList();
            if (lanesA.Count == 0 || lanesB.Count == 0) return GDist(groupA, groupB);
            double min = double.PositiveInfinity;
            foreach (var a in lanesA)
                foreach (var b in lanesB)
                {
                    double d = Math.Sqrt(Math.Pow(a.Pos!.Value.X - b.Pos!.Value.X, 2) + Math.Pow(a.Pos!.Value.Y - b.Pos!.Value.Y, 2));
                    if (d < min) min = d;
                }
            return min;
        }

        // 経由点: 区間(fromF, toF)内に「移動する手の固有持ち場以外」のノーツがあるか
        bool HasWaypoint(long fromF, long toF, string side)
        {
            for (int wi = LbFrame(fromF + 1); wi < frameArr.Count && frameArr[wi] < toF; wi++)
                foreach (var g in grpArr[wi])
                    if (SideOf(g) != side) return true;
            return false;
        }

        // --- パス1: イベント検出 ---
        var rawEvents = new List<RawEvent>();
        int excursionCount = 0, fingerCount = 0, stretchCount = 0;
        long? prevEventFrame = null;

        var hand = new Dictionary<string, HandState>();
        foreach (var s in activeSides)
        {
            string? initPos = ownGroups[s].FirstOrDefault(g => ParseGroupDir(g).Ud == "down")
                             ?? ownGroups[s].FirstOrDefault()
                             ?? centerGroups.FirstOrDefault();
            hand[s] = new HandState { Pos = initPos! };
        }
        var fingerLast = new Dictionary<string, long>();

        for (int fi = 0; fi < frameArr.Count; fi++)
        {
            long frame = frameArr[fi];
            long tick = tickArr[fi];
            var grpSet = grpArr[fi];
            var lanesHere = frameLanes.TryGetValue(frame, out var lh) ? lh : [];

            // ==== 1) 指衝突検出(縦隣接ペアの同時押し。手の状態とは独立) ====
            {
                string? firedPairKey = null;
                for (int a = 0; a < lanesHere.Count && firedPairKey is null; a++)
                {
                    for (int b = a + 1; b < lanesHere.Count; b++)
                    {
                        var pa = lanesHere[a].Pos;
                        var pb = lanesHere[b].Pos;
                        if (pa is null || pb is null) continue;
                        if (Math.Abs(pa.Value.X - pb.Value.X) <= 0.6 && Math.Abs(pa.Value.Y - pb.Value.Y) == 1)
                        {
                            var pair = new[] { lanesHere[a].Lane, lanesHere[b].Lane }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                            firedPairKey = pair[0] + "|" + pair[1];
                            break;
                        }
                    }
                }
                if (firedPairKey is not null)
                {
                    bool cooldownActive = fingerLast.TryGetValue(firedPairKey, out var lastF) && frame - lastF <= IttnAnalyzerConfig.Mov.FingerCooldown;
                    if (!cooldownActive)
                    {
                        long elapsed = prevEventFrame is { } pef ? Math.Max(1, frame - pef) : (long)IttnAnalyzerConfig.Mov.SpeedThreshold;
                        double sw = Math.Max(1.0, IttnAnalyzerConfig.Mov.SpeedThreshold / elapsed);
                        double baseScore = IttnAnalyzerConfig.Mov.CostFinger * sw;
                        fingerCount++;
                        rawEvents.Add(new RawEvent { Time = frame, Tick = tick, BaseScore = baseScore, Type = "finger", Pattern = "FNG:" + firedPairKey });
                        prevEventFrame = frame;
                    }
                    fingerLast[firedPairKey] = frame;
                }
            }

            // ==== 2) centerグループの手割り当て(近い方の手へ貪欲) ====
            var sideDemand = new Dictionary<string, HashSet<string>> { ["left"] = [], ["right"] = [] };
            foreach (var g in grpSet)
            {
                string lr = SideOf(g);
                if (lr is "left" or "right")
                {
                    if (activeSides.Contains(lr)) sideDemand[lr].Add(g);
                }
                else
                {
                    string? best = null;
                    double bestD = double.PositiveInfinity;
                    foreach (var s in activeSides)
                    {
                        double d = GDist(hand[s].Pos, g);
                        if (d < bestD - 1e-9 || (Math.Abs(d - bestD) < 1e-9 && s == "right"))
                        {
                            best = s;
                            bestD = d;
                        }
                    }
                    if (best is not null) sideDemand[best].Add(g);
                }
            }

            // ==== 3) 手ごとの出張/移動判定 ====
            foreach (var s in activeSides)
            {
                var H = hand[s];
                var demand = sideDemand[s];
                bool hasAnyDemand = demand.Count > 0;

                bool winOtherBusy = false;
                Dictionary<string, long>? ownFrameOf = null;
                if (ownGroups[s].Count >= 2)
                {
                    ownFrameOf = [];
                    long simulWindowTicks = (long)IttnAnalyzerConfig.Mov.SimulWindowBeats * TimingEngine.TicksPerBeat;
                    int wLo = LbTick(tick - simulWindowTicks);
                    for (int wi = wLo; wi <= fi; wi++)
                    {
                        foreach (var g in grpArr[wi])
                        {
                            string lr = SideOf(g);
                            if (lr == s) ownFrameOf[g] = frameArr[wi];
                            else if (lr != "center") winOtherBusy = true;
                        }
                    }
                }

                bool isSimul = false, isStretch = false;
                HashSet<string>? winOwn = null;
                string[]? stretchPair = null;
                if (ownFrameOf is not null && ownFrameOf.Count >= 2)
                {
                    var owns = ownFrameOf.Keys.ToList();
                    string[]? bestPair = null;
                    double bestD = 0;
                    for (int a = 0; a < owns.Count; a++)
                        for (int b = a + 1; b < owns.Count; b++)
                        {
                            long fa = ownFrameOf[owns[a]], fb = ownFrameOf[owns[b]];
                            long timeDiff = Math.Abs(fa - fb);
                            if (timeDiff > IttnAnalyzerConfig.Mov.MoveTime) continue; // RC1: 移動で解決可能→出張候補から除外
                            double d = GDistNotes(owns[a], fa, owns[b], fb); // RC4: 実ノーツ距離
                            if (d > bestD) { bestD = d; bestPair = [owns[a], owns[b]]; }
                        }
                    if (bestPair is not null && bestD > IttnAnalyzerConfig.Mov.StretchMax)
                    {
                        isSimul = true;
                        winOwn = [.. bestPair];
                    }
                    else if (bestPair is not null && bestD > IttnAnalyzerConfig.Mov.ExcMinDist)
                    {
                        isStretch = true;
                        stretchPair = bestPair;
                    }
                }

                if (isSimul)
                {
                    string simulKey = string.Join("+", winOwn!.OrderBy(x => x, StringComparer.Ordinal));
                    long elapsed = prevEventFrame is { } pef2 ? Math.Max(1, frame - pef2) : (long)IttnAnalyzerConfig.Mov.SpeedThreshold;
                    bool isCooldownSkip = H.LastSimulKey == simulKey
                                          && H.LastSimulFrame is { } lsf
                                          && (frame - lsf) <= IttnAnalyzerConfig.Mov.SimulCooldown
                                          && !H.HadEventSinceSimul;
                    if (!isCooldownSkip)
                    {
                        double baseVal = winOtherBusy ? IttnAnalyzerConfig.Mov.CostSimulDl : IttnAnalyzerConfig.Mov.CostSimul;
                        double sw = Math.Max(1.0, IttnAnalyzerConfig.Mov.SpeedThreshold / elapsed);
                        double baseScore = baseVal * sw + IttnAnalyzerConfig.Mov.BaseExcursion;
                        excursionCount++;
                        rawEvents.Add(new RawEvent { Time = frame, Tick = tick, BaseScore = baseScore, Type = "excursion", Pattern = "EXC:" + s + ":" + simulKey });
                        prevEventFrame = frame;
                        H.LastSimulKey = simulKey;
                        H.LastSimulFrame = frame;
                        H.HadEventSinceSimul = false;
                    }
                    H.SimulJustEnded = true;
                    if (hasAnyDemand) H.LastNoteFrame = frame;
                    continue;
                }

                if (isStretch)
                {
                    string shiftKey = "STRETCH:" + s + ":" + string.Join("|", stretchPair!.OrderBy(x => x, StringComparer.Ordinal));
                    bool hasLastStretch = H.ShiftLast.TryGetValue(shiftKey, out var lastStretch);
                    H.ShiftLast[shiftKey] = frame;
                    if (!hasLastStretch || frame - lastStretch > IttnAnalyzerConfig.Mov.ShiftCooldown)
                    {
                        long elapsed = prevEventFrame is { } pef3 ? Math.Max(1, frame - pef3) : (long)IttnAnalyzerConfig.Mov.SpeedThreshold;
                        double sw = Math.Max(1.0, IttnAnalyzerConfig.Mov.SpeedThreshold / elapsed);
                        double baseScore = IttnAnalyzerConfig.Mov.CostStretch * sw;
                        stretchCount++;
                        rawEvents.Add(new RawEvent
                        {
                            Time = frame,
                            Tick = tick,
                            BaseScore = baseScore,
                            Type = "stretch",
                            Pattern = "STR:" + s + ":" + string.Join("+", stretchPair!.OrderBy(x => x, StringComparer.Ordinal)),
                        });
                        prevEventFrame = frame;
                    }
                    H.SimulJustEnded = true;
                    if (hasAnyDemand) H.LastNoteFrame = frame;
                    continue;
                }

                // simul直後: 実際の単独要求から手の位置を確定(コスト加算なし)
                if (H.SimulJustEnded)
                {
                    if (demand.Count == 1)
                    {
                        H.Pos = demand.First();
                        H.SimulJustEnded = false;
                    }
                    if (hasAnyDemand) H.LastNoteFrame = frame;
                    continue;
                }

                // 移動判定: 単一の要求が現在位置と異なる
                if (demand.Count == 1)
                {
                    string target = demand.First();
                    if (target != H.Pos)
                    {
                        double dist = GDist(H.Pos, target);
                        bool withinSpan = dist <= IttnAnalyzerConfig.Mov.ExcMinDist;
                        if (withinSpan)
                        {
                            string shiftKey = s + ":" + string.Join("|", new[] { H.Pos, target }.OrderBy(x => x, StringComparer.Ordinal));
                            bool hasLastShift = H.ShiftLast.TryGetValue(shiftKey, out var lastShift);
                            H.ShiftLast[shiftKey] = frame;
                            if (hasLastShift && frame - lastShift <= IttnAnalyzerConfig.Mov.ShiftCooldown)
                            {
                                // クールダウン中: 構えの内側の往復。位置だけ更新しコスト無し
                                H.Pos = target;
                                if (hasAnyDemand) H.LastNoteFrame = frame;
                                continue;
                            }
                        }
                        long elapsed = prevEventFrame is { } pef4 ? Math.Max(1, frame - pef4) : (long)IttnAnalyzerConfig.Mov.SpeedThreshold;
                        double sw = withinSpan ? 1.0 : Math.Max(1.0, IttnAnalyzerConfig.Mov.SpeedThreshold / elapsed);
                        double distCoef = Math.Min(IttnAnalyzerConfig.Mov.DistMax, Math.Max(IttnAnalyzerConfig.Mov.DistMin, Math.Pow(dist / IttnAnalyzerConfig.Mov.RefArmDist, IttnAnalyzerConfig.Mov.MoveDistExp)));
                        double landCoef = LandCoefOf(target);
                        bool direct = H.LastNoteFrame is { } lnf && !HasWaypoint(lnf, frame, s);
                        double baseScore = IttnAnalyzerConfig.Mov.CostMove * distCoef * sw * landCoef * (direct ? IttnAnalyzerConfig.Mov.DirectMult : 1.0);
                        rawEvents.Add(new RawEvent
                        {
                            Time = frame,
                            Tick = tick,
                            BaseScore = baseScore,
                            Type = "move",
                            Pattern = "MOV:" + s + ":" + target + (direct ? ":d" : "") + (withinSpan ? ":s" : ""),
                        });
                        prevEventFrame = frame;
                        H.Pos = target;
                        H.HadEventSinceSimul = true;
                    }
                }
                // else demand.Count>=2: 固有1種+center等の複数要求(出張未満)。位置は据え置き、コスト無し

                if (hasAnyDemand) H.LastNoteFrame = frame;
            }
        }

        if (rawEvents.Count == 0) return Empty;
        rawEvents = rawEvents.OrderBy(e => e.Time).ToList();

        // --- パス2: 不意打ち係数(区間ごとの規則性)を乗算 ---
        var scoredEvents = new List<IttnPeakScoredEvent>();
        double totalMovScore = 0;
        {
            int segStart = 0;
            for (int i = 1; i <= rawEvents.Count; i++)
            {
                bool isBreak = i == rawEvents.Count || rawEvents[i].Time - rawEvents[i - 1].Time > IttnAnalyzerConfig.Mov.MovSegGap;
                if (!isBreak) continue;
                var segEvents = rawEvents.GetRange(segStart, i - segStart);
                var intervals = new List<double>();
                for (int k = 1; k < segEvents.Count; k++) intervals.Add(segEvents[k].Time - segEvents[k - 1].Time);
                var patterns = segEvents.Select(e => e.Pattern).ToList();
                // イベント3未満の区間は不意打ち係数を測れないため1.0固定
                var regResult = segEvents.Count >= 3
                    ? IttnAnalyzerCommon.RegularityCoef(intervals, patterns, IttnAnalyzerConfig.Mov.RegMax, IttnAnalyzerConfig.Mov.RegCvFull, IttnAnalyzerConfig.Mov.RegPatBase)
                    : new IttnRegularityResult(1.0, 0, 0);

                foreach (var e in segEvents)
                {
                    e.Score = e.BaseScore * regResult.Reg;
                    totalMovScore += e.Score;
                    scoredEvents.Add(new IttnPeakScoredEvent(e.Time, e.Tick, e.Score));
                }
                segStart = i;
            }
        }

        // --- 集計: 局所max成分 + 総量成分 ---
        // 2026-07-25 BPM強化パート: PEAK_WINDOW(600F固定)→PeakWindowMeasures(8小節、[仮])。
        // per-minute正規化は「実際に採用された窓の実測フレーム経過時間」を使う(VOLTAGE/ALTと同じ理由。
        // IttnAnalyzerCommon.PeakWindowScoreByMeasureのdocコメント参照)。
        var pk = IttnAnalyzerCommon.PeakWindowScoreByMeasure(scoredEvents, engine, IttnAnalyzerConfig.Mov.PeakWindowMeasures);
        double peakWindowFrameSpan = Math.Max(1, pk.End - pk.Start);
        double movPeakPm = pk.Peak * 3600 / peakWindowFrameSpan;
        long lastFrame = noteList[^1].Time != 0 ? noteList[^1].Time : 1;
        double movTotalPm = totalMovScore / lastFrame * 3600;

        double rawVal = IttnAnalyzerConfig.Mov.PeakAlpha * movPeakPm + IttnAnalyzerConfig.Mov.VolBeta * movTotalPm;

        return new IttnMovResult(
            IttnAnalyzerCommon.FinalizeRadarValue(rawVal, IttnAnalyzerConfig.Mov.MovAt100, IttnAnalyzerConfig.Mov.MovAt200),
            movPeakPm, movTotalPm, totalMovScore,
            rawEvents.Count, excursionCount, stretchCount, fingerCount);
    }
}
