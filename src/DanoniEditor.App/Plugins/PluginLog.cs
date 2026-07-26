using System.IO;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// プラグイン関連の診断ログ(2026-07-26)。プラグインの例外は本体を守るため個別に握りつぶす設計に
/// なっているが、それだけでは外部制作者が不具合の原因を全く追えなくなってしまうため、
/// 最低限「何が起きたか」をファイルへ残す。UIへの通知(MessageBox等)とは独立して常に記録する。
/// </summary>
internal static class PluginLog
{
    private static string LogFilePath => Path.Combine(AppPaths.PluginsDir, "plugin_log.txt");

    /// <summary>1行分のログを追記する。ログ自体の書き込み失敗(フォルダ無し等)は本体へ影響させず、
    /// 静かに諦める。</summary>
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.PluginsDir);
            File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* ログ自体が書けない状況(権限等)は諦める。本体の動作には影響させない */ }
    }
}
