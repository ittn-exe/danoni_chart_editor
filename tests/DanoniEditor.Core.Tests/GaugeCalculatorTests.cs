using DanoniEditor.Core.Analysis;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// ゲージ計算機(2026-08-01)のテスト。本体ソース(danoni_main.js)のgetAccuracy/calcLifeValと
/// 突き合わせて検証した式をそのまま移植しているため、期待値は手計算で追ったもの。
/// </summary>
public class GaugeCalculatorTests
{
    [Fact]
    public void GetAllCount_FreezeStartJudgeOn_CountsFreezeTwice()
    {
        Assert.Equal(1100, GaugeCalculator.GetAllCount(1000, 50, includeFreezeStartJudge: true));
        Assert.Equal(1050, GaugeCalculator.GetAllCount(1000, 50, includeFreezeStartJudge: false));
    }

    [Fact]
    public void ToRealValue_Fix_ReturnsRawValueUnchanged()
    {
        // 本体は可変フラグ=Fの場合、生値をそのまま実値として使う(×100等のスケーリングは行わない)
        double real = GaugeCalculator.ToRealValue(6, GaugeCalculator.CalcMode.Fix, maxLifeVal: 100000, allCnt: 1000);
        Assert.Equal(6, real);
    }

    [Fact]
    public void ToRealValue_Vary_MatchesCalcLifeValFormula()
    {
        // 本体: calcLifeVal(_val, _allArrows) = _val * maxLifeVal / _allArrows
        double real = GaugeCalculator.ToRealValue(2, GaugeCalculator.CalcMode.Vary, maxLifeVal: 100000, allCnt: 1000);
        Assert.Equal(200, real);
    }

    [Fact]
    public void ToRawValue_IsInverseOfToRealValue_ForVaryMode()
    {
        double real = GaugeCalculator.ToRealValue(2, GaugeCalculator.CalcMode.Vary, 100000, 1000);
        double raw = GaugeCalculator.ToRawValue(real, GaugeCalculator.CalcMode.Vary, 100000, 1000);
        Assert.Equal(2, raw, precision: 9);
    }

    [Fact]
    public void GetAccuracy_ComputesRateAndAllowableMiss()
    {
        // border=25%,init=50%(maxLifeVal=100000→25000/50000), rcv=200, dmg=500, allCnt=1000
        // justPoint = max(25000-50000+500*1000,0)/(200+500) = 475000/700 = 678.571...
        // minRecovery = ceil(678.571) = 679, rate = 67.9%, allowableMiss = 1000-679 = 321
        var result = GaugeCalculator.GetAccuracy(border: 25000, rcv: 200, dmg: 500, init: 50000, allCnt: 1000);

        Assert.True(result.IsValid);
        Assert.Equal(67.9, result.RatePercent, precision: 6);
        Assert.Equal(321, result.AllowableMiss);
    }

    [Theory]
    [InlineData(0, 0, 100)]   // rcv=dmg=0
    [InlineData(-1, 5, 100)]  // rcv<0
    [InlineData(5, -1, 100)]  // dmg<0
    public void GetAccuracy_InvalidInputs_ReturnsNotValid(double rcv, double dmg, int allCnt)
    {
        var result = GaugeCalculator.GetAccuracy(border: 100, rcv: rcv, dmg: dmg, init: 50, allCnt: allCnt);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void GetAccuracy_ZeroAllCount_ReturnsNotValid()
    {
        var result = GaugeCalculator.GetAccuracy(border: 100, rcv: 5, dmg: 5, init: 50, allCnt: 0);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void SolveForTarget_FixMode_DamageFixed_SolvesRecovery()
    {
        // border=100,init=50(maxLifeVal=100と同スケール), allCnt=100, target rate=90%(targetMiss=10, successCnt=90)
        // dmg固定=1(Fix) → sugRcv = ((100-50)+1*10)/90 = 60/90 = 0.6667
        var result = GaugeCalculator.SolveForTarget(
            GaugeCalculator.DesignTarget.Rate, targetValue: 90,
            GaugeCalculator.FixParam.Damage, fixedRawValue: 1,
            GaugeCalculator.CalcMode.Fix, maxLifeVal: 100,
            realBorder: 100, realInit: 50, allCnt: 100);

        Assert.True(result.IsSolvable);
        Assert.False(result.IsInfinite);
        Assert.Equal(60.0 / 90.0, result.RawValue, precision: 6);
    }

    [Fact]
    public void SolveForTarget_VaryMode_ScalesConsistentlyWithFixMode()
    {
        // maxLifeValを100→10000(100倍)にスケールし、border/init/dmgも同じ比率にすると、
        // Varyモードで逆算したrawValueはFixモードの結果と一致するはず(次元の整合性チェック)
        var result = GaugeCalculator.SolveForTarget(
            GaugeCalculator.DesignTarget.Rate, targetValue: 90,
            GaugeCalculator.FixParam.Damage, fixedRawValue: 1,
            GaugeCalculator.CalcMode.Vary, maxLifeVal: 10000,
            realBorder: 10000, realInit: 5000, allCnt: 100);

        Assert.True(result.IsSolvable);
        Assert.Equal(60.0 / 90.0, result.RawValue, precision: 6);
    }

    [Fact]
    public void SolveForTarget_MissCountZero_DamageUnknown_IsInfinite()
    {
        // ミス0回(targetMiss=0)を前提にダメージを逆算しようとすると理論上無限大になる
        var result = GaugeCalculator.SolveForTarget(
            GaugeCalculator.DesignTarget.MissCount, targetValue: 0,
            GaugeCalculator.FixParam.Recovery, fixedRawValue: 5,
            GaugeCalculator.CalcMode.Fix, maxLifeVal: 100,
            realBorder: 100, realInit: 50, allCnt: 100);

        Assert.True(result.IsSolvable);
        Assert.True(result.IsInfinite);
    }

    [Fact]
    public void SolveForTarget_SuccessCountNotPositive_IsNotSolvable()
    {
        // 目標ミス数が総ノート数以上(成功数0以下)の場合は解無し
        var result = GaugeCalculator.SolveForTarget(
            GaugeCalculator.DesignTarget.MissCount, targetValue: 100,
            GaugeCalculator.FixParam.Damage, fixedRawValue: 1,
            GaugeCalculator.CalcMode.Fix, maxLifeVal: 100,
            realBorder: 100, realInit: 50, allCnt: 100);

        Assert.False(result.IsSolvable);
    }
}
