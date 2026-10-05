using DanoniEditor.Core.Persistence;

namespace DanoniEditor.Core.Tests.Persistence;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "danoni_atomic_test_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WriteAllText_CreatesDirectoryAndFile()
    {
        var path = Path.Combine(_dir, "sub", "a.json");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile_WithoutLeavingTempFiles()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void WriteAllText_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(path, "あ");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xE3, 0x81, 0x82 }, bytes);
    }
}
