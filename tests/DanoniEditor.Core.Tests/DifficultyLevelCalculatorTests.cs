using DanoniEditor.Core.Analysis;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// danoniplus本体の「レベル計算ツール++」(calcLevel)移植の検証(2026-08-01)。
/// 期待値は本体アルゴリズムの計算式を手計算で追って求めたもの(実機での実測値との突き合わせではない)。
/// </summary>
public class DifficultyLevelCalculatorTests
{
    [Fact]
    public void SingleArrow_ReturnsFixedPointZeroOne()
    {
        // 実ノート1個のみ(allScorebook = [note-100, note, note+100] → allCnt=3、本体の特例分岐)
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[1000]],
            freezeFramesPerLane: [[]]);

        Assert.Equal("0.01", result.Tool);
    }

    [Fact]
    public void SingleFreezeOnly_UsesFreezeStartAsArrow_ReturnsFixedPointZeroOne()
    {
        // フリーズの始点のみが矢印データに組み込まれるため、単発ノート1個と同じ扱いになる
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[]],
            freezeFramesPerLane: [[(1000L, 1300L)]]);

        Assert.Equal("0.01", result.Tool);
    }

    [Fact]
    public void TwoIsolatedSingleNotes_FarApart_ReturnsZeroZero()
    {
        // allScorebook = [900,1000,2000,2100] → allCnt=4, calcArrowCnt=1
        // levelcount = 2/2000 = 0.001 → baseDifLevel = round2(0.001*4) = 0.00
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[1000, 2000]],
            freezeFramesPerLane: [[]]);

        Assert.Equal("0.00", result.Tool);
        Assert.Equal(0, result.Push3Cnt);
    }

    [Fact]
    public void TwoSimultaneousNotes_IsolatedInTime_ComputesTwoPushCorrection()
    {
        // 2レーン、同一フレームに同時押し。allScorebook = [900,1000,1000,1100] → allCnt=4
        // twoPushCount = 40/((1100-1000)*(1000-900)) = 40/10000 = 0.004
        // baseDifLevel = round2(0.004*4) = 0.02, difLevel = round2(0.02*1/1) = 0.02
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[1000], [1000]],
            freezeFramesPerLane: [[], []]);

        Assert.Equal("0.02", result.Tool);
    }

    [Fact]
    public void ThreePushAtSameFrame_MarksToolValueWithAsterisk()
    {
        // 3レーン同時押し(3つ押し)の場合、該当フレームはpush3Listに入り、tool値に"*"が付与される
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[1000], [1000], [1000]],
            freezeFramesPerLane: [[], [], []]);

        Assert.EndsWith("*", result.Tool);
        Assert.True(result.Push3Cnt > 0);
    }

    [Fact]
    public void NoArrowsAtAll_DoesNotThrow_ReturnsGuardValue()
    {
        // 本体側は未定義入力(NaN)になるケースなので、当エディタ独自の防御的ガード値を返す
        var result = DifficultyLevelCalculator.Calculate(
            arrowFramesPerLane: [[], []],
            freezeFramesPerLane: [[], []]);

        Assert.Equal("0.00", result.Tool);
    }
}
