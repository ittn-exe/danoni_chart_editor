using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>
/// 1行テキスト入力だけの最小モーダルダイアログ。Microsoft.VisualBasic.Interaction.InputBox相当を
/// 依存追加なしで用意する(FUJIインポート時のキー種ID入力など、暫定UIとして使う)。
/// </summary>
internal static class SimplePrompt
{
    public static string? Ask(Window owner, string title, string message, string defaultValue = "")
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(textBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        bool okClicked = false;
        ok.Click += (_, _) => { okClicked = true; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        textBox.Focus();
        textBox.SelectAll();

        var result = win.ShowDialog();
        return (result == true && okClicked) ? textBox.Text : null;
    }
}
