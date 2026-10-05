using System.IO;

namespace DanoniEditor.App;

/// <summary>
/// アプリ共通の診断ログ(2026-10-05)。描画例外など「アプリを落とさず握るが、原因は追えるようにしたい」
/// 事象の記録に使う。ログ自体の書き込み失敗は本体へ影響させない。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string LogFilePath => Path.Combine(AppPaths.SettingsDir, "app_log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.SettingsDir);
                var path = LogFilePath;
                // 肥大化防止: 上限を超えたら古いログを1世代だけ残して作り直す
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch { /* ログが書けない状況は諦める */ }
    }

    public static void Write(string context, Exception ex) => Write($"{context}: {ex}");
}
