using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>プロジェクト読込時に重複ノート(同一レーン・同一tickへの複数配置)が見つかった場合の
/// 解決方法(2026-08-08新設)。</summary>
internal enum DuplicateNoteResolution
{
    /// <summary>該当箇所の2件目以降のノートを削除する</summary>
    Resolve,
    /// <summary>削除はせず、該当箇所へ警告アイコンを設定するのみに留める</summary>
    Keep,
}

/// <summary>
/// プロジェクト読込時に重複ノートを検出した場合の確認ダイアログ(2026-08-08新設、第三者報告の
/// ノート重複不具合対応)。「気付かないまま重複ノートを含んだ状態で保存されていた過去のプロジェクト」
/// を開いた際に表示する。GridMismatchPolicyDialogと同じ、二択+OK/キャンセルの最小モーダル構成。
/// </summary>
internal static class DuplicateNoteDialog
{
    /// <summary>Resolve=重複を解消、Keep=重複を維持、null=キャンセル(何もせずそのまま開く)</summary>
    public static DuplicateNoteResolution? Ask(Window owner, int locationCount)
    {
        var win = new Window
        {
            Title = "重複ノートの検出",
            Owner = owner,
            Width = 460,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"このプロジェクトには、同じレーンの同じ位置に複数のノートが重なっている箇所が" +
                   $"{locationCount}件見つかりました。どちらの方法で解決するか選んでください。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var resolve = new RadioButton
        {
            Content = "重複を解消する(該当箇所の2件目以降のノートを削除する)",
            IsChecked = true, Margin = new Thickness(0, 0, 0, 6), GroupName = "policy",
        };
        var keep = new RadioButton
        {
            Content = "重複を維持する(削除せず、該当箇所に警告アイコンを設定する)",
            GroupName = "policy",
        };
        panel.Children.Add(resolve);
        panel.Children.Add(keep);

        panel.Children.Add(new TextBlock
        {
            Text = "キャンセルした場合、重複はそのままの状態で開きます。",
            Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });

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
        if (result != true || !okClicked) return null;
        return resolve.IsChecked == true ? DuplicateNoteResolution.Resolve : DuplicateNoteResolution.Keep;
    }
}
