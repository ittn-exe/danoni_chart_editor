using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// ノート強調グリッドの太さ・色を編集する最小モーダルダイアログ(仕様書14章アプリ設定、2026-07-16b追加)。
/// SimplePromptと同じ薄い自前ダイアログ方式(依存追加なし)。色は6桁カラーコードの直接入力のみ
/// (仕様書6.4.2のカラーコード入力方式に準拠、パレットUIは今回のスコープ外)。
/// </summary>
internal static class DisplaySettingsDialog
{
    /// <summary>変更後の設定を返す。キャンセル時はnull。</summary>
    public static AppSettings? Ask(Window owner, AppSettings current)
    {
        var win = new Window
        {
            Title = "表示設定",
            Owner = owner,
            Width = 360,
            Height = 470,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "ノート画像OFF時の強調グリッド表示",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock { Text = "太さ(px):", Margin = new Thickness(0, 0, 0, 2) });
        var widthBox = new TextBox
        {
            Text = current.HighlightLineWidth.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(widthBox);

        panel.Children.Add(new TextBlock { Text = "色(6桁カラーコード、例: #FFD400):", Margin = new Thickness(0, 0, 0, 2) });
        var colorBox = new TextBox { Text = current.HighlightLineColorHex, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(colorBox);

        var preview = new Border
        {
            Height = 18,
            Margin = new Thickness(0, 0, 0, 12),
            Background = SafeBrush(current.HighlightLineColorHex),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
        };
        panel.Children.Add(preview);
        colorBox.TextChanged += (_, _) => preview.Background = SafeBrush(colorBox.Text);

        // --- 再生開始フレームライン(2026-07-17f、未解決事項§2-2「色・太さは環境設定で変更可能」) ---
        panel.Children.Add(new TextBlock
        {
            Text = "再生開始フレームのライン",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "太さ(px):", Margin = new Thickness(0, 0, 0, 2) });
        var startWidthBox = new TextBox
        {
            Text = current.PlaybackStartLineWidth.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(startWidthBox);
        panel.Children.Add(new TextBlock { Text = "色(6桁カラーコード、例: #4FC3F7):", Margin = new Thickness(0, 0, 0, 2) });
        var startColorBox = new TextBox { Text = current.PlaybackStartLineColorHex, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(startColorBox);
        var startPreview = new Border
        {
            Height = 18,
            Margin = new Thickness(0, 0, 0, 12),
            Background = SafeBrush(current.PlaybackStartLineColorHex),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
        };
        panel.Children.Add(startPreview);
        startColorBox.TextChanged += (_, _) => startPreview.Background = SafeBrush(startColorBox.Text);

        // --- 目視テストの追従スクロール方式(2026-07-17f、未解決事項§2-1) ---
        panel.Children.Add(new TextBlock
        {
            Text = "目視テスト中の再生位置ライン追従",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 8),
        });
        var followCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        followCombo.Items.Add("ページ送り(画面外に出たら次の1画面へ)");
        followCombo.Items.Add("スムーズスクロール(ライン位置固定で譜面が流れる)");
        followCombo.SelectedIndex = current.VisualTestFollowMode == "smooth" ? 1 : 0;
        panel.Children.Add(followCombo);

        var errorText = new TextBlock { Foreground = Brushes.Red, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        AppSettings? result = null;
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(widthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) || width <= 0)
            {
                errorText.Text = "太さは正の数値で入力してください";
                return;
            }
            try { ColorConverter.ConvertFromString(colorBox.Text); }
            catch
            {
                errorText.Text = "色は #RRGGBB 形式のカラーコードで入力してください";
                return;
            }
            if (!double.TryParse(startWidthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var startWidth) || startWidth <= 0)
            {
                errorText.Text = "再生開始ラインの太さは正の数値で入力してください";
                return;
            }
            try { ColorConverter.ConvertFromString(startColorBox.Text); }
            catch
            {
                errorText.Text = "再生開始ラインの色は #RRGGBB 形式のカラーコードで入力してください";
                return;
            }
            result = new AppSettings
            {
                ShowNoteImages = current.ShowNoteImages,
                ShowHighlightGrid = current.ShowHighlightGrid, // 2026-07-17f: 従来ここが漏れており、ダイアログOKのたびに強調表示ONがデフォルト(false)へ戻る潜在バグがあった
                HighlightLineWidth = width,
                HighlightLineColorHex = colorBox.Text,
                PlaybackStartLineWidth = startWidth,
                PlaybackStartLineColorHex = startColorBox.Text,
                VisualTestFollowMode = followCombo.SelectedIndex == 1 ? "smooth" : "page",
            };
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }
}
