using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// 履歴・お気に入り・HSV視覚選択・RGB/HEX入力を統合したカラーピッカー(2026-08-08新設、
/// 旧ColorHistoryPicker.Show(履歴のみのポップアップ)の後継)。色欄の「履歴」ボタンをすべて
/// このピッカーの呼び出しへ置き換える(進捗まとめ5-2、お気に入りの色機能)。
///
/// 通常のColorHistoryPicker/CssColorPickerと同じくPopup(AllowsTransparency)で実装するが、
/// 以下2点をユーザー確定仕様として追加する。
/// - ドラッグによる自由移動: ヘッダーのタイトル部分をドラッグするとポップアップ位置を動かせる
///   (Popup自体はドラッグに対応しないため、HorizontalOffset/VerticalOffsetを手動で追従させる)。
/// - ピン留め: 「固定」ボタンでStaysOpenを切り替える。ピン留め中はフォーカスが外れても閉じない
///   (複数の色欄を見比べながら調整する用途を想定)。閉じるには×ボタンを押すか、固定を解除してから
///   ポップアップ外をクリックする。
///
/// お気に入りへの登録はこのピッカーでは行わない(FavoriteColorPicker.Register、色欄の「☆登録」
/// ボタン側で完結する)。お気に入りの削除も同様にこのピッカーでは行わず、環境設定「カラーピッカー」
/// カテゴリの一覧から行う(ユーザー確定仕様、2026-08-08)。
///
/// 履歴・お気に入りの一覧はポップアップを開いた時点のスナップショットであり、開いている間に
/// (別の色欄の☆登録操作等で)AppSettings側が変化しても追従しない。ピン留めで長時間開いたままに
/// できる分の既知の簡略化だが、実運用上は都度開き直せば十分と判断した。
/// </summary>
internal static partial class ColorPickerPopup
{
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex SimpleHexRegex();

    private const double SvSize = 160;
    private const double HueWidth = 18;

