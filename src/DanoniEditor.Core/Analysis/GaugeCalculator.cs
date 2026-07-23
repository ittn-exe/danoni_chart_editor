namespace DanoniEditor.Core.Analysis;

/// <summary>
/// gauge{ゲージ名}(仕様dos-h0022)の計算機能(2026-08-01、ユーザー要望「ゲージ計算機」)。
/// danoniplus本体(`danoni_main.js`)の`getAccuracy`/`calcLifeVal`/`gaugeFormat`をナレッジの本体ソースで
/// 検証したうえで移植したもの。ユーザー提供の参考ツール(calc.html、Geminiで作成)には
/// Fixモード(可変フラグ=F)の換算に本体側と食い違う「×100」の処理があったため、これは含めていない
/// (本体はFixed値をそのまま実値として使うだけで、maxLifeValやノート数によるスケーリングを一切行わない)。
/// </summary>
public static class GaugeCalculator
{
    public enum CalcMode { Fix, Vary }

    /// <summary>総ノーツ数(仕様: 通常矢印+フリーズ数。frzStartjdgUse時はフリーズを2重に数える)。
    /// gaugeFormat内の allCnt = sumData(arrowCnt) + (frzStartjdgUse?2:1)*sumData(frzCnt) に対応。</summary>
    public static int GetAllCount(int normalArrowCount, int freezeArrowCount, bool includeFreezeStartJudge)
        => normalArrowCount + freezeArrowCount * (includeFreezeStartJudge ? 2 : 1);

    /// <summary>Fix/Varyの生値(dos.txtのgaugeXXXにそのまま書く値)から実際のライフ増減量へ変換する
    /// (本体のcalcLifeVal相当。Fixはそのまま、Varyはノート数で按分)。</summary>
    public static double ToRealValue(double rawValue, CalcMode mode, double maxLifeVal, int allCnt)
        => mode == CalcMode.Vary
            ? (allCnt > 0 ? rawValue * maxLifeVal / allCnt : 0)
            : rawValue;

    /// <summary>実際のライフ増減量から生値(dos.txt書き込み用)へ逆変換する(ToRealValueの逆関数)。</summary>
    public static double ToRawValue(double realValue, CalcMode mode, double maxLifeVal, int allCnt)
        => mode == CalcMode.Vary
            ? (maxLifeVal > 0 ? realValue * allCnt / maxLifeVal : 0)
            : realValue;

    /// <summary>ノルマ/初期ライフの%指定を実際のライフ値へ変換する(maxLifeVal * percent / 100)。</summary>
    public static double PercentToReal(double percent, double maxLifeVal) => maxLifeVal * percent / 100;

    /// <summary>達成率・許容ミス数の計算結果(本体のgetAccuracyに対応)。</summary>
    /// <param name="IsValid">false の場合は計算不能(ノート数0、または回復・ダメージが共に0/負数)。
    /// UI側は「----」等のプレースホルダ表示にする。</param>
    /// <param name="RatePercent">必要達成率(%)。100を超える場合は「クリア不可能な設定」を意味する。</param>
    /// <param name="AllowableMiss">許容ミス数。0以上なら「あとX回ミスしてもクリア可能」、
    /// 負数なら「ノーツを1つも落とせない設定でも足りない(理論上クリア不可)」を意味する。</param>
    public readonly record struct AccuracyResult(bool IsValid, double RatePercent, int AllowableMiss);

    /// <summary>本体のgetAccuracy(_border, _rcv, _dmg, _init, _allCnt)を移植したもの。
    /// 引数は全て実値(border/initは%変換済み、rcv/dmgはToRealValue変換済み)であること。</summary>
    public static AccuracyResult GetAccuracy(double border, double rcv, double dmg, double init, int allCnt)
    {
        if (allCnt <= 0 || (rcv == 0 && dmg == 0) || rcv < 0 || dmg < 0)
            return new AccuracyResult(false, 0, 0);

        double justPoint = rcv + dmg > 0 ? Math.Max(border - init + dmg * allCnt, 0) / (rcv + dmg) : 0;
        double minRecovery = border == 0 ? Math.Floor(justPoint + 1) : Math.Ceiling(justPoint);
        double rate = Math.Max(minRecovery / allCnt * 100, 0);
        int allowableCnts = (int)Math.Min(allCnt - minRecovery, allCnt);

        return new AccuracyResult(true, rate, allowableCnts);
    }

    public enum DesignTarget { Rate, MissCount }
    public enum FixParam { Recovery, Damage }

    /// <summary>Design Mode(達成率/ミス数からrcv・dmgの逆算)の結果。</summary>
    /// <param name="IsSolvable">successCnt(= allCnt - 目標ミス数)が正の場合のみ解が求まる。</param>
    /// <param name="RawValue">dos.txtのgaugeXXXへそのまま書き込める生値。目標ミス数が0で
    /// ダメージを逆算する場合は理論上「無限大(∞)」になるため、IsInfiniteがtrueになる。</param>
    public readonly record struct DesignResult(bool IsSolvable, bool IsInfinite, double RawValue);

    /// <summary>
    /// 達成率(%)またはミス数の目標値から、Recovery/Damageの一方を固定してもう一方を逆算する。
    /// </summary>
    /// <param name="fixedRawValue">固定する側(fixParamで指定した方)の生値(dos.txt書き込み値)。</param>
    public static DesignResult SolveForTarget(
        DesignTarget target, double targetValue, FixParam fixParam, double fixedRawValue,
        CalcMode mode, double maxLifeVal, double realBorder, double realInit, int allCnt)
    {
        if (allCnt <= 0) return new DesignResult(false, false, 0);

        double targetMiss = target == DesignTarget.Rate ? allCnt * (1 - targetValue / 100) : targetValue;
        double successCnt = allCnt - targetMiss;
        if (successCnt <= 0) return new DesignResult(false, false, 0);

        double fixedReal = ToRealValue(fixedRawValue, mode, maxLifeVal, allCnt);

        if (fixParam == FixParam.Damage)
        {
            // Dmg固定 → Rcvを逆算
            double sugRcv = ((realBorder - realInit) + fixedReal * targetMiss) / successCnt;
            double rawRcv = ToRawValue(Math.Max(sugRcv, 0), mode, maxLifeVal, allCnt);
            return new DesignResult(true, false, rawRcv);
        }
        else
        {
            // Rcv固定 → Dmgを逆算
            if (targetMiss <= 0)
                return new DesignResult(true, true, 0); // ミス0回が前提だとダメージは無限大(理論上何をミスしても即失敗しない)
            double sugDmg = (fixedReal * successCnt - (realBorder - realInit)) / targetMiss;
            double rawDmg = ToRawValue(Math.Max(sugDmg, 0), mode, maxLifeVal, allCnt);
            return new DesignResult(true, false, rawDmg);
        }
    }
}
