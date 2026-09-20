using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DanoniEditor.App.Plugins;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>デバッグ用の環境報告書き出し(2026-07-26要望対応)。「設定 > 環境報告作成」から呼ばれる。
/// OS・.NET・エディタの設定値・プラグイン読み込み状況など、バグ報告時に添えてもらう情報源となる
/// テキストファイルを1つにまとめて生成する。ファイルパス・楽曲URL等の個人情報寄りの項目は
/// AppSettingsのRecentFiles等に含まれうるため、共有前に内容を確認するよう案内する。</summary>
internal static class DiagnosticsReport
{
    private static readonly JsonSerializerOptions SettingsJsonOpts = new() { WriteIndented = true };

    public static string Build(AppSettings settings, PluginManager pluginManager, VisualTestDiagnostics? lastVisualTestDiag = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== ダンおに譜面エディタ 環境報告 ===");
        sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (ローカル)");
        sb.AppendLine();

        sb.AppendLine("--- アプリケーション ---");
        // 2026-07-26: csprojの<Version>(手動管理、AssemblyVersionではなくInformationalVersionを読む。
        // 後者は数字4つ限定だが、こちらは自由な文字列(将来的に日付・コードネーム等を足しても対応できる)。
        var version = typeof(DiagnosticsReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticsReport).Assembly.GetName().Version?.ToString()
            ?? "(不明)";
        sb.AppendLine($"バージョン: {version}");
        sb.AppendLine($"実行フォルダ: {AppContext.BaseDirectory}");
        sb.AppendLine();

        sb.AppendLine("--- 実行環境 ---");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($".NETランタイム: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"プロセスアーキテクチャ: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"OSアーキテクチャ: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"カルチャ: {CultureInfo.CurrentCulture.Name}");
        sb.AppendLine($"プロセッサ数: {Environment.ProcessorCount}");
        sb.AppendLine();

        sb.AppendLine("--- プラグイン ---");
        if (pluginManager.Plugins.Count == 0)
        {
            sb.AppendLine("(読み込まれているプラグインはありません)");
        }
        else
        {
            foreach (var p in pluginManager.Plugins)
                sb.AppendLine($"- {p.Id} \"{p.Name}\" v{p.Version}");
        }
        if (pluginManager.LoadErrors.Count > 0)
        {
            sb.AppendLine("読み込みエラー:");
            foreach (var err in pluginManager.LoadErrors)
                sb.AppendLine($"- {err}");
        }
        sb.AppendLine();

        // 2026-09-07要望対応: 「Spaceで目視テストを開始しても無音・再生位置ラインが動かない」不具合の
        // 切り分け用(VisualTestDiagnostics参照)。直近1回分の開始試行があれば含める。
        sb.AppendLine("--- 目視テスト診断情報(直近1回の開始試行) ---");
        sb.AppendLine(lastVisualTestDiag is { } diag
            ? diag.Format()
            : "(記録なし。この起動セッションで目視テストを一度も開始していません)");
        sb.AppendLine();

        sb.AppendLine("--- 環境設定(settings.json相当) ---");
        sb.AppendLine("※ 最近開いたファイルのパス等、環境固有の情報が含まれます。共有前に内容をご確認ください。");
        sb.AppendLine(JsonSerializer.Serialize(settings, SettingsJsonOpts));

        return sb.ToString();
    }
}
