using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Tests.Settings;

/// <summary>
/// レーン入替マクロの永続化(仕様書11.1、2026-07-30)のテスト。settings.json(AppSettings)とは
/// 独立したswap_macro.jsonファイルとして管理する(LaneSwapMacroFile.Load/Save)。
/// </summary>
public class LaneSwapMacroSettingsTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsMacrosAndLaneMapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swap_macro_test_{Guid.NewGuid():N}.json");
        try
        {
            var macros = new List<LaneSwapMacro>
            {
                new() { MacroName = "左右ミラー", TargetKeyTypeId = "5", LaneMapping = [3, 1, 2, 0, 4] },
            };

            LaneSwapMacroFile.Save(path, macros);
            var loaded = LaneSwapMacroFile.Load(path);

            Assert.Single(loaded);
            Assert.Equal("左右ミラー", loaded[0].MacroName);
            Assert.Equal("5", loaded[0].TargetKeyTypeId);
            Assert.Equal([3, 1, 2, 0, 4], loaded[0].LaneMapping);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swap_macro_missing_{Guid.NewGuid():N}.json");
        Assert.Empty(LaneSwapMacroFile.Load(path));
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swap_macro_corrupt_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json ]");
            Assert.Empty(LaneSwapMacroFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
