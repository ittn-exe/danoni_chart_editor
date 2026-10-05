using System.Text;

namespace DanoniEditor.Core.Persistence;

/// <summary>
/// Crash-safe text file writing. The content is written to a temporary file in the same directory,
/// flushed to disk, and then swapped into place, so that an interrupted write never leaves a
/// truncated or half-written target file.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(tmp, fullPath, null);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // Some file systems (e.g. certain network shares) do not support Replace.
                    File.Move(tmp, fullPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, fullPath);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
