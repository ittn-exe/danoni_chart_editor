using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>「現在のタブをdosエクスポート」(2026-08-22要望対応、合作用途)の出力設定ダイアログ。
/// 出力するdos.txtデータ行のサフィックス番号(dataName{N}_data等の{N}部分)を、追加しない(既定、
/// 先頭タブ相当の名前)か、追加する(数値入力欄が有効化される)かをラジオボタンで選ばせる。
/// GridMismatchPolicyDialog/DosBpmSetupDialogと同じ、コード組み立てのみの最小モーダルダイアログ方針。</summary>
internal static class DosSuffixExportDialog
{
    /// <summary>SuffixNumber: nullならサフィックス無し、値があればその番号を{N}部分に使う。</summary>
    public readonly record struct Choice(int? SuffixNumber);

    public static Choice? Ask(Window owner)
    {
        var win = new Window
        {
            Title = "現在のタブをdosエクスポート",
            Owner = owner,
            Width = 420,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "現在の難易度タブのデータ行(note/freeze/ncolor/speed/boost/word_data)のみを" +
                   "dos.txt形式のテキストとして出力します(musicTitle等のプロジェクト共通ヘッダーや" +
                   "difDataは出力しません)。合作相手が持つ既存のdos.txtへ手動で貼り付けることを" +
                   "想定した機能ですので、貼り付け先で何番目のスロットに割り当てるかに合わせて" +
                   "サフィックス番号を指定してください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var noneRadio = new RadioButton
        {
            Content = "サフィックス番号を追加しない(1番目のタブ相当の名前で出力する)",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 6),
            GroupName = "DosSuffixMode",
        };
        panel.Children.Add(noneRadio);

        var addRadio = new RadioButton
        {
            Content = "サフィックス番号を追加する",
            Margin = new Thickness(0, 0, 0, 4),
            GroupName = "DosSuffixMode",
        };
        panel.Children.Add(addRadio);

        var suffixBox = new TextBox
        {
            Text = "2",
            Margin = new Thickness(20, 0, 0, 12),
            IsEnabled = false,
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(suffixBox);

        void UpdateEnabled() => suffixBox.IsEnabled = addRadio.IsChecked == true;
        noneRadio.Checked += (_, _) => UpdateEnabled();
        addRadio.Checked += (_, _) => UpdateEnabled();

        var errorText = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        Choice? result = null;
        ok.Click += (_, _) =>
        {
            if (addRadio.IsChecked == true)
            {
                if (!int.TryParse(suffixBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 2)
                {
                    errorText.Text = "サフィックス番号は2以上の整数で入力してください(1番目は「追加しない」に相当します)。";
                    return;
                }
                result = new Choice(n);
            }
            else
            {
                result = new Choice(null);
            }
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
