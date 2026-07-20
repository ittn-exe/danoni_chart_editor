using System.Text.RegularExpressions;
using DanoniEditor.Core.Export;

namespace DanoniEditor.Core.Tests;

/// <summary>CssColorNames(色編集モードの色名+透明度指定用、2026-07-24)の健全性テスト。</summary>
public partial class CssColorNamesTests
{
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexRegex();

    [Fact]
    public void All_HasExpectedCount()
    {
        // CSS拡張色名キーワードは一般に「147色」と呼ばれることが多いが、rebeccapurple(CSS Color 4で追加)を
        // 含めると148色になる(本リストはrebeccapurpleを含むため148)。
        Assert.Equal(148, CssColorNames.All.Count);
    }

    [Fact]
    public void All_NamesAreUniqueAndLowercase()
    {
        var names = CssColorNames.All.Select(c => c.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.All(names, n => Assert.Equal(n, n.ToLowerInvariant()));
    }

    [Fact]
    public void All_HexValuesAreValidSixDigitFormat()
    {
        Assert.All(CssColorNames.All, c => Assert.Matches(HexRegex(), c.Hex));
    }
}
