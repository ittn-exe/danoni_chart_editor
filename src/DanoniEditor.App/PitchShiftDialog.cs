using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>再生速度を「ピッチ(半音移動量)」で指定するための入力ダイアログ(2026-07-26要望対応)。
/// 移動量(半音単位、0=現状、+1=キー+1、-1=キー-1)からspeedRatio = 2^(移動量/12)を計算する。
/// 範囲はオクターブ-2〜+1(=半音-24〜+12)。</summary>
internal static class PitchShiftDialog
{
    public const int MinSemitones = -24;
    public const int MaxSemitones = 12;

    /// <summary>半音移動量からspeedRatioを計算する</summary>
    public static double RatioFromSemitones(int semitones) => Math.Pow(2, semitones / 12.0);

    private readonly record struct SemitoneItem(int Semitones, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>ピッチ指定ダイアログを表示する。OKで確定した半音移動量を返す(キャンセル時null)。</summary>
    public static int? Ask(Window owner, int currentSemitones)
    {
        var win = new Window
        {
            Title = "ピッチで指定",
            Owner = owner,
            Width = 340,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "移動量(半音、0=現状、+1=キー+1、-1=キー-1、範囲: -2〜+1オクターブ)",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var items = Enumerable.Range(MinSemitones, MaxSemitones - MinSemitones + 1)
            .Select(s => new SemitoneItem(s, $"{(s > 0 ? "+" : "")}{s}半音"))
            .ToList();

        var combo = new ComboBox { ItemsSource = items, Margin = new Thickness(0, 0, 0, 12) };
        combo.SelectedItem = items.FirstOrDefault(it => it.Semitones == Math.Clamp(currentSemitones, MinSemitones, MaxSemitones));
        if (combo.SelectedItem is null) combo.SelectedIndex = items.FindIndex(it => it.Semitones == 0);
        panel.Children.Add(combo);

        var ratioText = new TextBlock { Margin = new Thickness(0, 0, 0, 16) };
        void UpdateRatioText()
        {
            if (combo.SelectedItem is SemitoneItem item)
            {
                double ratio = RatioFromSemitones(item.Semitones);
                ratioText.Text = $"再生速度: x{ratio:0.0000}";
            }
        }
        combo.SelectionChanged += (_, _) => UpdateRatioText();
        UpdateRatioText();
        panel.Children.Add(ratioText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        int? result = null;
        ok.Click += (_, _) =>
        {
            if (combo.SelectedItem is SemitoneItem item) result = item.Semitones;
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
