using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>dos.txtインポート(新規プロジェクト作成時のみ、2026-08-02要望対応)の冒頭で、
/// BPM決定方法を「自動検出」(従来のde_*/es_*埋め込み優先→任意でノート分布から自動推定→
/// 見つからなければ既定BPM仮定)と「手動入力」(ユーザーが直接BPMを指定し、それを最優先で使う。
/// DosImportOptions.TimingOverrideへ渡す)のどちらにするか選ばせるダイアログ。
/// 既存プロジェクトへタブとして追加する場合はプロジェクト共通のBPMを使うためこのダイアログ自体を
/// 出さない(呼び出し元のImportDosFile参照)。</summary>
internal static class DosBpmSetupDialog
{
    /// <summary>AutoEstimate: 自動推定を試みるか(手動入力時はfalse固定で無関係)。
    /// ManualBpm: 手動入力時のみ値を持つ(nullなら自動検出モード)。</summary>
    public readonly record struct Choice(bool AutoEstimate, double? ManualBpm);

    public static Choice? Ask(Window owner, double defaultBpm)
    {
        var win = new Window
        {
            Title = "dos.txtインポート - BPM設定",
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
            Text = "BPMの決定方法を選んでください。",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        var autoRadio = new RadioButton
        {
            Content = "自動検出(de_*/es_*埋め込み情報を優先。見つからなければ後述の推定/既定値)",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 4),
            GroupName = "DosBpmMode",
        };
        panel.Children.Add(autoRadio);

        var autoEstimateCheck = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "タイミング情報が見つからない場合、ノートの分布からBPMを自動推定してみる(推定できなければ既定BPM=120・4/4拍子を仮定)",
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(20, 0, 0, 12),
        };
        panel.Children.Add(autoEstimateCheck);

        var manualRadio = new RadioButton
        {
            Content = "手動入力(以下のBPM値をそのまま採用し、de_*/es_*等の情報より優先します)",
            Margin = new Thickness(0, 0, 0, 4),
            GroupName = "DosBpmMode",
        };
        panel.Children.Add(manualRadio);

        var bpmBox = new TextBox
        {
            Text = defaultBpm.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(20, 0, 0, 12),
            IsEnabled = false,
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(bpmBox);

        void UpdateEnabled()
        {
            autoEstimateCheck.IsEnabled = autoRadio.IsChecked == true;
            bpmBox.IsEnabled = manualRadio.IsChecked == true;
        }
        autoRadio.Checked += (_, _) => UpdateEnabled();
        manualRadio.Checked += (_, _) => UpdateEnabled();

        var errorText = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "次へ", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        Choice? result = null;
        ok.Click += (_, _) =>
        {
            if (manualRadio.IsChecked == true)
            {
                if (!double.TryParse(bpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) || bpm <= 0)
                {
                    errorText.Text = "BPMは正の数で入力してください。";
                    return;
                }
                result = new Choice(AutoEstimate: false, ManualBpm: bpm);
            }
            else
            {
                result = new Choice(AutoEstimate: autoEstimateCheck.IsChecked == true, ManualBpm: null);
            }
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
