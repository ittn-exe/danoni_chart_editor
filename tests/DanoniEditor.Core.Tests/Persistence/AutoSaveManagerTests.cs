using DanoniEditor.Core.Persistence;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>
/// AutoSaveManager(2026-07-25、TBD「自動保存・クラッシュ復旧」)のテスト。
/// 実ファイルI/Oを行うため、テストごとにOSの一時フォルダ配下へ使い捨てディレクトリを作って検証する。
/// </summary>
public class AutoSaveManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "danoni_autosave_test_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CrashFlag_SetCheckClear_RoundTrips()
    {
        Assert.False(AutoSaveManager.IsCrashFlagSet(_dir));

        AutoSaveManager.SetCrashFlag(_dir);
        Assert.True(AutoSaveManager.IsCrashFlagSet(_dir));

        AutoSaveManager.ClearCrashFlag(_dir);
        Assert.False(AutoSaveManager.IsCrashFlagSet(_dir));
    }

    [Fact]
    public void ClearCrashFlag_WhenNotSet_DoesNotThrow()
    {
        AutoSaveManager.ClearCrashFlag(_dir); // フォルダ自体が無い状態でも例外にならないこと
        Assert.False(AutoSaveManager.IsCrashFlagSet(_dir));
    }

    [Fact]
    public void WriteSlot_CreatesFileAndManifestEntry()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", @"C:\proj\a.json", "曲A", "{\"dummy\":1}");

        Assert.Equal("{\"dummy\":1}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));

        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("slot-a", entry.SlotId);
        Assert.Equal(@"C:\proj\a.json", entry.LastKnownPath);
        Assert.Equal("曲A", entry.ProjectName);
    }

    [Fact]
    public void WriteSlot_CalledAgainForSameSlotId_ReplacesNotDuplicates()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", null, "曲A", "{\"v\":1}");
        AutoSaveManager.WriteSlot(_dir, "slot-a", null, "曲A(改題)", "{\"v\":2}");

        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("曲A(改題)", entry.ProjectName);
        Assert.Equal("{\"v\":2}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));
    }

    [Fact]
    public void MultipleSlots_AreIndependent()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", null, "曲A", "{\"v\":\"a\"}");
        AutoSaveManager.WriteSlot(_dir, "slot-b", null, "曲B", "{\"v\":\"b\"}");

        var manifest = AutoSaveManager.LoadManifest(_dir);
        Assert.Equal(2, manifest.Count);
        Assert.Equal("{\"v\":\"a\"}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));
        Assert.Equal("{\"v\":\"b\"}", AutoSaveManager.ReadSlotContent(_dir, "slot-b"));
    }

    [Fact]
    public void ClearSlot_RemovesFileAndManifestEntry_LeavesOthersIntact()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", null, "曲A", "{}");
        AutoSaveManager.WriteSlot(_dir, "slot-b", null, "曲B", "{}");

        AutoSaveManager.ClearSlot(_dir, "slot-a");

        Assert.False(File.Exists(AutoSaveManager.GetSlotFilePath(_dir, "slot-a")));
        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("slot-b", entry.SlotId);
    }

    [Fact]
    public void ClearSlot_WhenSlotDoesNotExist_DoesNotThrow()
    {
        AutoSaveManager.ClearSlot(_dir, "nonexistent"); // 例外にならないこと
        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }

    [Fact]
    public void LoadManifest_WhenFileMissing_ReturnsEmptyList()
    {
        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }

    [Fact]
    public void LoadManifest_WhenFileCorrupted_ReturnsEmptyListInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AutoSaveManager.GetManifestPath(_dir), "{ this is not valid json ");

        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }
}
