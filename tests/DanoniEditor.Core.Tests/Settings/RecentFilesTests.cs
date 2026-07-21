using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Tests.Settings;

/// <summary>
/// AppSettings.AddRecentFile/PruneMissingRecentFiles(2026-07-28、ファイル>最近開いたファイル)のテスト。
/// </summary>
public class RecentFilesTests
{
    [Fact]
    public void AddRecentFile_InsertsAtFront()
    {
        var settings = new AppSettings();
        settings.AddRecentFile("a.json");
        settings.AddRecentFile("b.json");

        Assert.Equal(2, settings.RecentFiles.Count);
        Assert.EndsWith("b.json", settings.RecentFiles[0]);
        Assert.EndsWith("a.json", settings.RecentFiles[1]);
    }

    [Fact]
    public void AddRecentFile_DuplicatePath_MovesToFrontInsteadOfDuplicating()
    {
        var settings = new AppSettings();
        settings.AddRecentFile("a.json");
        settings.AddRecentFile("b.json");
        settings.AddRecentFile("a.json"); // 再度開く

        Assert.Equal(2, settings.RecentFiles.Count);
        Assert.EndsWith("a.json", settings.RecentFiles[0]);
        Assert.EndsWith("b.json", settings.RecentFiles[1]);
    }

    [Fact]
    public void AddRecentFile_CaseInsensitivePathComparison_MovesToFrontInsteadOfDuplicating()
    {
        var settings = new AppSettings();
        settings.AddRecentFile("C:/charts/a.json");
        settings.AddRecentFile("c:/CHARTS/A.JSON");

        Assert.Single(settings.RecentFiles);
    }

    [Fact]
    public void AddRecentFile_TrimsToLimit()
    {
        var settings = new AppSettings { RecentFilesLimit = 3 };
        settings.AddRecentFile("1.json");
        settings.AddRecentFile("2.json");
        settings.AddRecentFile("3.json");
        settings.AddRecentFile("4.json");

        Assert.Equal(3, settings.RecentFiles.Count);
        Assert.EndsWith("4.json", settings.RecentFiles[0]);
        Assert.EndsWith("2.json", settings.RecentFiles[2]);
    }

    [Fact]
    public void PruneMissingRecentFiles_RemovesNonexistentPaths_AndReturnsWhetherChanged()
    {
        var settings = new AppSettings();
        var tempFile = Path.GetTempFileName();
        try
        {
            settings.AddRecentFile(tempFile);
            settings.AddRecentFile("definitely-does-not-exist-xyz.json");

            bool changed = settings.PruneMissingRecentFiles();

            Assert.True(changed);
            Assert.Single(settings.RecentFiles);
            Assert.Equal(Path.GetFullPath(tempFile), settings.RecentFiles[0]);

            bool changedAgain = settings.PruneMissingRecentFiles();
            Assert.False(changedAgain); // 既に整理済みなら変化なし
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Clone_DeepCopiesRecentFiles()
    {
        var settings = new AppSettings();
        settings.AddRecentFile("a.json");

        var clone = settings.Clone();
        clone.AddRecentFile("b.json");

        Assert.Single(settings.RecentFiles); // 元のインスタンスは影響を受けない
        Assert.Equal(2, clone.RecentFiles.Count);
    }
}
