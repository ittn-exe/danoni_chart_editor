using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// 色コード使用履歴(仕様書6.4.2/14章 colorHistory、2026-07-23実装)。
/// 単純な#RRGGBB形式のみを履歴対象とする(グラデーション等の生文字列は対象外、ユーザー確定仕様)。
/// 色入力欄(②色設定タブ・表示設定ダイアログ・環境設定・ncolor_data色編集タブ)から共通で使う。
/// </summary>
internal static partial class ColorHistoryPicker
{
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex SimpleHexRegex();

    /// <summary>単純な#RRGGBBとしてパース可能な値のみ履歴の先頭に記録する(重複は先頭へ移動、
    /// ColorHistoryLimit件で切り詰め)。呼び出し側で保存(AppSettings.Save)まで行うこと。</summary>
    public static void Record(AppSettings settings, string value)
    {
        var trimmed = value.Trim();
        if (!SimpleHexRegex().IsMatch(trimmed)) return;

        settings.ColorHistory.RemoveAll(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        settings.ColorHistory.Insert(0, trimmed);
        int limit = Math.Max(1, settings.ColorHistoryLimit);
        if (settings.ColorHistory.Count > limit)
            settings.ColorHistory.RemoveRange(limit, settings.ColorHistory.Count - limit);
    }

    /// <summary>anchorの下に履歴スウォッチのポップアップを表示する。スウォッチクリックでonPickを呼び、
    /// ポップアップを閉じる。履歴が空の場合は「履歴なし」とだけ表示する。</summary>
    public static void Show(AppSettings settings, FrameworkElement anchor, Action<string> onPick)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        var wrap = new WrapPanel { Width = 168, Margin = new Thickness(6) };
        if (settings.ColorHistory.Count == 0)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = "履歴なし", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(2),
            });
        }
        else
        {
            foreach (var hex in settings.ColorHistory)
            {
                var swatch = new Button
                {
                    Width = 22, Height = 22, Margin = new Thickness(2),
                    Background = SafeBrush(hex), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                    ToolTip = hex,
                };
                swatch.Click += (_, _) => { onPick(hex); popup.IsOpen = false; };
                wrap.Children.Add(swatch);
            }
        }

        popup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Child = wrap,
        };
        popup.IsOpen = true;
    }

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }
}
