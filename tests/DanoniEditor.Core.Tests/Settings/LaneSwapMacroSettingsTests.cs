using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Tests.Settings;

/// <summary>
/// レーン入替マクロの永続化(仕様書11.1、2026-07-30)のテスト。settings.json(AppSettings)とは
/// 独立したファイルで管理する(LaneSwapMacroFile.Load/Save = 単一ファイル版、
/// LoadAll/SaveAll = キー種ごとの"s-macro_キー種.json"分割版、2026-07-26g)。
/// </summary>
public class LaneSwapMacroSettingsTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"swap_macro_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    [Fact]
    public void SaveAll_SplitsByKeyType_IntoSeparateFiles()
    {
        var dir = NewTempDir();
        try
        {
            var macros = new List<LaneSwapMacro>
            {
                new() { MacroName = "左右ミラー", TargetKeyTypeId = "5", LaneMapping = [3, 1, 2, 0, 4] },
                new() { MacroName = "シャッフルA", TargetKeyTypeId = "5", LaneMapping = [1, 0, 3, 2, 4] },
                new() { MacroName = "9Aミラー", TargetKeyTypeId = "9A", LaneMapping = [8, 7, 6, 5, 4, 3, 2, 1, 0] },
            };

            LaneSwapMacroFile.SaveAll(dir, macros);

            Assert.True(File.Exists(Path.Combine(dir, "s-macro_5.json")));
            Assert.True(File.Exists(Path.Combine(dir, "s-macro_9A.json")));

            var lane5 = LaneSwapMacroFile.Load(Path.Combine(dir, "s-macro_5.json"));
            Assert.Equal(2, lane5.Count);
            var lane9A = LaneSwapMacroFile.Load(Path.Combine(dir, "s-macro_9A.json"));
            Assert.Single(lane9A);

            var all = LaneSwapMacroFile.LoadAll(dir);
            Assert.Equal(3, all.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveAll_RemovesFileForKeyTypeWithNoMacrosLeft()
    {
        var dir = NewTempDir();
        try
        {
            LaneSwapMacroFile.SaveAll(dir, new List<LaneSwapMacro>
            {
                new() { MacroName = "旧5keyマクロ", TargetKeyTypeId = "5", LaneMapping = [1, 0, 2, 3, 4] },
            });
            Assert.True(File.Exists(Path.Combine(dir, "s-macro_5.json")));

            // 5keyのマクロを全て削除した状態で再保存
            LaneSwapMacroFile.SaveAll(dir, new List<LaneSwapMacro>
            {
                new() { MacroName = "7keyマクロ", TargetKeyTypeId = "7", LaneMapping = [1, 0, 2, 3, 4, 5, 6] },
            });

            Assert.False(File.Exists(Path.Combine(dir, "s-macro_5.json")));
            Assert.True(File.Exists(Path.Combine(dir, "s-macro_7.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_MigratesLegacyCombinedFile_SplitsByKeyTypeAndRenamesOld()
    {
        var dir = NewTempDir();
        try
        {
            var legacyPath = Path.Combine(dir, "swap_macro.json");
            var legacyMacros = new List<LaneSwapMacro>
            {
                new() { MacroName = "左右ミラー", TargetKeyTypeId = "5", LaneMapping = [3, 1, 2, 0, 4] },
                new() { MacroName = "9Aミラー", TargetKeyTypeId = "9A", LaneMapping = [8, 7, 6, 5, 4, 3, 2, 1, 0] },
            };
            LaneSwapMacroFile.Save(legacyPath, legacyMacros);

            var loaded = LaneSwapMacroFile.LoadAll(dir);

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, m => m.TargetKeyTypeId == "5" && m.MacroName == "左右ミラー");
            Assert.Contains(loaded, m => m.TargetKeyTypeId == "9A" && m.MacroName == "9Aミラー");

            Assert.True(File.Exists(Path.Combine(dir, "s-macro_5.json")));
            Assert.True(File.Exists(Path.Combine(dir, "s-macro_9A.json")));
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(legacyPath + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_NoLegacyFile_JustReadsSplitFiles()
    {
        var dir = NewTempDir();
        try
        {
            LaneSwapMacroFile.SaveAll(dir, new List<LaneSwapMacro>
            {
                new() { MacroName = "左右ミラー", TargetKeyTypeId = "5", LaneMapping = [3, 1, 2, 0, 4] },
            });

            var loaded = LaneSwapMacroFile.LoadAll(dir);
            Assert.Single(loaded);
            Assert.Equal("左右ミラー", loaded[0].MacroName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
