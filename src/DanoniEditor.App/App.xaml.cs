using System.Windows;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

public partial class App : Application
{
    /// <summary>
    /// 2026-07-28要望: 起動の立ち上がりが重く感じるため、スプラッシュウィンドウ(プログレスバー+
    /// 現在の処理内容のテキスト表示)を出す。WPFの起動処理はUIスレッド上で同期的に進むため、
    /// 各段階でSplashWindow.Report(実処理の直前に呼ぶ)を挟むことで、進捗表示と実際の重い処理
    /// (設定読込・テンプレート確認・MainWindow構築=InitializeComponent)を1:1に対応させる。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        try
        {
            splash.Report("環境設定を読み込み中...", 15);
            var settings = AppSettings.Load(AppPaths.SettingsFilePath);

            splash.Report("テンプレート・アセットを確認中...", 45);
            var templateDir = AppPaths.FindAssetDir("template")
                ?? throw new System.IO.DirectoryNotFoundException(
                    "./template フォルダが見つかりません(実行ファイルと同じ場所、および上位ディレクトリを探索しました)");
            var templates = new TemplateRepository(templateDir);

            splash.Report("メイン画面を構築中...", 75);
            var main = new MainWindow(settings, templates);

            splash.Report("起動完了", 100);
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"起動に失敗しましたわ: {ex.Message}", "起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        finally
        {
            splash.Close();
        }
    }
}
