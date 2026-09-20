using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Collab.Protocol;

namespace DanoniEditor.App.Collab;

/// <summary>
/// 共同編集セッションへの参加ダイアログ(接続先アドレス+ポート+表示名、2026-09-20)。
/// CollabHostStartDialog同様、XAML無しのコード生成ウィンドウ。
/// </summary>
internal sealed class CollabJoinDialog : Window
{
    private readonly TextBox _addressBox;
    private readonly TextBox _portBox;
    private readonly TextBox _displayNameBox;

    public string HostAddress { get; private set; } = "";
    public int Port { get; private set; } = CollabProtocol.DefaultPort;
    public string DisplayName { get; private set; } = "";

    public CollabJoinDialog(Window owner, string defaultDisplayName)
    {
        Title = "共同編集: セッションに参加";
        Owner = owner;
        Width = 360;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "接続先アドレス(ホストのIP等)", Margin = new Thickness(0, 0, 0, 4) });
        _addressBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_addressBox);

        panel.Children.Add(new TextBlock { Text = "ポート", Margin = new Thickness(0, 0, 0, 4) });
        _portBox = new TextBox { Text = CollabProtocol.DefaultPort.ToString(), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_portBox);

        panel.Children.Add(new TextBlock { Text = "表示名", Margin = new Thickness(0, 0, 0, 4) });
        _displayNameBox = new TextBox { Text = defaultDisplayName, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_displayNameBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "接続", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_addressBox.Text))
            {
                MessageBox.Show(this, "接続先アドレスを入力してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(_portBox.Text, out var port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, "ポート番号は1〜65535の範囲で指定してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            HostAddress = _addressBox.Text.Trim();
            Port = port;
            DisplayName = string.IsNullOrWhiteSpace(_displayNameBox.Text) ? "無名の参加者" : _displayNameBox.Text.Trim();
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;

        _addressBox.Focus();
    }
}
