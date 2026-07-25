namespace DanoniEditor.Core.Analysis;

/// <summary>JACK計算結果(analyze.jsの`calcJack`戻り値のうちhighlightsを除いた部分)。</summary>
public readonly record struct IttnJackResult(double Val, double TotalJackScore, double Jpm, int MaxCombo);

/// <summary>
/// このファイルは analyzer_and_viewer/analyze.js (2026-07-25時点) の calcJack を移植したもの。
/// 今後analyze.js側を更新した場合、このファイルへの片方向バックポート要否を確認すること
/// (docs/progress_and_tbd_2026-07-24_add.md §1-1「片方向運用」)。
///
/// ■JACK: 縦連(同レーン連打)
/// ■計算内容:
///   縦連の難しさは同指の反復速度にほぼ支配される(速いほど急激に難化し、擦り等の誤魔化しが
///   効かずゲージが削れる=クリア難に直結)ため、速度重み speedW = (REF_GAP / 間隔)^SPEED_EXP を採用。
/// ■トリル判定:
///   同レーンL-L間に「Lを含まないユニット」が挟まれていればトリル=縦連除外。
///   L自身を含むユニットが挟まる場合は軸打ちなので縦連としてカウント。
/// ■コンボ累積:
///   n打目の追加重み = Σ(1.0 + COMBO_INC×i) for i=0..n-1。長い縦連ほど超線形に伸びる。
/// </summary>
public static class IttnAnalyzerJack
{
    /// <summary>同一フレームの打鍵をまとめた1打鍵単位(analyze.jsの`units`要素)。</summary>
    private sealed class Unit
    {
        public required long Time { get; init; }
        public required HashSet<string> Lanes { get; init; }
    }

    public static IttnJackResult Calculate(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame)
    {
        if (timeline.Count == 0 || playFrame <= 0)
            return new IttnJackResult(0, 0, 0, 0);

        // --- 1. 同フレームをユニット(打鍵単位)にまとめる ---
        var units = new List<Unit>();
        {
            int i = 0;
            while (i < timeline.Count)
            {
                var evt = timeline[i];
                if (evt.Type is not (IttnTimelineEventType.Normal or IttnTimelineEventType.FreezeStart)) { i++; continue; }
                long t = evt.Time;
                var lanes = new HashSet<string>();
                int j = i;
                while (j < timeline.Count && timeline[j].Time == t)
                {
                    var e = timeline[j];
                    if (e.Type is IttnTimelineEventType.Normal or IttnTimelineEventType.FreezeStart) lanes.Add(e.Lane!);
                    j++;
                }
                units.Add(new Unit { Time = t, Lanes = lanes });
                i = j;
            }
        }

        // --- 2. トリル判定 ---
        var unitIdxByTime = new Dictionary<long, int>();
        for (int idx = 0; idx < units.Count; idx++) unitIdxByTime[units[idx].Time] = idx;

        bool IsTrill(long prevTime, long currTime, string lane)
        {
            if (!unitIdxByTime.TryGetValue(prevTime, out var prevIdx)) return false;
            if (!unitIdxByTime.TryGetValue(currTime, out var currIdx)) return false;
            if (currIdx <= prevIdx + 1) return false; // 直接連続なら縦連
            for (int k = prevIdx + 1; k < currIdx; k++)
                if (units[k].Lanes.Contains(lane)) return false; // 軸打ち → 縦連
            return true; // 間に別レーンのみ → トリル
        }

        // --- 3. レーンごとに縦連を評価 ---
        var lastByLane = new Dictionary<string, (long Time, int Combo)>();
        double totalJackScore = 0;
        int maxCombo = 0;

        foreach (var unit in units)
        {
            foreach (var lane in unit.Lanes)
            {
                if (!lastByLane.TryGetValue(lane, out var prev))
                {
                    lastByLane[lane] = (unit.Time, 0);
                    continue;
                }

                long diff = unit.Time - prev.Time;
                int combo = prev.Combo;

                if (diff > 0 && diff <= IttnAnalyzerConfig.Jack.MaxGap)
                {
                    if (IsTrill(prev.Time, unit.Time, lane))
                    {
                        combo = 0;
                    }
                    else
                    {
                        combo++;
                        if (combo > maxCombo) maxCombo = combo;
                        // コンボ累積重み
                        double weight = 0, cur = 1.0;
                        for (int n = 0; n < combo; n++) { weight += cur; cur += IttnAnalyzerConfig.Jack.ComboInc; }
                        // 速度重み。間隔が短い(速い)縦連ほど急激に重くなる
                        double speedW = Math.Pow(IttnAnalyzerConfig.Jack.RefGap / diff, IttnAnalyzerConfig.Jack.SpeedExp);
                        totalJackScore += weight * speedW;
                    }
                }
                else
                {
                    combo = 0;
                }
                lastByLane[lane] = (unit.Time, combo);
            }
        }

        double jpm = totalJackScore / (playFrame / 3600);
        return new IttnJackResult(
            IttnAnalyzerCommon.FinalizeRadarValue(jpm, IttnAnalyzerConfig.Jack.JpmAt100, IttnAnalyzerConfig.Jack.JpmAt200),
            totalJackScore, jpm, maxCombo);
    }
}
