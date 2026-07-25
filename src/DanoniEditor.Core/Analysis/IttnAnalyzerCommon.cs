using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Analysis;

/// <summary>局所難所抽出(<see cref="IttnAnalyzerCommon.PeakWindowScore"/>/
/// <see cref="IttnAnalyzerCommon.PeakWindowScoreByMeasure"/>)に渡す1イベント分の
/// {time, score}(analyze.jsの同名オブジェクトに対応)。
/// Tick(2026-07-25 BPM強化パートで追加)は<see cref="PeakWindowScoreByMeasure"/>専用で、
/// 窓幅を小節数で判定する際の境界判定にのみ使う。</summary>
public readonly record struct IttnPeakScoredEvent(long Time, long Tick, double Score);

/// <summary>peakWindowScoreの戻り値。窓内スコア合計の最大値と、その窓の開始・終了時刻。</summary>
public readonly record struct IttnPeakWindowResult(double Peak, long Start, long End);

/// <summary>regularityCoefの戻り値。reg=1.0(規則的)〜REG_MAX(完全に不規則)と内訳。</summary>
public readonly record struct IttnRegularityResult(double Reg, double Cv, double PatRatio);

/// <summary>
/// analyze.jsの共通ユーティリティ(clamp / peakWindowScore / regularityCoef / finalizeRadarValue)の
/// 移植。ALT/MOV/JACK/6軸レーダー計算の全てから共有される。
/// </summary>
public static class IttnAnalyzerCommon
{
    /// <summary>値を[min, max]に収める(analyze.jsの`clamp`)。</summary>
    public static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));

    /// <summary>
    /// ■peakWindowScore: 局所難所の抽出(analyze.js同名関数の移植)。
    /// ■入力: 時間順の{time, score}配列 / 窓幅(F)
    /// ■出力: 窓内スコア合計の最大値と、その窓の開始・終了時刻
    /// ■設計意図: ALT/MOVの「局所難所基準」集計に使う。
    /// </summary>
    public static IttnPeakWindowResult PeakWindowScore(IReadOnlyList<IttnPeakScoredEvent> events, double windowF)
    {
        if (events.Count == 0) return new IttnPeakWindowResult(0, 0, 0);
        double best = 0;
        long bestStart = events[0].Time, bestEnd = events[0].Time;
        double sum = 0;
        int tail = 0;
        for (int head = 0; head < events.Count; head++)
        {
            sum += events[head].Score;
            while (events[head].Time - events[tail].Time > windowF)
            {
                sum -= events[tail].Score;
                tail++;
            }
            if (sum > best)
            {
                best = sum;
                bestStart = events[tail].Time;
                bestEnd = events[head].Time;
            }
        }
        return new IttnPeakWindowResult(best, bestStart, bestEnd);
    }

    /// <summary>
    /// ■指定tickの時点で有効な拍子から、N小節ぶんのtick数を求める(2026-07-25 BPM強化パートで追加)。
    /// <see cref="TimingEngine.SignatureAt"/>が既に「拍子イベントが無ければ4/4扱い」という
    /// エディタ既定のフォールバックを内包しているため、ここで独自にデフォルト拍子を持つ必要はない
    /// (<see cref="TimingEngine"/>のコンストラクタ参照。拍子イベント列の先頭がtick 0でなければ
    /// 4/4を自動挿入する)。
    /// </summary>
    public static long MeasureTicks(TimingEngine engine, long atTick, double measureCount)
        => (long)Math.Round(measureCount * engine.SignatureAt(atTick).TicksPerMeasure);

    /// <summary>
    /// ■peakWindowScoreByMeasure: 局所難所の抽出、小節基準版(2026-07-25 BPM強化パートで追加。
    /// <see cref="PeakWindowScore"/>のフレーム固定窓版に対応する、tick/小節基準の窓版)。
    /// ■入力: 時間順の{time, tick, score}配列 / タイミングエンジン / 窓幅(小節数、[仮]値)
    /// ■窓境界の判定: 「tail側イベントの時点で有効な拍子」を基準に、tail.Tick〜tail.Tick+N小節分tick
    /// を窓とする(head.Tickがこれを超えたらtailを進める、古典的な二本指スライディング窓)。
    /// 拍子が曲中で変わる場合、tail側の拍子で窓幅を決めるため厳密には非単調(windowの終端tickが
    /// 位置によって微妙に前後しうる)だが、拍子変化はノーツ密度に比べ遥かに低頻度であり、
    /// 元のanalyze.js版(固定フレーム窓の単純スライド)と同じ「実用上十分な近似」として許容する。
    /// ■出力: 窓内スコア合計の最大値と、その窓の開始・終了「フレーム」時刻(=窓境界となった
    /// tail/headイベント自身のTime)。呼び出し側はEnd-Startを「その窓の実測フレーム経過時間」として
    /// per-minute正規化に使う(固定フレーム除数SECTION_SIZE/PEAK_WINDOWの代わり。窓が小節基準になり
    /// 実フレーム長がテンポで変動するため、正規化にも実測値を使うのが数量的に一貫している)。
    /// </summary>
    public static IttnPeakWindowResult PeakWindowScoreByMeasure(
        IReadOnlyList<IttnPeakScoredEvent> events, TimingEngine engine, double measureCount)
    {
        if (events.Count == 0) return new IttnPeakWindowResult(0, 0, 0);
        double best = 0;
        long bestStart = events[0].Time, bestEnd = events[0].Time;
        double sum = 0;
        int tail = 0;
        for (int head = 0; head < events.Count; head++)
        {
            sum += events[head].Score;
            while (true)
            {
                long windowTicks = MeasureTicks(engine, events[tail].Tick, measureCount);
                if (events[head].Tick - events[tail].Tick > windowTicks)
                {
                    sum -= events[tail].Score;
                    tail++;
                }
                else break;
            }
            if (sum > best)
            {
                best = sum;
                bestStart = events[tail].Time;
                bestEnd = events[head].Time;
            }
        }
        return new IttnPeakWindowResult(best, bestStart, bestEnd);
    }

    /// <summary>
    /// ■規則性係数(ALT/MOV共用、analyze.jsの`regularityCoef`の移植)。
    /// ■入力: イベント間隔の配列 / パターンラベルの配列 / REG_MAX・REG_CV_FULL・REG_PAT_BASE
    /// ■出力: reg=1.0(規則的)〜REG_MAX(完全に不規則)と内訳(cv, patRatio)
    /// ■計算内容:
    ///   指標1: 間隔の変動係数CV。交互押し・等間隔移動ならCV≒0
    ///   指標2: パターン多様性。イベントの構成が毎回違うほど高い
    ///   → maxを採用(どちらか一方でも不規則性を検出したら上乗せ)
    /// </summary>
    public static IttnRegularityResult RegularityCoef(
        IReadOnlyList<double> intervals, IReadOnlyList<string> patterns,
        double regMax, double regCvFull, double regPatBase)
    {
        double cv = 0;
        if (intervals.Count >= 2)
        {
            double mean = intervals.Sum() / intervals.Count;
            if (mean > 0)
            {
                double varr = intervals.Sum(b => (b - mean) * (b - mean)) / intervals.Count;
                cv = Math.Sqrt(varr) / mean;
            }
        }
        var patSet = new HashSet<string>(patterns);
        double patRatio = patterns.Count > 0 ? (double)patSet.Count / patterns.Count : 0;

        double cvNorm = Math.Min(1, cv / regCvFull);
        double patNorm = Math.Max(0, Math.Min(1, (patRatio - regPatBase) / (1 - regPatBase)));
        double reg = 1 + (regMax - 1) * Math.Max(cvNorm, patNorm);
        return new IttnRegularityResult(reg, cv, patRatio);
    }

    /// <summary>
    /// ■finalizeRadarValue: 生スコア→レーダー値(analyze.js同名関数の移植)。
    /// ■計算内容: 100点まで線形、100〜200も線形(傾き別)、200超は段階減衰。
    /// ■設計意図: 体感難易度の飽和特性に合わせ、外れ値がチャートを破壊しないようにする。
    /// </summary>
    public static double FinalizeRadarValue(double rawScore, double base100, double base200)
    {
        if (rawScore <= 0 || base100 <= 0 || base200 <= base100) return 0;
        double v = rawScore <= base100
            ? (100 / base100) * rawScore
            : 100 + (100 / (base200 - base100)) * (rawScore - base100);

        (double Threshold, double Divisor)[] brakeRules =
        [
            (400, 10),
            (300, 4),
            (200, 2),
            (0, 1),
        ];

        double finalVal = 0, remaining = v;
        foreach (var (threshold, divisor) in brakeRules)
        {
            if (remaining > threshold)
            {
                finalVal += (remaining - threshold) / divisor;
                remaining = threshold;
            }
        }
        return (double.IsNaN(finalVal) || double.IsInfinity(finalVal)) ? 0 : Math.Max(0, finalVal);
    }
}
