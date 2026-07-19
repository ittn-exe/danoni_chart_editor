using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>新規プロジェクト作成時のキー種/難易度名/BPM入力ダイアログ(仕様書のプロジェクト新規作成UIの暫定版)</summary>
internal static class NewProjectDialog
{
    public readonly record struct Choice(string KeyTypeId, string DifficultyName, double Bpm);

    public static Choice? Ask(Window owner, TemplateRepository templates, double defaultBpm = 120)
    {
        var keyTypeIds = templates.ListKeyTypeIds().ToList();

        var win = new Window
        {
            Title = "新規プロジェクト",
            Owner = owner,
            Width = 360,
            Height = 260,
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

        panel.Children.Add(new TextBlock { Text = "BPM", Margin = new Thickness(0, 0, 0, 4) });
        var bpmBox = new TextBox { Text = defaultBpm.ToString(System.Globalization.CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 16) }; // 環境設定のデフォルトBPM(2026-07-19b)
        panel.Children.Add(bpmBox);

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
                errorText.Text = "キー種を選択してくださいませ。";
                return;
            }
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "難易度名を入力してくださいませ。";
                return;
            }
            if (!double.TryParse(bpmBox.Text, out var bpm) || bpm <= 0)
            {
                errorText.Text = "BPMは正の数で入力してくださいませ。";
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
