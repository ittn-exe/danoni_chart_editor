using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace DanoniEditor.App;

/// <summary>
/// インポートウィンドウ(2026-07-30要望対応、TBD「FUJIファイル用D&Dウィンドウ・SKB用コピペウィンドウ」)。
/// 「ファイルを指定してインポート」の都度のダイアログ操作が煩雑という要望に応え、「ファイル >
/// インポートウィンドウ」から開く専用ウィンドウに、FUJIファイルのドラッグ&ドロップ領域と、SKBデータの
/// 直接貼り付け欄をまとめる。SKB欄はプレーンなTextBoxのため、Ctrl+V・右クリック貼り付けはWPF標準の
/// TextBoxコンテキストメニューでそのまま利用できる(追加実装不要)。
/// モードレス(開いたまま譜面ビューを操作でき、続けて複数回インポートできるよう閉じない)。
/// 実際のインポート処理自体はMainWindow.ImportFujiFile/ImportSkbTextへ委譲し、ここでは入力受付のみ行う。
/// </summary>
internal sealed class ImportHubWindow : Window
{
    private readonly MainWindow _owner;
    private readonly TextBox _skbTextBox = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 220,
        FontFamily = new FontFamily("Consolas"),
    };

    public ImportHubWindow(MainWindow owner)
    {
        _owner = owner;
        Owner = owner;

        Title = "インポートウィンドウ";
        Width = 480;
        Height = 600;
        MinWidth = 360;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new StackPanel { Margin = new Thickness(12) };

        // --- FUJIファイルD&Dエリア ---
        root.Children.Add(new TextBlock
        {
            Text = "FUJIエディタファイルのインポート",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "FUJIエディタのファイルをここへドラッグ&ドロップしてください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var fujiDropArea = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.WhiteSmoke,
            Height = 110,
            AllowDrop = true,
            Child = new TextBlock
            {
                Text = "ここにドロップ",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            },
        };
        fujiDropArea.DragEnter += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            fujiDropArea.Background = e.Effects == DragDropEffects.Copy ? Brushes.LightYellow : Brushes.WhiteSmoke;
            e.Handled = true;
        };
        fujiDropArea.DragLeave += (_, _) => fujiDropArea.Background = Brushes.WhiteSmoke;
        fujiDropArea.Drop += (_, e) =>
        {
            fujiDropArea.Background = Brushes.WhiteSmoke;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
                _owner.ImportFujiFile(paths[0]);
        };
        root.Children.Add(fujiDropArea);

        var browseFujiButton = new Button
        {
            Content = "またはファイルを選択してインポート...",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        browseFujiButton.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "FUJIエディタファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) _owner.ImportFujiFile(dlg.FileName);
        };
        root.Children.Add(browseFujiButton);

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 12) });

        // --- SKBデータ貼り付け欄 ---
        root.Children.Add(new TextBlock
        {
            Text = "SKBデータの貼り付けインポート",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "SKBエディタのデータをコピーし、下の欄へCtrl+Vまたは右クリック貼り付けしてください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(_skbTextBox);

        var skbImportButton = new Button
        {
            Content = "インポート実行",
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        skbImportButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_skbTextBox.Text))
            {
                MessageBox.Show(this, "SKBデータが貼り付けられていません。", "インポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _owner.ImportSkbText(_skbTextBox.Text, "クリップボードからの貼り付け");
            _skbTextBox.Clear();
        };
        root.Children.Add(skbImportButton);

        var closeButton = new Button
        {
            Content = "閉じる",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        closeButton.Click += (_, _) => Close();
        root.Children.Add(closeButton);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
