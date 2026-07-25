namespace DanoniEditor.Core.Analysis;

/// <summary>
/// 「おにスター」推定機能(2026-07-26、docs/progress_and_tbd_2026-07-25.md §2-1 TBD対応)。
/// 難易度表(dodl4、☆/★スケール)と多鍵データベース(ta、Lv1〜10スケール)を、統一スケール(score)へ
/// 変換したうえでプールし、<see cref="IttnAnalyzer"/>が返すTotalRatingから統一スケール値を
/// 逆算する単純線形回帰(「全キー種を統合した単一の回帰」方針、docs/progress_and_tbd_2026-07-24_add.md
/// §3-2で「具体形は再フィット時に決定する」とされていたもの)。
///
/// ■統一スケール変換(docs/ittn_analyzer_integration_handoff.md §1・2026-07-26確認済みブリッジ式):
///   score = tableLevel&lt;0 ? |tableLevel| : 12+tableLevel  (dodl4側、負=☆・正=★)
///   score ≈ 2.704×Lv − 1.737                              (ta側、Lv1〜10からの橋渡し式)
///
/// ■回帰係数の算出根拠(2026-07-26、このセッションで実データを使って算出):
/// `danoni_chart_collector`が収集した実測ログ(`collector-app/publish/out/analysis_log.csv`
/// dodl4由来n=1696、同`analysis_ta_log.csv` ta由来n=1093)を、tableLevel=0(未評価)・Lv=99
/// (プレースホルダ)・totalRating欠損/異常値を除外したうえで上記の統一スケールへ変換・プールし
/// (有効n=2752)、score = a×TotalRating + b の単回帰(最小二乗法)をPythonで実施して得た値。
/// a=0.107885, b=2.899794, RMSE=3.1539(score単位), R²=0.7771。
/// 60%信頼区間の半幅は残差が正規分布に従うと仮定した近似(z=0.8416≒norm.ppf(0.8))でRMSEから算出。
///
/// ※本推定はあくまで実測データに基づく目安であり、star値は最終的に人間の投票で決まるものである
/// (docs/ittn_analyzer_integration_handoff.md §7、ユーザー所見)。R²=0.78は全キー種混在のため、
/// 個別キー種ごとの回帰(参考: 同docs §6の旧表)より当てはまりは粗くなる点に留意
/// (統合回帰を採用したことによる既知のトレードオフ)。
///
/// 2026-07-26: ☆/★表記への変換はここでは行わない(ユーザー指示: 最終的な表記は「おにスター」表記側で
/// 行う想定で、この統一スケール値はあくまで算出途中の値であるため)。統一スケール(score)の数値を
/// そのまま返す。
/// </summary>
public static class OniStarEstimator
{
    private const double SlopeA = 0.107885;
    private const double InterceptB = 2.899794;
    private const double Rmse = 3.1539;

    /// <summary>正規分布の両側60%区間に対応するz値(≒norm.ppf(0.8))。</summary>
    private const double Z60 = 0.8416;

    /// <summary>推定結果(統一スケールの点推定・60%信頼区間、いずれも数値のまま)。</summary>
    public readonly record struct OniStarEstimate(double Score, double ScoreLow, double ScoreHigh);

    /// <summary>TotalRating(IttnAnalysisResult.TotalRating)から統一スケール(score)の
    /// 点推定・60%信頼区間を算出する。</summary>
    public static OniStarEstimate Estimate(double totalRating)
    {
        double score = SlopeA * totalRating + InterceptB;
        double low = Math.Max(0, score - Z60 * Rmse);
        double high = score + Z60 * Rmse;
        return new OniStarEstimate(score, low, high);
    }
}
