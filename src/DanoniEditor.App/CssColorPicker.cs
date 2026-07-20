using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DanoniEditor.Core.Export;

namespace DanoniEditor.App;

/// <summary>
/// CSS標準色名(147色、CssColorNames)を視覚的に選べる専用カラーピッカー(2026-07-24、
/// 色編集モードの「透明度を使用する」色名指定用)。ColorHistoryPickerと同じPopup+WrapPanelの
/// スタイルに合わせる。
/// </summary>
internal static class CssColorPicker
{
    /// <summary>anchorの下に147色のスウォッチのポップアップを表示する。スウォッチクリックで
    /// 色名(onPick)を呼び、ポップアップを閉じる。</summary>
    public static void Show(FrameworkElement anchor, Action<string> onPick)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        var wrap = new WrapPanel { Width = 280, Margin = new Thickness(6) };
        foreach (var (name, hex) in CssColorNames.All)
        {
            var swatch = new Button
            {
                Width = 20, Height = 20, Margin = new Thickness(2),
                Background = SafeBrush(hex), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                ToolTip = name,
            };
            swatch.Click += (_, _) => { onPick(name); popup.IsOpen = false; };
            wrap.Children.Add(swatch);
        }

        popup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            MaxHeight = 320,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = wrap },
        };
        popup.IsOpen = true;
    }

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }
}
