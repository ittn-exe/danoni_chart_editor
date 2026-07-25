using System.Windows;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

public partial class App : Application
{
    /// <summary>このプロセス(ウィンドウ)自身を識別するID(2026-07-26、複数ウィンドウ対応でクラッシュ
    /// フラグをインスタンス単位に分離するために追加)。OnStartupで生成し、OnExitでの自分自身の
    /// フラグ削除に使う。</summary>
    private string? _instanceId;

    /// <summary>
    /// 2026-07-26要望: 起動の立ち上がりが重く感じるため、スプラッシュウィンドウ(プログレスバー+
    /// 現在の処理内容のテキスト表示)を出す。WPFの起動処理はUIスレッド上で同期的に進むため、
    /// 各段階でSplashWindow.Report(実処理の直前に呼ぶ)を挟むことで、進捗表示と実際の重い処理
    /// (設定読込・テンプレート確認・MainWindow構築=InitializeComponent)を1:1に対応させる。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2026-07-26: Sonar同様の「クラッシュを検出して現在のデータを保存するか選べる」機能。
        // UIスレッドの未処理例外はDispatcherUnhandledExceptionで捕捉できるため、強制終了する前に
        // 緊急保存するかどうかをユーザーに選ばせる。非UIスレッドの致命的例外(AppDomain側)は
        // ダイアログを介さず可能な限り静かに緊急保存だけ試みる(復旧を試みても続行は保証できないため)。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { (MainWindow as MainWindow)?.EmergencySaveAllSessions(); } catch { /* できる範囲で試みるだけ */ }
        };

        var splash = new SplashWindow();
        splash.Show();

        try
        {
            splash.Report("環境設定を読み込み中...", 15);
            var settings = AppSettings.Load(AppPaths.SettingsFilePath);

            // 2026-07-25: クラッシュ復旧(TBD)。2026-07-26: 「新しいウィンドウ」機能で同一exeが
            // 複数プロセス同時実行され得るため、このプロセス専用のインスタンスIDを発行し、
            // フラグ・manifestの持ち主として使う。他プロセス(まだ正常に開いている別ウィンドウ)の
            // フラグに記録されたPIDが実際に生きているかで「本当にクラッシュしたインスタンス」だけを
            // 判定し、そのインスタンスに属するセッションだけを復旧候補として保持する。
            _instanceId = Guid.NewGuid().ToString("N");
            var aliveInstanceIds = AutoSaveManager.GetAliveInstanceIds(AppPaths.AutoSaveDir);
            var manifest = AutoSaveManager.LoadManifest(AppPaths.AutoSaveDir);
            var recoverableSlots = manifest.Where(s => !aliveInstanceIds.Contains(s.InstanceId)).ToList();
            bool crashSuspected = recoverableSlots.Count > 0;
            // 生きていないインスタンスの古いフラグはここで掃除しておく(次回以降の走査を汚さないため)。
            var deadInstanceIds = recoverableSlots.Select(s => s.InstanceId).Distinct();
            AutoSaveManager.PurgeDeadInstanceFlags(AppPaths.AutoSaveDir, deadInstanceIds);
            AutoSaveManager.SetCrashFlag(AppPaths.AutoSaveDir, _instanceId, Environment.ProcessId);

            // 2026-07-26: 統計情報(環境設定 > 統計情報)。起動の都度カウントし、クラッシュ検出時も
            // ここで加算しておく(この後MainWindowへ渡るsettingsインスタンスがそのまま_appSettingsになる)。
            settings.StatAppLaunchCount++;
            if (crashSuspected) settings.StatCrashCount++;
            settings.Save(AppPaths.SettingsFilePath);

            splash.Report("テンプレート・アセットを確認中...", 45);
            var templateDir = AppPaths.FindAssetDir("template")
                ?? throw new System.IO.DirectoryNotFoundException(
                    "./template フォルダが見つかりません(実行ファイルと同じ場所、および上位ディレクトリを探索しました)");
            var templates = new TemplateRepository(templateDir);

            splash.Report("メイン画面を構築中...", 75);
            var main = new MainWindow(settings, templates, _instanceId);

            splash.Report("起動完了", 100);
            MainWindow = main;
            main.Show();

            // 2026-07-25: 復旧候補があれば、メイン画面表示後に1件ずつ確認する
            // (MessageBoxをオーナー付きで出すため、Show()より後である必要がある)。
            if (recoverableSlots.Count > 0)
                main.OfferCrashRecovery(recoverableSlots);
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

    /// <summary>2026-07-25: 正常終了時のみ到達する(OnClosingでe.Cancel=trueにされた場合はここへ来ない)。
    /// 自分自身のクラッシュフラグだけを消し、次回起動時に「クラッシュした」と誤検知しないようにする
    /// (2026-07-26: 他のウィンドウ[プロセス]のフラグには触れない)。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceId is not null) AutoSaveManager.ClearCrashFlag(AppPaths.AutoSaveDir, _instanceId);
        base.OnExit(e);
    }

    /// <summary>2026-07-26: UIスレッドで未処理例外が発生した際のハンドラ。強制終了する前に、
    /// 編集中データを緊急保存するかどうかをユーザーに選ばせる(Sonar等と同様の挙動)。
    /// 保存する/しないに関わらず、この後は状態が不定なため常にアプリを終了する
    /// (「はい」を選んでも処理続行はしない。緊急保存したデータは次回起動時のクラッシュ復旧フローで
    /// 復元できる)。</summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var result = MessageBox.Show(
            $"予期しないエラーが発生しましたわ。\n\n{e.Exception.Message}\n\n" +
            "このままではアプリを終了する必要がありますが、その前に編集中のデータを緊急保存しますか?\n" +
            "(保存したデータは次回起動時に「クラッシュ復旧」として復元できます)",
            "予期しないエラー", MessageBoxButton.YesNo, MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes && MainWindow is MainWindow main)
        {
            try
            {
                int saved = main.EmergencySaveAllSessions();
                MessageBox.Show(saved > 0 ? $"{saved}件のプロジェクトを緊急保存しましたわ。" : "保存が必要な変更はありませんでしたわ。",
                    "緊急保存", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception saveEx)
            {
                MessageBox.Show($"緊急保存にも失敗しましたわ: {saveEx.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        e.Handled = true;
        Shutdown(-1);
    }
}
