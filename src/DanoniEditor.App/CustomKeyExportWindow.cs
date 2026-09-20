using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// キー種テンプレートを、danoniplus本体互換の「カスタムキー」定義テキストとして書き出すウィンドウ
/// (2026-08-02要望対応)。「環境設定 > テンプレート」の一覧で選択中のテンプレート1件のみを対象とする。
/// 出力はdos.txt / danoni_settings.js のどちらに貼り付けても書式は同じため、テキストの塊を
/// template/カスタムキー定義_{KeyTypeId}.txt として保存するだけに留め、コピペ運用とする。
/// keyHelpJa/keyHelpEn(本体側にはあるがエディタのテンプレートには対応フィールドが無い項目)は
/// このウィンドウで入力する(省略可)。入力のたびにプレビューへ即座に反映する。
/// </summary>
internal sealed class CustomKeyExportWindow : Window
{
    private readonly KeyTemplate _template;
    private readonly string _templateDir;

    private readonly TextBox _keyHelpJaBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 50 };
    private readonly TextBox _keyHelpEnBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 50 };
    private readonly TextBox _previewBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Brushes.Black,
        Foreground = Brushes.White,
    };
    private readonly TextBlock _status = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public CustomKeyExportWindow(KeyTemplate template, string templateDir)
    {
        _template = template;
        _templateDir = templateDir;

        Title = $"カスタムキー定義へエクスポート - {template.KeyTypeId}";
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        // keyHelpJaはKeyTemplate.Commentを初期値として流用する(2026-08-02、大きな問題が無ければ
        // そのまま採用してよいとのユーザー確定方針)。Commentは自由記述欄であり、本体側の
        // keyHelpJaも「説明ページ」用の自由記述テキストのため、意味の齟齬は無いと判断。
        _keyHelpJaBox.Text = template.Comment ?? "";

        Content = BuildLayout();
        UpdatePreview();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(12) };
        for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star); // プレビュー行だけ残り全部を使う

        var instructions = new TextBlock
        {
            Text = $"テンプレート「{_template.KeyTypeId} ({_template.KeyTypeName})」を、danoniplus本体互換の" +
                   "カスタムキー定義テキストとして書き出します。下のプレビューをそのままdos.txtの" +
                   "ヘッダー部、またはdanoni_settings.js等の共通設定ファイルへコピー&ペーストしてお使いください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(instructions, 0);
        root.Children.Add(instructions);

        var helpJaCell = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        helpJaCell.Children.Add(new TextBlock { Text = "説明文(keyHelpJa、省略可):", Margin = new Thickness(0, 0, 0, 2) });
        helpJaCell.Children.Add(_keyHelpJaBox);
        Grid.SetRow(helpJaCell, 1);
        root.Children.Add(helpJaCell);

        var helpEnCell = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        helpEnCell.Children.Add(new TextBlock { Text = "説明文(keyHelpEn、省略可):", Margin = new Thickness(0, 0, 0, 2) });
        helpEnCell.Children.Add(_keyHelpEnBox);
        Grid.SetRow(helpEnCell, 2);
        root.Children.Add(helpEnCell);

        _keyHelpJaBox.TextChanged += (_, _) => UpdatePreview();
        _keyHelpEnBox.TextChanged += (_, _) => UpdatePreview();

        var previewLabel = new TextBlock { Text = "プレビュー:", Margin = new Thickness(0, 8, 0, 2), FontWeight = FontWeights.Bold };
        Grid.SetRow(previewLabel, 3);
        root.Children.Add(previewLabel);

        Grid.SetRow(_previewBox, 4);
        root.Children.Add(_previewBox);

        Grid.SetRow(_status, 5);
        root.Children.Add(_status);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var exportButton = new Button { Content = "書き出し", Width = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var closeButton = new Button { Content = "閉じる", Width = 90, IsCancel = true };
        exportButton.Click += ExportButton_Click;
        buttonsPanel.Children.Add(exportButton);
        buttonsPanel.Children.Add(closeButton);

        var outer = new DockPanel();
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        outer.Children.Add(buttonsPanel);
        outer.Children.Add(root);
        return outer;
    }

    private void UpdatePreview() =>
        _previewBox.Text = CustomKeyTemplateExporter.Export(_template, _keyHelpJaBox.Text, _keyHelpEnBox.Text);

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(_templateDir, $"カスタムキー定義_{_template.KeyTypeId}.txt");
            File.WriteAllText(path, _previewBox.Text);
            _status.Foreground = Brushes.LightGreen;
            _status.Text = $"書き出しました: {path}";
        }
        catch (Exception ex)
        {
            _status.Foreground = Brushes.Orange;
            _status.Text = $"書き出しに失敗いたしました: {ex.Message}";
        }
    }
}
