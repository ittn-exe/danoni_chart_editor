using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;



/// <summary>
/// danoniplus本体互換の「カスタムキー」定義テキスト(|keyCtrlX=...|等)を貼り付けて、エディタの
/// キー種テンプレート(temp_{keyTypeId}.json)として取り込むウィンドウ(2026-08-03要望対応、
/// 「出力ができるなら入力もできるべき」との方針に基づき、CustomKeyExportWindowと対になる形で新設)。
///
/// カスタムキー定義のテキスト側には無い情報(laneId・keyboardInputKeys・engineLaneNum・
/// frzDataNameOverride)は仮の値で埋めて取り込むため、取り込み後は必ずテンプレートエディタで
/// 内容を確認・修正することを前提とする(CustomKeyTemplateImporter.Import参照)。
/// </summary>
internal sealed class CustomKeyImportWindow : Window
{
    private readonly string _templateDir;
    private readonly TemplateRepository _templates;

    private readonly TextBox _pasteBox = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _keyTypeIdOverride = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _layoutCombo = new()
    {
        Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[] { KeyboardLayout.Us, KeyboardLayout.Jis }, SelectedIndex = 0,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.Orange };

    /// <summary>取り込みに成功した場合の保存先パス(呼び出し元がテンプレートエディタを開く用)</summary>
    public string? SavedPath { get; private set; }

    /// <summary>取り込みに成功した場合の、確定できなかった項目の位置情報(2026-08-03要望対応、
    /// 呼び出し元がテンプレートエディタをこの情報付きで開き、ハイライト表示させる用)。</summary>
    public ImportFieldStatus? FieldStatus { get; private set; }

    public CustomKeyImportWindow(string templateDir)
    {
        _templateDir = templateDir;
        // 2026-08-03: 本体側の「他キー種/パターンの値を丸ごと再利用する」略記("5_0"等)を、
        // 標準キー種のエディタ用テンプレート(temp_5.json等、既にエディタに揃っている)から
        // 解決できるようにするため、自前のTemplateRepositoryを持つ(MainWindow側のインスタンスとは
        // 別だが、同じtemplateDirを読むので取り込み用途としては問題ない)。
        _templates = new TemplateRepository(templateDir);

        Title = "カスタムキー定義からインポート";
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(12) };
        for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 貼り付け欄
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // ステータス

        var instructions = new TextBlock
        {
            Text = "dos.txtのヘッダー部やdanoni_settings.js等に書かれている、danoniplus本体互換の" +
                   "カスタムキー定義テキスト(|keyCtrlX=...|等)を下の欄へ貼り付けてくださいまし。" +
                   "keyTypeIdは通常自動検出しますが、複数のキー種が混在するテキストの場合は" +
                   "明示的に指定してくださいまし。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(instructions, 0);
        root.Children.Add(instructions);

        var idRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        idRow.Children.Add(new TextBlock { Text = "keyTypeId(省略時は自動検出):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        idRow.Children.Add(_keyTypeIdOverride);
        Grid.SetRow(idRow, 1);
        root.Children.Add(idRow);

        var layoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        layoutRow.Children.Add(new TextBlock { Text = "キーボード配列("+"\"@\"/\"[\"/\"]\"等の記号キーの解釈):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        layoutRow.Children.Add(_layoutCombo);
        Grid.SetRow(layoutRow, 2);
        root.Children.Add(layoutRow);

        var pasteLabel = new TextBlock { Text = "カスタムキー定義テキスト:", Margin = new Thickness(0, 0, 0, 2), FontWeight = FontWeights.Bold };
        Grid.SetRow(pasteLabel, 3);
        root.Children.Add(pasteLabel);

        Grid.SetRow(_pasteBox, 4);
        root.Children.Add(_pasteBox);

        Grid.SetRow(_status, 5);
        root.Children.Add(_status);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var importButton = new Button { Content = "取り込み", Width = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        importButton.Click += ImportButton_Click;
        buttonsPanel.Children.Add(importButton);
        buttonsPanel.Children.Add(cancelButton);

        var outer = new DockPanel();
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        outer.Children.Add(buttonsPanel);
        outer.Children.Add(root);
        return outer;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        _status.Text = "";

        if (string.IsNullOrWhiteSpace(_pasteBox.Text))
        { _status.Text = "カスタムキー定義テキストを貼り付けてくださいまし。"; return; }

        var layout = _layoutCombo.SelectedItem is KeyboardLayout kbl ? kbl : KeyboardLayout.Us;
        var idOverride = string.IsNullOrWhiteSpace(_keyTypeIdOverride.Text) ? null : _keyTypeIdOverride.Text.Trim();

        CustomKeyImportResult result;
        try
        {
            result = CustomKeyTemplateImporter.Import(_pasteBox.Text, layout, idOverride, _templates.Get);
        }
        catch (Exception ex)
        {
            _status.Text = $"取り込みに失敗しましたわ: {ex.Message}";
            return;
        }

        var destPath = Path.Combine(_templateDir, $"temp_{result.Template.KeyTypeId}.json");
        if (File.Exists(destPath))
        {
            var overwrite = MessageBox.Show(this,
                $"temp_{result.Template.KeyTypeId}.json は既に存在しますの。上書きしてよろしいですか?",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes) { _status.Text = "取り込みを中止しましたわ。"; return; }
        }

        try
        {
            result.Template.Save(destPath);
        }
        catch (Exception ex)
        {
            _status.Text = $"保存に失敗しましたわ: {ex.Message}";
            return;
        }

        if (result.Warnings.Count > 0)
            MessageBox.Show(this,
                "取り込みは完了いたしましたが、以下の点をテンプレートエディタでご確認くださいまし。\n\n" +
                string.Join("\n\n", result.Warnings.Select(w => "・" + w)),
                "取り込み完了(要確認)", MessageBoxButton.OK, MessageBoxImage.Information);

        SavedPath = destPath;
        FieldStatus = result.FieldStatus;
        DialogResult = true;
    }
}
