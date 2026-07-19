using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Import;

namespace DanoniEditor.App;

/// <summary>
/// FUJIインポート時、difDataに同一キー種の候補が複数あった場合に、使用する難易度を選ばせる
/// 最小モーダルダイアログ(2026-07-16e追加要望)。SimplePrompt/DisplaySettingsDialogと同じ
/// 自前ダイアログ方式。表示ラベルは「(キー種) - (難易度名)」形式(ユーザー指定)。
/// </summary>
internal static class DifDataPickerDialog
{
    /// <summary>選ばれた候補を返す。キャンセル時はnull。
    /// fileNameが指定されていれば、どのファイルのインポートかをタイトル・本文に表示する
    /// (2026-07-17: 「自分がどのファイルをインポートしようとしているのか忘れる」との要望対応)。</summary>
    public static DifDataCandidate? Ask(Window owner, IReadOnlyList<DifDataCandidate> candidates, string? fileName = null)
    {
        var win = new Window
        {
            Title = fileName is null ? "難易度の選択" : $"難易度の選択 - {fileName}",
            Owner = owner,
            Width = 360,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        if (fileName is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"インポート中のファイル: {fileName}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "difDataに同じキー種の難易度候補が複数見つかりましたわ。使用する難易度を選んでくださいませ。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var combo = new ComboBox
        {
            ItemsSource = candidates,
            DisplayMemberPath = nameof(DifDataCandidate.Label),
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(combo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        DifDataCandidate? result = null;
        ok.Click += (_, _) =>
        {
            result = combo.SelectedItem as DifDataCandidate;
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
