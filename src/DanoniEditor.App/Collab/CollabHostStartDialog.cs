using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Collab.Protocol;

namespace DanoniEditor.App.Collab;

/// <summary>
/// 共同編集セッションのホスト開始ダイアログ(表示名+待受ポートのみの最小構成、2026-09-20)。
/// SimplePrompt.cs同様、XAML無しのコード生成ウィンドウとする(暫定UIとして最小構成に留める)。
/// </summary>
internal sealed class CollabHostStartDialog : Window
{
    private readonly TextBox _displayNameBox;
    private readonly TextBox _portBox;

    public string DisplayName { get; private set; } = "";
    public int Port { get; private set; } = CollabProtocol.DefaultPort;

    public CollabHostStartDialog(Window owner, string defaultDisplayName)
    {
        Title = "共同編集: ホストを開始";
        Owner = owner;
        Width = 360;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "表示名", Margin = new Thickness(0, 0, 0, 4) });
        _displayNameBox = new TextBox { Text = defaultDisplayName, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_displayNameBox);

        panel.Children.Add(new TextBlock { Text = "待受ポート", Margin = new Thickness(0, 0, 0, 4) });
        _portBox = new TextBox { Text = CollabProtocol.DefaultPort.ToString(), Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(_portBox);
        panel.Children.Add(new TextBlock
        {
            Text = "※ルーター等でこのポートを転送しておく必要があります。",
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "開始", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;

        ok.Click += (_, _) =>
        {
            if (!int.TryParse(_portBox.Text, out var port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, "ポート番号は1〜65535の範囲で指定してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DisplayName = string.IsNullOrWhiteSpace(_displayNameBox.Text) ? "無名の参加者" : _displayNameBox.Text.Trim();
            Port = port;
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;

        _displayNameBox.Focus();
        _displayNameBox.SelectAll();
    }
}
