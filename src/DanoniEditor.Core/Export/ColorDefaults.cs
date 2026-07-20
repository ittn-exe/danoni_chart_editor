using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Export;

/// <summary>
/// レーンの既定色(ncolor_dataが無い場合に本家が使う色)の解決ロジック(2026-07-24)。
/// ChartCanvas(App層、譜面ビュー表示)とDosExporter/DosImporter(Core層、書き出し/読み込み)の
/// 両方が「このレーンは今どの色がデフォルトか」を同一の規則で判定する必要があるため、
/// WPF非依存のCoreにhex文字列ベースで実装し、双方から共有する(2026-07-24 ncolor_data永続化対応の一環)。
/// </summary>
public static class ColorDefaults
{
    /// <summary>本体側のデフォルト配色(仕様書6.4.2、setColor未指定時のエディタ側既定値)</summary>
    public static readonly string[] DefaultLaneColors =
        ["#99ffff", "#ffff33", "#ffffff", "#ff0066", "#ff9966", "#99ff99"];

    /// <summary>
    /// レーンの矢印本体の既定色(hex)を解決する。tab自身のSetColorOverrideが無ければ
    /// 1タブ目(=共通値の実体、仕様書6.4.2)へ、それも無ければDefaultLaneColorsへフォールバックする。
    /// </summary>
    public static string ResolveSetColorHex(DifficultyTab tab, ChartProject project, int colorGroup)
    {
        var overrides = tab.SetColorOverride ?? (project.Tabs.Count > 0 ? project.Tabs[0].SetColorOverride : null);
        if (overrides is not null && colorGroup >= 0 && colorGroup < overrides.Count)
            return overrides[colorGroup];
        return DefaultLaneColors[colorGroup % DefaultLaneColors.Length];
    }

    /// <summary>
    /// フリーズアローの既定色(端点/帯、hex)を解決する(仕様書6.4.2 frzColor)。
    /// defaultFrzColorUse=trueの間は本体側の既定フリーズアロー色セットが使われ、frzColorの値自体が
    /// 無視される仕様だが、本エディタは本体既定色セットまでは実装していないためsetColor由来の色へ
    /// フォールバックする(ChartCanvas.FrzColorsと同じ、ユーザー向けに開示済みの簡略化)。
    /// </summary>
    public static (string NormalHex, string BarHex) ResolveFrzColorsHex(
        DifficultyTab tab, ChartProject project, int colorGroup, string arrowDefaultHex)
    {
        bool defaultFrzColorUse = project.ExtraHeaders.TryGetValue("defaultFrzColorUse", out var dfu) && dfu == "true";
        if (defaultFrzColorUse) return (arrowDefaultHex, arrowDefaultHex);

        var frz = tab.FrzColorOverride ?? (project.Tabs.Count > 0 ? project.Tabs[0].FrzColorOverride : null);
        if (frz is null) return (arrowDefaultHex, arrowDefaultHex);

        int baseIdx = colorGroup * 4;
        string? noteHex = baseIdx < frz.Count ? frz[baseIdx] : null;
        string? bandHex = baseIdx + 1 < frz.Count ? frz[baseIdx + 1] : null;

        return (
            string.IsNullOrWhiteSpace(noteHex) ? arrowDefaultHex : noteHex!,
            string.IsNullOrWhiteSpace(bandHex) ? arrowDefaultHex : bandHex!
        );
    }

    /// <summary>
    /// フリーズアローのヒット時(判定中)の既定色(端点/帯、hex)を解決する(2026-07-24、frzHitColor編集
    /// モード用)。FrzColorOverrideは1グループにつき4スロット([0]通常端点 [1]通常帯 [2]ヒット時端点
    /// [3]ヒット時帯)を持つ既存仕様(仕様書6.4.2)だが、[2][3]はこれまでプレイ判定を行わないエディタ
    /// では未使用だった。値が無いスロットは通常時(Normal/NormalBar)の解決値へフォールバックする。
    /// </summary>
    public static (string HitHex, string HitBarHex) ResolveFrzHitColorsHex(
        DifficultyTab tab, ChartProject project, int colorGroup, string normalHex, string normalBarHex)
    {
        var frz = tab.FrzColorOverride ?? (project.Tabs.Count > 0 ? project.Tabs[0].FrzColorOverride : null);
        if (frz is null) return (normalHex, normalBarHex);

        int baseIdx = colorGroup * 4;
        string? hitHex = baseIdx + 2 < frz.Count ? frz[baseIdx + 2] : null;
        string? hitBarHex = baseIdx + 3 < frz.Count ? frz[baseIdx + 3] : null;

        return (
            string.IsNullOrWhiteSpace(hitHex) ? normalHex : hitHex!,
            string.IsNullOrWhiteSpace(hitBarHex) ? normalBarHex : hitBarHex!
        );
    }

    /// <summary>
    /// 矢印/フリーズアローの塗りつぶし色(ArrowShadow/NormalShadow、2026-07-24 ShadowColor編集モード用)の
    /// 既定色(hex)を解決する。setShadowColor/frzShadowColorは本エディタでは②色設定タブのような
    /// 専用UI・per-ColorGroupのList管理を持たず、「その他ヘッダー」(ChartProject.ExtraHeaders)に
    /// 生文字列のまま格納される(仕様書6.4.4)。setColor/frzColorと同様のカンマ区切りper-ColorGroup
    /// 値として書かれている前提でパースし、無ければExtraHeaderDefsの既定値(#000000)へフォールバックする。
    /// </summary>
    public static string ResolveShadowHex(ChartProject project, int colorGroup, string headerKey)
    {
        const string fallback = "#000000";
        if (!project.ExtraHeaders.TryGetValue(headerKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (colorGroup < 0 || colorGroup >= parts.Length || string.IsNullOrWhiteSpace(parts[colorGroup]))
            return fallback;
        return parts[colorGroup];
    }
}
