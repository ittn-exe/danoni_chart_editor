using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// 環境設定ウィンドウ(2026-07-19、仕様書14章)。メニューバー「設定 > 環境設定」から開く。
/// 左にカテゴリ一覧、右に設定項目のカテゴリ式レイアウト。従来のDisplaySettingsDialog(表示設定のみの
/// 最小ダイアログ)を置き換え、AppSettingsの全項目をここへ集約する。
/// 今後の設定項目(headerDefaults/colorHistory/macros/undoHistorySize等、仕様書14章のTBD)も
/// カテゴリを足すだけで拡張できる構造にしてある。
/// </summary>
internal sealed class PreferencesWindow : Window
{
    private readonly AppSettings _work; // 作業コピー(OKで確定)

    /// <summary>OK確定後の設定。キャンセル時はnull</summary>
    public AppSettings? Result { get; private set; }

    // --- 表示 ---
    private readonly CheckBox _showImages = new() { Content = "ノート画像を表示する" };
    private readonly CheckBox _showGrid = new() { Content = "強調グリッド(横棒)を表示する" };
    private readonly TextBox _gridWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _gridColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _gridPreview = MakePreview();
    private readonly TextBox _startLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _startLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _startLinePreview = MakePreview();

    // --- 目視テスト ---
    private readonly ComboBox _followMode = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };

    // --- プレイテスト ---
    private readonly CheckBox _ptReverse = new() { Content = "Reverse(スクロール反転)" };
    private readonly ComboBox _ptHiSpeed = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _ptOffset = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _ptScale = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- マーカー表示(表示カテゴリ内) ---
    private readonly RadioButton _markerFull = new() { Content = "全文表示", GroupName = "marker" };
    private readonly RadioButton _markerHead = new() { Content = "先頭数文字のみ", GroupName = "marker" };
    private readonly TextBox _markerHeadChars = new() { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 新規プロジェクト(headerDefaults) ---
    private readonly TextBox _defStartFrame = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defBlankFrame = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defTuning = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defFrzAttempt = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _defBpm = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 編集・保存 ---
    private readonly TextBox _undoSize = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _confirmUnsaved = new() { Content = "未保存の変更がある時、終了前に確認する" };
    private readonly TextBox _colorHistLimit = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public PreferencesWindow(AppSettings current, int initialCategory = 0)
    {
        _work = current.Clone();

        Title = "環境設定";
        Width = 560;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        // --- カテゴリ一覧+パネル切替 ---
        var categories = new ListBox { Margin = new Thickness(8), Width = 120 };
        categories.Items.Add("表示");
        categories.Items.Add("目視テスト");
        categories.Items.Add("プレイテスト");
        categories.Items.Add("新規プロジェクト");
        categories.Items.Add("編集・保存");

        var panels = new[] { BuildDisplayPanel(), BuildVisualTestPanel(), BuildPlaytestPanel(), BuildNewProjectPanel(), BuildEditSavePanel() };
        var content = new ContentControl { Margin = new Thickness(0, 8, 8, 0) };
        categories.SelectionChanged += (_, _) =>
        {
            if (categories.SelectedIndex >= 0) content.Content = panels[categories.SelectedIndex];
        };
        categories.SelectedIndex = Math.Clamp(initialCategory, 0, panels.Length - 1);

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (TryCommit()) { DialogResult = true; } };
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var errDock = new Border { Child = _error, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(errDock, Dock.Bottom);
        root.Children.Add(errDock);
        DockPanel.SetDock(categories, Dock.Left);
        root.Children.Add(categories);
        root.Children.Add(content);
        Content = root;

        LoadFrom(_work);
    }

    private static Border MakePreview() => new()
    {
        Height = 16,
        Width = 160,
        HorizontalAlignment = HorizontalAlignment.Left,
        BorderBrush = Brushes.Black,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 2, 0, 8),
    };

    private static TextBlock Label(string text, bool section = false) => new()
    {
        Text = text,
        FontWeight = section ? FontWeights.Bold : FontWeights.Normal,
        Margin = section ? new Thickness(0, 8, 0, 6) : new Thickness(0, 4, 0, 2),
    };

    private UIElement BuildDisplayPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("ノート表示", section: true));
        _showImages.Margin = new Thickness(0, 0, 0, 4);
        _showGrid.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_showImages);
        p.Children.Add(_showGrid);
        p.Children.Add(Label("強調グリッドの太さ(px):"));
        p.Children.Add(_gridWidth);
        p.Children.Add(Label("強調グリッドの色(#RRGGBB):"));
        p.Children.Add(_gridColor);
        p.Children.Add(_gridPreview);
        _gridColor.TextChanged += (_, _) => _gridPreview.Background = SafeBrush(_gridColor.Text);

        p.Children.Add(Label("再生開始フレームライン", section: true));
        p.Children.Add(Label("太さ(px):"));
        p.Children.Add(_startLineWidth);
        p.Children.Add(Label("色(#RRGGBB):"));
        p.Children.Add(_startLineColor);
        p.Children.Add(_startLinePreview);
        _startLineColor.TextChanged += (_, _) => _startLinePreview.Background = SafeBrush(_startLineColor.Text);

        p.Children.Add(Label("マーカーのコメント表示(仕様書7.4)", section: true));
        _markerFull.Margin = new Thickness(0, 0, 0, 2);
        _markerHead.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_markerFull);
        p.Children.Add(_markerHead);
        p.Children.Add(Label("先頭表示の文字数:"));
        p.Children.Add(_markerHeadChars);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildVisualTestPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("再生位置ラインの追従方式", section: true));
        _followMode.Items.Add("ページ送り(画面外に出たら次の1画面へ)");
        _followMode.Items.Add("スムーズスクロール(ライン位置固定で譜面が流れる)");
        p.Children.Add(_followMode);
        p.Children.Add(new TextBlock
        {
            Text = "目視テスト(Space)中、再生位置ラインが画面外へ出た時の譜面ビューの動き方ですわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return p;
    }

    private UIElement BuildPlaytestPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("プレイテスト(Ctrl+P)", section: true));
        _ptReverse.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_ptReverse);
        p.Children.Add(Label("ハイスピード(x0.25〜x10、0.25刻み):"));
        foreach (var v in Enumerable.Range(1, 40).Select(i => i * 0.25)) _ptHiSpeed.Items.Add(v);
        p.Children.Add(_ptHiSpeed);
        p.Children.Add(Label("タイミング調整オフセット(frame、正=譜面を後ろへ):"));
        p.Children.Add(_ptOffset);
        p.Children.Add(Label("ウィンドウサイズ倍率:"));
        foreach (var v in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 }) _ptScale.Items.Add(v);
        p.Children.Add(_ptScale);
        p.Children.Add(new TextBlock
        {
            Text = "これらは上部パネルの「プレーテスト」欄と同じ設定ですわ(どちらで変えても保存されますの)。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return p;
    }

    private UIElement BuildNewProjectPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("新規プロジェクトのデフォルト値(headerDefaults)", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "新規プロジェクト作成時に適用される初期値ですわ(仕様書6.4.1)。musicURLは対象外(毎回入力)ですの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        p.Children.Add(Label("startFrame:"));
        p.Children.Add(_defStartFrame);
        p.Children.Add(Label("blankFrame(個人運用では200等):"));
        p.Children.Add(_defBlankFrame);
        p.Children.Add(Label("tuning(製作者名義):"));
        p.Children.Add(_defTuning);
        p.Children.Add(Label("frzAttempt:"));
        p.Children.Add(_defFrzAttempt);
        p.Children.Add(Label("BPM初期値(dos.txtインポートの推定不能時にも使用):"));
        p.Children.Add(_defBpm);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildEditSavePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("編集", section: true));
        p.Children.Add(Label("Undo履歴の保持件数(仕様書14章、デフォルト30):"));
        p.Children.Add(_undoSize);
        p.Children.Add(Label("保存", section: true));
        _confirmUnsaved.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_confirmUnsaved);
        p.Children.Add(Label("色履歴", section: true));
        p.Children.Add(Label("色コード使用履歴の上限件数(デフォルト24):"));
        p.Children.Add(_colorHistLimit);
        p.Children.Add(new TextBlock
        {
            Text = "※履歴の記録・呼び出しUI(カラーピッカー連携)は今後の実装ですわ。上限だけ先に設定できますの。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return p;
    }

    private void LoadFrom(AppSettings s)
    {
        _showImages.IsChecked = s.ShowNoteImages;
        _showGrid.IsChecked = s.ShowHighlightGrid;
        _gridWidth.Text = s.HighlightLineWidth.ToString(CultureInfo.InvariantCulture);
        _gridColor.Text = s.HighlightLineColorHex;
        _gridPreview.Background = SafeBrush(s.HighlightLineColorHex);
        _startLineWidth.Text = s.PlaybackStartLineWidth.ToString(CultureInfo.InvariantCulture);
        _startLineColor.Text = s.PlaybackStartLineColorHex;
        _startLinePreview.Background = SafeBrush(s.PlaybackStartLineColorHex);
        _followMode.SelectedIndex = s.VisualTestFollowMode == "smooth" ? 1 : 0;
        _ptReverse.IsChecked = s.PlaytestReverse;
        _ptHiSpeed.SelectedItem = _ptHiSpeed.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestHiSpeed)).First();
        _ptOffset.Text = s.PlaytestOffsetFrames.ToString(CultureInfo.InvariantCulture);
        _ptScale.SelectedItem = _ptScale.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestWindowScale)).First();
        _markerFull.IsChecked = s.MarkerCommentFull;
        _markerHead.IsChecked = !s.MarkerCommentFull;
        _markerHeadChars.Text = s.MarkerCommentHeadChars.ToString(CultureInfo.InvariantCulture);
        _defStartFrame.Text = s.DefaultStartFrame.ToString(CultureInfo.InvariantCulture);
        _defBlankFrame.Text = s.DefaultBlankFrame.ToString(CultureInfo.InvariantCulture);
        _defTuning.Text = s.DefaultTuning;
        _defFrzAttempt.Text = s.DefaultFrzAttempt.ToString(CultureInfo.InvariantCulture);
        _defBpm.Text = s.DefaultBpm.ToString(CultureInfo.InvariantCulture);
        _undoSize.Text = s.UndoHistorySize.ToString(CultureInfo.InvariantCulture);
        _confirmUnsaved.IsChecked = s.ConfirmUnsavedOnClose;
        _colorHistLimit.Text = s.ColorHistoryLimit.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryCommit()
    {
        _error.Text = "";
        if (_showImages.IsChecked != true && _showGrid.IsChecked != true)
        { _error.Text = "ノート画像と強調グリッドの両方をOFFにはできませんの(どちらかはONにしてくださいまし)"; return false; }
        if (!TryPositive(_gridWidth.Text, out var gw))
        { _error.Text = "強調グリッドの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_gridColor.Text))
        { _error.Text = "強調グリッドの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_startLineWidth.Text, out var sw))
        { _error.Text = "再生開始ラインの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_startLineColor.Text))
        { _error.Text = "再生開始ラインの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!double.TryParse(_ptOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ofs))
        { _error.Text = "調整オフセットは数値で入力してくださいまし"; return false; }
        if (!int.TryParse(_markerHeadChars.Text, out var headChars) || headChars < 1)
        { _error.Text = "マーカー先頭表示の文字数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defStartFrame.Text, out var defSf) || defSf < 0)
        { _error.Text = "startFrameは0以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defBlankFrame.Text, out var defBf) || defBf < 0)
        { _error.Text = "blankFrameは0以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_defFrzAttempt.Text, out var defFa) || defFa < 0)
        { _error.Text = "frzAttemptは0以上の整数で入力してくださいまし"; return false; }
        if (!TryPositive(_defBpm.Text, out var defBpm))
        { _error.Text = "BPM初期値は正の数値で入力してくださいまし"; return false; }
        if (!int.TryParse(_undoSize.Text, out var undoSize) || undoSize < 1)
        { _error.Text = "Undo履歴件数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_colorHistLimit.Text, out var colorLimit) || colorLimit < 1)
        { _error.Text = "色履歴の上限件数は1以上の整数で入力してくださいまし"; return false; }

        _work.ShowNoteImages = _showImages.IsChecked == true;
        _work.ShowHighlightGrid = _showGrid.IsChecked == true;
        _work.HighlightLineWidth = gw;
        _work.HighlightLineColorHex = _gridColor.Text;
        _work.PlaybackStartLineWidth = sw;
        _work.PlaybackStartLineColorHex = _startLineColor.Text;
        _work.VisualTestFollowMode = _followMode.SelectedIndex == 1 ? "smooth" : "page";
        _work.PlaytestReverse = _ptReverse.IsChecked == true;
        if (_ptHiSpeed.SelectedItem is double hs) _work.PlaytestHiSpeed = hs;
        _work.PlaytestOffsetFrames = ofs;
        if (_ptScale.SelectedItem is double sc) _work.PlaytestWindowScale = sc;
        _work.MarkerCommentFull = _markerFull.IsChecked == true;
        _work.MarkerCommentHeadChars = headChars;
        _work.DefaultStartFrame = defSf;
        _work.DefaultBlankFrame = defBf;
        _work.DefaultTuning = _defTuning.Text;
        _work.DefaultFrzAttempt = defFa;
        _work.DefaultBpm = defBpm;
        _work.UndoHistorySize = undoSize;
        _work.ConfirmUnsavedOnClose = _confirmUnsaved.IsChecked == true;
        _work.ColorHistoryLimit = colorLimit;
        Result = _work;
        return true;
    }

    private static bool TryPositive(string text, out double v) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;

    private static bool TryColor(string text)
    {
        try { ColorConverter.ConvertFromString(text); return true; }
        catch { return false; }
    }

    private static Brush SafeBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { return Brushes.Magenta; }
    }
}
