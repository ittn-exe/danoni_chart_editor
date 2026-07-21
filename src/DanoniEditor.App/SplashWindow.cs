using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DanoniEditor.App;

/// <summary>
/// 起動時スプラッシュウィンドウ(2026-07-28要望: 「立ち上がりが重たく感じる」ため、
/// プログレスバー+現在の起動処理内容を表示する)。App.OnStartupから使う。
/// 他の補助ウィンドウ(PreferencesWindow等)と同じく、XAMLを使わず全てコードで組み立てる。
/// </summary>
internal sealed class SplashWindow : Window
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;

    public SplashWindow()
    {
        Title = "起動中...";
        Width = 420;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.White;

        var border = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20),
        };

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "ダンおに譜面エディタ",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        });

        _statusText = new TextBlock
        {
            Text = "起動しています...",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(_statusText);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16, Value = 0 };
        panel.Children.Add(_progressBar);

        border.Child = panel;
        Content = border;
    }

    /// <summary>起動段階のメッセージ・進捗率(0〜100)を更新し、即座に画面へ反映させる。
    /// WPFの起動処理はUIスレッド上で同期的に進むため、更新直後にDispatcherへ低優先度の
    /// 空アクションを積んで保留中の描画(Render)を強制的にフラッシュしてから処理を続ける
    /// (でないとテキスト・バーの変化が画面に出る前に次の重い処理へ進んでしまい、スプラッシュが
    /// 固まって見える)。</summary>
    public void Report(string message, double percent)
    {
        _statusText.Text = message;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }
}
