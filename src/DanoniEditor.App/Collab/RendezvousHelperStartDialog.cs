using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App.Collab;

/// <summary>
/// 「仲介ヘルパーとして待機する」開始ダイアログ(設計メモ4.2節、CGNAT対応)。待受ポートのみの
/// 最小構成。仲介ヘルパー役は共同編集セッション本体とは無関係に動作する(自分がホスト/ゲストの
/// いずれであっても、あるいはどちらでもなくても、単に到達可能な回線を一時的に貸すだけの役割)。
/// </summary>
internal sealed class RendezvousHelperStartDialog : Window
{
    private readonly TextBox _portBox;

    public int Port { get; private set; } = 47622; // CollabProtocol.DefaultPort(47621)とは別番号にしておく

    public RendezvousHelperStartDialog(Window owner)
    {
        Title = "仲介ヘルパーとして待機";
        Owner = owner;
        Width = 380;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "自分の回線が普通に到達可能で、CGNAT配下の相手同士の接続を仲介する場合に使います。" +
                   "相手側にはこのポートを伝えてください。",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock { Text = "待受ポート", Margin = new Thickness(0, 0, 0, 4) });
        _portBox = new TextBox { Text = Port.ToString(), Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_portBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "待機を開始", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;

        ok.Click += (_, _) =>
        {
            if (!int.TryParse(_portBox.Text, out var port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, "ポート番号は1〜65535の範囲で指定してください。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Port = port;
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;

        _portBox.Focus();
        _portBox.SelectAll();
    }
}
