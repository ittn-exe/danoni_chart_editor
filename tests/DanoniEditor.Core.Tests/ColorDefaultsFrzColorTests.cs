using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// ColorDefaults.ResolveFrzColorsHex/ResolveFrzHitColorsHexのテスト(2026-07-27修正)。
/// frzColorはdanoniplus本体の仕様上、setColorの色グループ数に関わらず常に4スロット
/// ([0]通常端点 [1]通常帯 [2]判定中端点 [3]判定中帯)の1セットのみ持つ。従来の実装は
/// 誤って「色グループごとに4スロット」(colorGroup*4オフセット)を使っていたため、
/// 色グループ1以降のフリーズが常にarrowDefaultへフォールバックしてしまうバグがあった。
/// </summary>
public class ColorDefaultsFrzColorTests
{
    private static ChartProject NewProject() => new();

    private static DifficultyTab NewTab(List<string>? frzColorOverride)
    {
        var tab = new DifficultyTab { DifficultyName = "Normal" };
        tab.FrzColorOverride = frzColorOverride;
        return tab;
    }

    [Fact]
    public void ResolveFrzColorsHex_UsesSameFourSlots_RegardlessOfColorGroup()
    {
        var project = NewProject();
        var tab = NewTab(["#111111", "#222222", "#333333", "#444444"]);
        project.Tabs.Add(tab);

        // colorGroupという概念自体が引数から消えている(常に同じ4スロットを見る)ことを確認
        var (normal0, bar0) = ColorDefaults.ResolveFrzColorsHex(tab, project, "#000000");
        Assert.Equal("#111111", normal0);
        Assert.Equal("#222222", bar0);
    }

    [Fact]
    public void ResolveFrzHitColorsHex_ReadsSlots2And3()
    {
        var project = NewProject();
        var tab = NewTab(["#111111", "#222222", "#333333", "#444444"]);
        project.Tabs.Add(tab);

        var (hit, hitBar) = ColorDefaults.ResolveFrzHitColorsHex(tab, project, "#normalFallback", "#barFallback");
        Assert.Equal("#333333", hit);
        Assert.Equal("#444444", hitBar);
    }

    [Fact]
    public void ResolveFrzColorsHex_EmptySlot_FallsBackToArrowDefault()
    {
        var project = NewProject();
        var tab = NewTab(["", "#222222"]); // 端点(0番)が空欄
        project.Tabs.Add(tab);

        var (normal, bar) = ColorDefaults.ResolveFrzColorsHex(tab, project, "#ARROWDEF");
        Assert.Equal("#ARROWDEF", normal);
        Assert.Equal("#222222", bar);
    }

    [Fact]
    public void ResolveFrzColorsHex_NoOverride_FallsBackToArrowDefault()
    {
        var project = NewProject();
        var tab = NewTab(null);
        project.Tabs.Add(tab);

        var (normal, bar) = ColorDefaults.ResolveFrzColorsHex(tab, project, "#ARROWDEF");
        Assert.Equal("#ARROWDEF", normal);
        Assert.Equal("#ARROWDEF", bar);
    }

    [Fact]
    public void ResolveFrzColorsHex_DefaultFrzColorUse_AlwaysUsesArrowDefault()
    {
        var project = NewProject();
        project.ExtraHeaders["defaultFrzColorUse"] = "true";
        var tab = NewTab(["#111111", "#222222", "#333333", "#444444"]);
        project.Tabs.Add(tab);

        var (normal, bar) = ColorDefaults.ResolveFrzColorsHex(tab, project, "#ARROWDEF");
        Assert.Equal("#ARROWDEF", normal);
        Assert.Equal("#ARROWDEF", bar);
    }
}
