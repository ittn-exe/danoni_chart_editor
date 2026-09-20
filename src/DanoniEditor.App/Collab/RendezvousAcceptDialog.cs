using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App.Collab;

/// <summary>
/// 「仲介ヘルパー経由で参加者を受け入れる」ダイアログ(設計メモ4.2節、CGNAT対応)。ホストとして
/// 待受中、自分自身がCGNAT配下で参加者から直接到達できない場合に使う。SessionCodeは相手側の
/// 「仲介ヘルパー経由でホストへ参加する」操作と同じ値を、Discord等で事前に口頭合わせしておく
/// 必要がある(参加者が複数いる場合は1人受け入れるたびに毎回別のコードで行う)。
/// </summary>
internal sealed class RendezvousAcceptDialog : Window
{
    private readonly TextBox _helperAddressBox;
    private readonly TextBox _helperPortBox;
    private readonly TextBox _sessionCodeBox;

    public string HelperAddress { get; private set; } = "";
    public int HelperPort { get; private set; } = 47622;
    public string SessionCode { get; private set; } = "";

    public RendezvousAcceptDialog(Window owner)
    {
        Title = "仲介ヘルパー経由で参加者を受け入れる";
        Owner = owner;
        Width = 380;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "自分(ホスト)がCGNAT配下等で直接到達できない場合、仲介ヘルパーを介して1人ぶんの" +
                   "参加を受け入れます。合言葉(セッションコード)は、相手側と事前に口頭等で合わせておき、" +
                   "参加者が複数いる場合は1人ずつ別のコードで行ってください。",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock { Text = "仲介ヘルパーのアドレス", Margin = new Thickness(0, 0, 0, 4) });
        _helperAddressBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_helperAddressBox);

        panel.Children.Add(new TextBlock { Text = "仲介ヘルパーのポート", Margin = new Thickness(0, 0, 0, 4) });
        _helperPortBox = new TextBox { Text = HelperPort.ToString(), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_helperPortBox);

        panel.Children.Add(new TextBlock { Text = "セッションコード(合言葉)", Margin = new Thickness(0, 0, 0, 4) });
        _sessionCodeBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_sessionCodeBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "受け入れる", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_helperAddressBox.Text))
            {
                MessageBox.Show(this, "仲介ヘルパーのアドレスを入力してください。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(_helperPortBox.Text, out var port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, "ポート番号は1〜65535の範囲で指定してください。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_sessionCodeBox.Text))
            {
                MessageBox.Show(this, "セッションコード(合言葉)を入力してください。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            HelperAddress = _helperAddressBox.Text.Trim();
            HelperPort = port;
            SessionCode = _sessionCodeBox.Text.Trim();
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;

        _helperAddressBox.Focus();
    }
}
