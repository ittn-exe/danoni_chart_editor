using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>新規プロジェクト作成時のキー種/難易度名/BPM入力ダイアログ(仕様書のプロジェクト新規作成UIの暫定版)</summary>
internal static class NewProjectDialog
{
    public readonly record struct Choice(string KeyTypeId, string DifficultyName, double Bpm);

    /// <summary>
    /// <paramref name="showBpm"/>=false(難易度タブ追加時、2026-07-26要望対応): カレントプロジェクトは
    /// 既にBPMを持っており、このダイアログで入力してもタブ追加処理側では使用されない(プロジェクト側の
    /// 値で上書きされる)ため、欄自体を非表示にする。戻り値のBpmは<paramref name="defaultBpm"/>をそのまま
    /// 返すのみで、呼び出し側でも使用しないこと。
    /// </summary>
    public static Choice? Ask(Window owner, TemplateRepository templates, double defaultBpm = 120, bool showBpm = true)
    {
        var keyTypeIds = templates.ListKeyTypeIds().ToList();

        var win = new Window
        {
            Title = "新規プロジェクト",
            Owner = owner,
            Width = 360,
            Height = showBpm ? 260 : 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock { Text = "キー種", Margin = new Thickness(0, 0, 0, 4) });
        var keyTypeCombo = new ComboBox { ItemsSource = keyTypeIds, Margin = new Thickness(0, 0, 0, 12) };
        keyTypeCombo.SelectedItem = keyTypeIds.Contains("5") ? "5" : keyTypeIds.FirstOrDefault();
        panel.Children.Add(keyTypeCombo);

        panel.Children.Add(new TextBlock { Text = "難易度名", Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Text = "Normal", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(nameBox);

        TextBox? bpmBox = null;
        if (showBpm)
        {
            panel.Children.Add(new TextBlock { Text = "BPM", Margin = new Thickness(0, 0, 0, 4) });
            bpmBox = new TextBox { Text = defaultBpm.ToString(System.Globalization.CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 16) }; // 環境設定のデフォルトBPM(2026-07-19b)
            panel.Children.Add(bpmBox);
        }

        var errorText = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "作成", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        Choice? result = null;
        ok.Click += (_, _) =>
        {
            if (keyTypeCombo.SelectedItem is not string keyTypeId || string.IsNullOrWhiteSpace(keyTypeId))
            {
                errorText.Text = "キー種を選択してください。";
                return;
            }
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "難易度名を入力してください。";
                return;
            }
            double bpm = defaultBpm;
            if (bpmBox is not null && (!double.TryParse(bpmBox.Text, out bpm) || bpm <= 0))
            {
                errorText.Text = "BPMは正の数で入力してください。";
                return;
            }
            result = new Choice(keyTypeId, nameBox.Text, bpm);
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
