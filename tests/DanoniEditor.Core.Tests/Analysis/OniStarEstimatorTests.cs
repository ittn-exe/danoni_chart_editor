using DanoniEditor.Core.Analysis;

namespace DanoniEditor.Core.Tests.Analysis;

/// <summary>
/// おにスター推定(2026-08-05、OniStarEstimator)の検証。回帰係数自体は実データ(danoni_chart_collector
/// 収集分)から算出した固定値のため、ここでは「係数を適用した計算が正しく行われているか」
/// (点推定・信頼区間の算出式)を検証する。2026-08-05: ☆/★表記への変換は行わない方針になったため、
/// 数値(統一スケールscore)のみを検証する。
/// </summary>
public class OniStarEstimatorTests
{
    // 回帰式: score = 0.107885*TotalRating + 2.899794 (OniStarEstimator実装値と同じ定数を使用)
    private const double SlopeA = 0.107885;
    private const double InterceptB = 2.899794;
    private const double Rmse = 3.1539;
    private const double Z60 = 0.8416;

    [Fact]
    public void Estimate_ComputesScoreFromLinearRegression()
    {
        double totalRating = 100;
        var est = OniStarEstimator.Estimate(totalRating);
        double expectedScore = SlopeA * totalRating + InterceptB;
        Assert.Equal(expectedScore, est.Score, precision: 4);
    }

    [Fact]
    public void Estimate_ConfidenceIntervalWidth_MatchesZ60TimesRmse()
    {
        var est = OniStarEstimator.Estimate(100);
        double expectedHalfWidth = Z60 * Rmse;
        Assert.Equal(est.Score + expectedHalfWidth, est.ScoreHigh, precision: 4);
        Assert.Equal(est.Score - expectedHalfWidth, est.ScoreLow, precision: 4);
    }

    [Fact]
    public void Estimate_ScoreLow_NeverGoesBelowZero()
    {
        // TotalRatingが極端に小さい(負の)場合でもScoreLowが0未満にならないことを確認
        var est = OniStarEstimator.Estimate(-1000);
        Assert.True(est.ScoreLow >= 0);
    }
}
