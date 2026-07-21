using System.Text.Json;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Models;

/// <summary>
/// DifficultyTab.DisplayLabel(2026-07-21要望「難易度名タブにキー種を表示」)のテスト。
/// 「キー種k - 難易度名」形式、難易度名未設定時は"(newdiff)"、キー種は"key"でなく"k"接尾辞。
/// </summary>
public class DifficultyTabDisplayLabelTests
{
    [Theory]
    [InlineData("5", "Normal", "5k - Normal")]
    [InlineData("11L", "Hard", "11Lk - Hard")]
    [InlineData("12i", "Another", "12ik - Another")]
    public void DisplayLabel_FormatsKeyTypeAndName(string keyTypeId, string difficultyName, string expected)
    {
        var tab = new DifficultyTab { KeyTypeId = keyTypeId, DifficultyName = difficultyName };
        Assert.Equal(expected, tab.DisplayLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayLabel_EmptyOrWhitespaceName_ShowsNewDiffPlaceholder(string name)
    {
        var tab = new DifficultyTab { KeyTypeId = "5", DifficultyName = name };
        Assert.Equal("5k - (newdiff)", tab.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_IsExcludedFromJsonSerialization()
    {
        var tab = new DifficultyTab { KeyTypeId = "5", DifficultyName = "Normal" };
        var json = JsonSerializer.Serialize(tab);
        Assert.DoesNotContain("displayLabel", json, StringComparison.OrdinalIgnoreCase);
    }
}
