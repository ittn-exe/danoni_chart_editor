namespace DanoniEditor.Core.Import;

/// <summary>BPM推定候補</summary>
/// <param name="Bpm">推定BPM</param>
/// <param name="Phase">グリッド位相(小節0の頭に相当する絶対フレーム。格子として等価な位置のうちの1つ)</param>
/// <param name="InlierRatio">16分グリッド±0.55Fに乗ったノートの割合(1.0=全ノート一致)</param>
public sealed record TimingCandidate(double Bpm, double Phase, double InlierRatio);

/// <summary>
/// dos.txtのフレーム列からBPM(四分間隔)を自動推定する(仕様書15.2のBPM欠落問題への発展対応)。
/// 原理: dosのフレームは「正確なグリッドの整数丸め(±0.5F)」なので、
/// BPM候補を走査して「各ノートの最寄り16分グリッドからの距離が0.55F以内」となる割合
/// (インライア率)を採点すれば、正しいBPMだけが突出して高得点になる。
/// 実データ検証: by_node(530ノート)→BPM185.0を100%一致で検出(次点34.9%)、
/// skb_test(539ノート・位相未知)→BPM176.0を100%一致で検出。
/// 制約: 単一BPM前提(ソフラン譜面は区間分割が必要、将来対応)。
/// 「どのグリッド点が1拍目か」は原理的に決定不能なので、位相は格子等価な代表値。
/// </summary>
public static class DosTimingEstimator
{
    /// <summary>
    /// フレーム列からBPM候補を推定する(インライア率の高い順)。
    /// </summary>
    /// <param name="frames">全ノート・フリーズ端点等の絶対フレーム列</param>
    /// <param name="anchorPhase">位相の基準値(通常はblankFrame。FUJI系はここが位相そのもの)</param>
    /// <param name="searchPhase">falseならanchorPhaseを位相として固定(FUJI系)、trueなら位相も探索</param>
    public static List<TimingCandidate> Estimate(
        IReadOnlyList<double> frames, double anchorPhase,
        bool searchPhase, double bpmMin = 100, double bpmMax = 200)
    {
        if (frames.Count < 8)
            return []; // 統計的に信頼できるノート数が無い

        var candidates = new List<TimingCandidate>();

        // --- 粗走査: BPM 0.1刻み ---
        for (double bpm = bpmMin; bpm <= bpmMax + 1e-9; bpm = Math.Round(bpm + 0.1, 4))
        {
            double g = 900.0 / bpm; // 16分間隔(=四分間隔/4)
            if (searchPhase)
            {
                // 位相はanchorから1拍(4g)ぶんを0.5F刻みで走査
                double bestRatio = -1, bestPhase = anchorPhase;
                for (double p = anchorPhase; p < anchorPhase + 4 * g; p += 0.5)
                {
                    double r = InlierRatio(frames, p, g);
                    if (r > bestRatio) { bestRatio = r; bestPhase = p; }
                }
                candidates.Add(new TimingCandidate(bpm, bestPhase, bestRatio));
            }
            else
            {
                candidates.Add(new TimingCandidate(bpm, anchorPhase, InlierRatio(frames, anchorPhase, g)));
            }
        }

        // --- 上位候補を最小二乗でリファイン(粗走査の量子化誤差による曲末尾ドリフトを除去) ---
        var top = candidates.OrderByDescending(c => c.InlierRatio).Take(5)
            .Select(c => Refine(frames, c, searchPhase))
            .OrderByDescending(c => c.InlierRatio)
            .ThenBy(c => Math.Abs(c.Phase - anchorPhase))
            .ToList();

        // BPM重複(隣接刻みが同値へ収束)を除去
        var result = new List<TimingCandidate>();
        foreach (var c in top)
            if (!result.Any(r => Math.Abs(r.Bpm - c.Bpm) < 0.05))
                result.Add(c);
        return result;
    }

    private static double InlierRatio(IReadOnlyList<double> frames, double phase, double g)
    {
        int inl = 0;
        foreach (var f in frames)
        {
            double r = (f - phase) % g;
            if (r < 0) r += g;
            if (Math.Min(r, g - r) <= 0.55) inl++;
        }
        return (double)inl / frames.Count;
    }

    /// <summary>
    /// インライアに対する最小二乗フィットで(位相, 16分間隔)を高精度化し、
    /// 「キリの良いBPM」(0.05刻み)がインライア率を落とさなければそちらへスナップする。
    /// </summary>
    private static TimingCandidate Refine(IReadOnlyList<double> frames, TimingCandidate c, bool fitPhase)
    {
        double g = 900.0 / c.Bpm, p = c.Phase;

        for (int iter = 0; iter < 3; iter++)
        {
            // 各フレームをグリッドインデックスkへ割当て、インライアのみで f ≈ p + k·g を回帰
            var pts = new List<(double k, double f)>();
            foreach (var f in frames)
            {
                double k = Math.Round((f - p) / g);
                if (Math.Abs(f - (p + k * g)) <= 0.55) pts.Add((k, f));
            }
            if (pts.Count < 8) break;

            if (fitPhase)
            {
                double mk = pts.Average(x => x.k), mf = pts.Average(x => x.f);
                double sxx = pts.Sum(x => (x.k - mk) * (x.k - mk));
                if (sxx < 1e-9) break;
                double slope = pts.Sum(x => (x.k - mk) * (x.f - mf)) / sxx;
                g = slope;
                p = mf - slope * mk;
            }
            else
            {
                // 位相固定: g のみ最適化 g = Σk(f−p)/Σk²
                double num = pts.Sum(x => x.k * (x.f - p));
                double den = pts.Sum(x => x.k * x.k);
                if (den < 1e-9) break;
                g = num / den;
            }
        }

        double bpm = 900.0 / g;
        double ratio = InlierRatio(frames, p, g);

        // キリの良いBPM(0.05刻み)へのスナップを試す
        double snapped = Math.Round(bpm * 20) / 20.0;
        if (Math.Abs(snapped - bpm) > 1e-9)
        {
            double gs = 900.0 / snapped;
            double rs = InlierRatio(frames, p, gs);
            if (rs >= ratio - 1e-9) { bpm = snapped; g = gs; ratio = rs; }
        }

        return new TimingCandidate(bpm, p, ratio);
    }
}
