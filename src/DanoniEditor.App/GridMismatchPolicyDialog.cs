using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>
/// SKB/FUJIエクスポート時の「グリッドに乗らない位置の扱い」選択ダイアログ(2026-08-03要望対応)。
/// 丸める(既定)/削除するの二択のみの最小モーダルダイアログ(SimplePromptと同じ方針)。
/// </summary>
internal static class GridMismatchPolicyDialog
{
    /// <summary>true=丸める、false=削除する、null=キャンセル</summary>
    public static bool? Ask(Window owner, string title)
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            Width = 420,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "出力先の形式のグリッド(位置の分解能)に乗らないノート・フリーズ・BPM変化・速度変化が" +
                   "あった場合の扱いを選んでください。いずれの場合も対象は警告一覧でお知らせいたします。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var round = new RadioButton { Content = "丸める(最も近い位置へスナップして出力する)", IsChecked = true, Margin = new Thickness(0, 0, 0, 6), GroupName = "policy" };
        var delete = new RadioButton { Content = "削除する(そのオブジェクト自体を出力しない)", GroupName = "policy" };
        panel.Children.Add(round);
        panel.Children.Add(delete);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        bool okClicked = false;
        ok.Click += (_, _) => { okClicked = true; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        var result = win.ShowDialog();
        return (result == true && okClicked) ? round.IsChecked == true : null;
    }
}