    /// <summary>anchorの下にピッカーを表示する。initialHexが単純な#RRGGBBとして解釈できない場合
    /// (未設定・グラデーション等の生文字列)は白から開始する。以後、視覚選択・RGB/HEX入力・
    /// 履歴/お気に入りスウォッチのいずれを操作してもonPickを都度呼ぶ(即時反映、ポップアップは
    /// 閉じない)。</summary>
    public static void Show(AppSettings settings, FrameworkElement anchor, string initialHex, Action<string> onPick)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // --- ヘッダー(タイトル=ドラッグハンドル、固定トグル、閉じるボタン) ---
        var titleText = new TextBlock
        {
            Text = "カラーピッカー", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
        };
        var pinBtn = new Button { Content = "固定", Width = 44, Margin = new Thickness(4, 0, 0, 0) };
        var closeBtn = new Button { Content = "×", Width = 22, Margin = new Thickness(4, 0, 0, 0) };
        var header = new DockPanel { Margin = new Thickness(6, 6, 6, 4) };
        DockPanel.SetDock(closeBtn, Dock.Right);
        DockPanel.SetDock(pinBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(pinBtn);
        header.Children.Add(titleText);

        bool pinned = false;
        pinBtn.Click += (_, _) =>
        {
            pinned = !pinned;
            pinBtn.Content = pinned ? "固定中" : "固定";
            popup.StaysOpen = pinned;
        };
        closeBtn.Click += (_, _) => popup.IsOpen = false;

        // ドラッグ移動: Popup自体はドラッグに対応しないため、オーナーウィンドウ基準のマウス座標の
        // 差分をHorizontalOffset/VerticalOffsetへ加算する(ウィンドウ自体は動かないため、差分計算の
        // 基準として安定する)。
        var ownerWindow = Window.GetWindow(anchor) ?? Application.Current.MainWindow;
        Point? dragStart = null;
        double dragStartH = 0, dragStartV = 0;
        titleText.MouseLeftButtonDown += (_, e) =>
        {
            if (ownerWindow is null) return;
            dragStart = e.GetPosition(ownerWindow);
            dragStartH = popup.HorizontalOffset;
            dragStartV = popup.VerticalOffset;
            titleText.CaptureMouse();
            e.Handled = true;
        };
        titleText.MouseMove += (_, e) =>
        {
            if (dragStart is not { } start || ownerWindow is null) return;
            var cur = e.GetPosition(ownerWindow);
            popup.HorizontalOffset = dragStartH + (cur.X - start.X);
            popup.VerticalOffset = dragStartV + (cur.Y - start.Y);
        };
        titleText.MouseLeftButtonUp += (_, _) =>
        {
            dragStart = null;
            titleText.ReleaseMouseCapture();
        };

        // --- HSV視覚選択(彩度×明度の正方形+色相の縦バー) ---
        var svCanvas = new Canvas { Width = SvSize, Height = SvSize, ClipToBounds = true };
        var hueBase = new Rectangle { Width = SvSize, Height = SvSize };
        var satOverlay = new Rectangle
        {
            Width = SvSize, Height = SvSize,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var valOverlay = new Rectangle
        {
            Width = SvSize, Height = SvSize,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        };
        var svCursor = new Ellipse
        {
            Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false,
        };
        svCanvas.Children.Add(hueBase);
        svCanvas.Children.Add(satOverlay);
        svCanvas.Children.Add(valOverlay);
        svCanvas.Children.Add(svCursor);

        var hueCanvas = new Canvas { Width = HueWidth, Height = SvSize, Margin = new Thickness(6, 0, 0, 0) };
        var hueGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        (double Offset, Color Color)[] hueStops =
        [
            (0.0 / 6, Colors.Red), (1.0 / 6, Colors.Yellow), (2.0 / 6, Colors.Lime),
            (3.0 / 6, Colors.Cyan), (4.0 / 6, Colors.Blue), (5.0 / 6, Colors.Magenta), (1.0, Colors.Red),
        ];
        foreach (var (offset, color) in hueStops) hueGradient.GradientStops.Add(new GradientStop(color, offset));
        var hueBar = new Rectangle { Width = HueWidth, Height = SvSize, Fill = hueGradient };
        var hueCursor = new Rectangle { Width = HueWidth, Height = 3, Fill = Brushes.Black, IsHitTestVisible = false };
        hueCanvas.Children.Add(hueBar);
        hueCanvas.Children.Add(hueCursor);

        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal };
        pickerRow.Children.Add(svCanvas);
        pickerRow.Children.Add(hueCanvas);

        // --- RGB/HEX入力 ---
        var rBox = new TextBox { Width = 40 };
        var gBox = new TextBox { Width = 40, Margin = new Thickness(4, 0, 0, 0) };
        var bBox = new TextBox { Width = 40, Margin = new Thickness(4, 0, 0, 0) };
        var rgbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        rgbRow.Children.Add(new TextBlock { Text = "R", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) });
        rgbRow.Children.Add(rBox);
        rgbRow.Children.Add(new TextBlock { Text = "G", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
        rgbRow.Children.Add(gBox);
        rgbRow.Children.Add(new TextBlock { Text = "B", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
        rgbRow.Children.Add(bBox);

        var hexBox = new TextBox { Width = 90 };
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "HEX", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        hexRow.Children.Add(hexBox);

        var preview = new Border { Height = 22, Margin = new Thickness(0, 8, 0, 0), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) };

        // --- 状態(HSV)とビュー同期 ---
        double h = 0, s = 0, v = 0;
        bool updating = false;

        void RefreshVisuals(bool notify)
        {
            var (r, g, b) = HsvToRgb(h, s, v);
            var hex = $"#{r:x2}{g:x2}{b:x2}";

            updating = true;
            hueBase.Fill = new SolidColorBrush(HsvToColor(h, 1, 1));
            Canvas.SetLeft(svCursor, s * SvSize - 5);
            Canvas.SetTop(svCursor, (1 - v) * SvSize - 5);
            Canvas.SetTop(hueCursor, h / 360.0 * SvSize - 1.5);
            rBox.Text = r.ToString(CultureInfo.InvariantCulture);
            gBox.Text = g.ToString(CultureInfo.InvariantCulture);
            bBox.Text = b.ToString(CultureInfo.InvariantCulture);
            hexBox.Text = hex;
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            updating = false;

            if (notify) onPick(hex);
        }

        void ApplyRgb(byte r, byte g, byte b)
        {
            (h, s, v) = RgbToHsv(r, g, b);
            RefreshVisuals(notify: true);
        }

        void ApplyHex(string hex)
        {
            var trimmed = hex.Trim();
            if (!SimpleHexRegex().IsMatch(trimmed)) return;
            var c = (Color)ColorConverter.ConvertFromString(trimmed)!;
            ApplyRgb(c.R, c.G, c.B);
        }

        svCanvas.MouseLeftButtonDown += (_, e) => { svCanvas.CaptureMouse(); UpdateFromSvPoint(e.GetPosition(svCanvas)); };
        svCanvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && svCanvas.IsMouseCaptured) UpdateFromSvPoint(e.GetPosition(svCanvas)); };
        svCanvas.MouseLeftButtonUp += (_, _) => svCanvas.ReleaseMouseCapture();
        void UpdateFromSvPoint(Point p)
        {
            s = Math.Clamp(p.X / SvSize, 0, 1);
            v = Math.Clamp(1 - p.Y / SvSize, 0, 1);
            RefreshVisuals(notify: true);
        }

        hueCanvas.MouseLeftButtonDown += (_, e) => { hueCanvas.CaptureMouse(); UpdateFromHuePoint(e.GetPosition(hueCanvas)); };
        hueCanvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && hueCanvas.IsMouseCaptured) UpdateFromHuePoint(e.GetPosition(hueCanvas)); };
        hueCanvas.MouseLeftButtonUp += (_, _) => hueCanvas.ReleaseMouseCapture();
        void UpdateFromHuePoint(Point p)
        {
            h = Math.Clamp(p.Y / SvSize, 0, 1) * 360;
            RefreshVisuals(notify: true);
        }

        void TryApplyRgbFromBoxes()
        {
            if (!TryByte(rBox.Text, out var r) || !TryByte(gBox.Text, out var g) || !TryByte(bBox.Text, out var b)) return;
            ApplyRgb(r, g, b);
        }
        rBox.TextChanged += (_, _) => { if (!updating) TryApplyRgbFromBoxes(); };
        gBox.TextChanged += (_, _) => { if (!updating) TryApplyRgbFromBoxes(); };
        bBox.TextChanged += (_, _) => { if (!updating) TryApplyRgbFromBoxes(); };
        hexBox.TextChanged += (_, _) => { if (!updating) ApplyHex(hexBox.Text); };

        // --- 履歴・お気に入り(スウォッチクリックでピッカーの状態を更新、ポップアップは閉じない) ---
        WrapPanel BuildSwatchWrap(IEnumerable<string> hexList)
        {
            var wrap = new WrapPanel { Width = SvSize + HueWidth + 6 };
            var list = hexList.ToList();
            if (list.Count == 0)
            {
                wrap.Children.Add(new TextBlock { Text = "なし", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(2) });
            }
            else
            {
                foreach (var hex in list)
                {
                    var swatch = new Button
                    {
                        Width = 20, Height = 20, Margin = new Thickness(2),
                        Background = SafeBrush(hex), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                        ToolTip = hex,
                    };
                    swatch.Click += (_, _) => ApplyHex(hex);
                    wrap.Children.Add(swatch);
                }
            }
            return wrap;
        }

        var body = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        body.Children.Add(pickerRow);
        body.Children.Add(rgbRow);
        body.Children.Add(hexRow);
        body.Children.Add(preview);
        body.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(new TextBlock { Text = "履歴", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        body.Children.Add(BuildSwatchWrap(settings.ColorHistory));
        body.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(new TextBlock { Text = "お気に入り", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        body.Children.Add(BuildSwatchWrap(settings.FavoriteColors));

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 440 });

        popup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Child = root,
        };

        // 初期値の反映(notify:falseでonPickは呼ばない。initialHexが単純な#RRGGBB以外
        // (未設定・グラデーション等の生文字列)の場合、開いただけで元の値を上書きしてしまうのを防ぐ)。
        var initial = SimpleHexRegex().IsMatch(initialHex.Trim())
            ? (Color)ColorConverter.ConvertFromString(initialHex.Trim())!
            : Colors.White;
        (h, s, v) = RgbToHsv(initial.R, initial.G, initial.B);
        RefreshVisuals(notify: false);

        popup.IsOpen = true;
    }

    private static bool TryByte(string text, out byte value) => byte.TryParse(text.Trim(), out value);

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        return Color.FromRgb(r, g, b);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        var (rf, gf, bf) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (
            (byte)Math.Clamp(Math.Round((rf + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((gf + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((bf + m) * 255), 0, 255));
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        double h;
        if (delta < 1e-9) h = 0;
        else if (max == rf) h = 60 * ((gf - bf) / delta % 6);
        else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
        else h = 60 * ((rf - gf) / delta + 4);
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }
}
