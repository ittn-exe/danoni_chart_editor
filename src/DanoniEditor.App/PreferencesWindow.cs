using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;
using Microsoft.Win32;

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
    private readonly CheckBox _excludeFreezeEndHighlight = new() { Content = "フリーズアロー終点を強調グリッドの対象から除外する" };
    private readonly TextBox _gridWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _gridColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _gridPreview = MakePreview();
    private readonly TextBox _startLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _startLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _startLinePreview = MakePreview();
    // --- カーソルライン(マウスモード、2026-07-25) ---
    private readonly TextBox _cursorLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorLinePreview = MakePreview();
    private readonly TextBox _cursorHighlightWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorHighlightColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorHighlightPreview = MakePreview();

    // --- テスト再生 > 全般: ノート音として鳴らす./sounds内の音声ファイル選択 ---
    private readonly ComboBox _noteSoundFile = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 目視テスト ---
    private readonly ComboBox _followMode = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };

    // --- プレイテスト ---
    private readonly CheckBox _ptReverse = new() { Content = "Reverse(スクロール反転)" };
    private readonly ComboBox _ptHiSpeed = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _ptOffset = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _ptScale = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _ptQuitDelete = new() { Content = "Delete" };
    private readonly CheckBox _ptQuitEscape = new() { Content = "Escape" };
    // --- プレイテスト起動時ウェイト(2026-07-26d要望対応、ms単位) ---
    private readonly TextBox _ptStartupWaitMs = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    // --- プレイテスト: キー種ごとのReverse既定値(2026-07-26要望対応) ---
    private readonly Dictionary<string, CheckBox> _ptReverseByKeyType = [];

    // --- プレイテスト: キー種ごとの採用キーパターン(2026-07-26e要望対応)。
    // 追加パターンを持つキー種のみ選択欄を出す。値はコンボの表示文字列("パターン0(既定)"等)ではなく
    // インデックスで管理したいため、ComboBoxのTagにキー種IDを持たせてSelectedIndexをそのまま使う。 ---
    private readonly Dictionary<string, ComboBox> _ptPatternByKeyType = [];

    // --- プレイテスト: ウィンドウ幅(2026-07-26要望対応、2026-07-26 自動モード追加) ---
    private readonly RadioButton _ptWidthAutoMode = new() { Content = "自動(特に指定せず、プレイテストする譜面のキー種に合わせて自動的に切り替える)", GroupName = "ptWidthMode", Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _ptWidthPxMode = new() { Content = "ウィンドウ幅を直接入力", GroupName = "ptWidthMode", Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBox _ptWidthPx = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20, 0, 0, 8) };
    private readonly RadioButton _ptWidthKeyTypeMode = new() { Content = "キー種から選択(常に指定したキー種の幅を使う)", GroupName = "ptWidthMode", Margin = new Thickness(0, 0, 0, 2) };
    private readonly StackPanel _ptWidthKeyTypeList = new() { Margin = new Thickness(20, 0, 0, 0) };
    /// <summary>幅グループごとのラジオボタン。Members=その幅を共有するキー種ID一式(設定値の読込照合用)、
    /// RepresentativeKeyTypeId=保存時にAppSettings.PlaytestWindowWidthKeyTypeへ書き込む代表キー種
    /// (同じ幅を生む値ならどれでも計算結果は同じなので、ラベルの筆頭=最少キー数のものを使う)。</summary>
    private readonly List<(RadioButton Radio, string RepresentativeKeyTypeId, HashSet<string> Members)> _ptWidthKeyTypeGroups = [];

    // --- マーカー表示(表示カテゴリ内) ---
    private readonly RadioButton _markerFull = new() { Content = "全文表示", GroupName = "marker" };
    private readonly RadioButton _markerHead = new() { Content = "先頭数文字のみ", GroupName = "marker" };
    private readonly TextBox _markerHeadChars = new() { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };

    // --- レーン文字サイズ(時間情報レーン/マーカーレーン、2026-07-26) ---
    private readonly TextBox _timeInfoFontSize = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _markerFontSize = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };

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
    private readonly TextBox _recentFilesLimit = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 自動保存・クラッシュ復旧(2026-07-25) ---
    private readonly CheckBox _autoSaveEnabled = new() { Content = "自動保存を有効にする(クラッシュ復旧用、通常の保存とは別領域に保存されますわ)" };
    private readonly TextBox _autoSaveInterval = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 譜面ビューReverse(2026-07-22、環境設定のみで切替) ---
    private readonly CheckBox _chartViewReverse = new() { Content = "譜面ビューをReverse表示する(tick0を下端・末尾を上端にする)" };

    // --- キーボードモードのSpace/B方向(2026-07-26要望対応) ---
    private readonly RadioButton _spaceBModeVisual = new() { Content = "見た目通りの上下(Spaceで下方向、Bで上方向、現在の実装)", GroupName = "spaceBMode", Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _spaceBModeTime = new() { Content = "前進/後退(時間基準。Reverse中は前進=上方向、後退=下方向になる)", GroupName = "spaceBMode", Margin = new Thickness(0, 0, 0, 2) };

    // --- キーボードモードの←/→方向(2026-07-26要望対応) ---
    private readonly RadioButton _leftRightModeVisual = new() { Content = "見た目通りの上下(←で上方向、→で下方向、現在の実装)", GroupName = "leftRightMode", Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _leftRightModeTime = new() { Content = "前進/後退(時間基準。Reverse中は→=上方向、←=下方向になる)", GroupName = "leftRightMode", Margin = new Thickness(0, 0, 0, 2) };

    // --- SKB操作モード(キーボード操作、2026-07-21) ---
    private readonly TextBox _kbdThreshold = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- グリッド分解能ショートカット(Ctrl+1〜9,0,-,^、2026-07-26) ---
    private readonly ComboBox _gridShortcutPreset = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };

    // --- musicURLからの楽曲取得(2026-07-26) ---
    private readonly CheckBox _musicUrlEnabled = new() { Content = "musicURLから楽曲を取得できるようにする" };
    private readonly TextBox _musicUrlFolder = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, IsReadOnly = true };
    private readonly Button _musicUrlBrowse = new() { Content = "参照...", Width = 70, Margin = new Thickness(4, 0, 0, 0) };

    // --- 全選択(Shift+Ctrl+A)の対象(2026-07-21) ---
    private readonly CheckBox _selAllNote = new() { Content = "ノート" };
    private readonly CheckBox _selAllFreeze = new() { Content = "フリーズアロー" };
    private readonly CheckBox _selAllSpeed = new() { Content = "速度変化(speed_data)" };
    private readonly CheckBox _selAllBoost = new() { Content = "個別加速(boost_data)" };
    private readonly CheckBox _selAllBpm = new() { Content = "BPM変化" };
    private readonly CheckBox _selAllTimeSig = new() { Content = "拍子変化" };
    private readonly CheckBox _selAllMarker = new() { Content = "マーカー" };

    // --- テンプレート(temp_*.json、2026-07-26) ---
    private readonly ListBox _templateList = new() { Margin = new Thickness(0, 0, 0, 8), Height = 260 };
    private readonly Button _templateEditButton = new() { Content = "編集", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _templateNewButton = new() { Content = "新規作成", Width = 90 };
    private readonly TemplateRepository? _templates;

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public PreferencesWindow(AppSettings current, int initialCategory = 0, TemplateRepository? templates = null)
    {
        _work = current.Clone();
        _templates = templates;

        Title = "環境設定";
        Width = 560;
        Height = 470;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize; // 2026-07-26要望対応: サイズ変更できるように
        WindowStyle = WindowStyle.ToolWindow;

        // --- カテゴリ一覧+パネル切替 ---
        var categories = new ListBox { Margin = new Thickness(8), Width = 120 };
        categories.Items.Add("表示");
        categories.Items.Add("テスト再生");
        categories.Items.Add("新規プロジェクト");
        categories.Items.Add("編集・保存");
        categories.Items.Add("キーボードモード");
        categories.Items.Add("musicURL取得");
        categories.Items.Add("テンプレート");
        categories.Items.Add("キーマクロ");
        categories.Items.Add("統計情報");

        var panels = new[] { BuildDisplayPanel(), BuildTestPlaybackPanel(), BuildNewProjectPanel(), BuildEditSavePanel(), BuildKeyboardModePanel(), BuildMusicUrlPanel(), BuildTemplatePanel(), BuildKeyMacroPanel(), BuildStatsPanel() };
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

    /// <summary>色コード入力欄+履歴ピッカーボタンの横並び行を作る(2026-07-23)。</summary>
    private UIElement ColorFieldRow(TextBox colorBox)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var historyBtn = new Button { Content = "履歴", Width = 36, Margin = new Thickness(4, 0, 0, 0) };
        historyBtn.Click += (_, _) => ColorHistoryPicker.Show(_work, historyBtn, hex => colorBox.Text = hex);
        row.Children.Add(colorBox);
        row.Children.Add(historyBtn);
        return row;
    }

    private UIElement BuildDisplayPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };

        // 2026-07-26要望対応: 譜面ビューのReverse設定を一番上に移動。
        p.Children.Add(Label("譜面ビュー", section: true));
        _chartViewReverse.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_chartViewReverse);
        p.Children.Add(Label("キーボードモード中のSpace/Bキーの移動方向:"));
        p.Children.Add(_spaceBModeVisual);
        p.Children.Add(_spaceBModeTime);
        p.Children.Add(Label("キーボードモード中の←/→キーの移動方向:"));
        p.Children.Add(_leftRightModeVisual);
        p.Children.Add(_leftRightModeTime);

        p.Children.Add(Label("ノート表示", section: true));
        _showImages.Margin = new Thickness(0, 0, 0, 4);
        _showGrid.Margin = new Thickness(0, 0, 0, 4);
        _excludeFreezeEndHighlight.Margin = new Thickness(16, 0, 0, 4); // 強調グリッドの子項目として少し字下げ
        p.Children.Add(_showImages);
        p.Children.Add(_showGrid);
        p.Children.Add(_excludeFreezeEndHighlight);
        p.Children.Add(Label("強調グリッドの太さ(px):"));
        p.Children.Add(_gridWidth);
        p.Children.Add(Label("強調グリッドの色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_gridColor));
        p.Children.Add(_gridPreview);
        _gridColor.TextChanged += (_, _) => _gridPreview.Background = SafeBrush(_gridColor.Text);
        _gridColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _gridColor.Text);

        p.Children.Add(Label("再生開始フレームライン", section: true));
        p.Children.Add(Label("太さ(px):"));
        p.Children.Add(_startLineWidth);
        p.Children.Add(Label("色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_startLineColor));
        p.Children.Add(_startLinePreview);
        _startLineColor.TextChanged += (_, _) => _startLinePreview.Background = SafeBrush(_startLineColor.Text);
        _startLineColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _startLineColor.Text);

        p.Children.Add(Label("カーソルライン(マウスモード)", section: true));
        p.Children.Add(Label("細い線の太さ(px):"));
        p.Children.Add(_cursorLineWidth);
        p.Children.Add(Label("細い線の色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_cursorLineColor));
        p.Children.Add(_cursorLinePreview);
        _cursorLineColor.TextChanged += (_, _) => _cursorLinePreview.Background = SafeBrush(_cursorLineColor.Text);
        _cursorLineColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _cursorLineColor.Text);

        p.Children.Add(Label("強調帯の太さ(px):"));
        p.Children.Add(_cursorHighlightWidth);
        p.Children.Add(Label("強調帯の色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_cursorHighlightColor));
        p.Children.Add(_cursorHighlightPreview);
        _cursorHighlightColor.TextChanged += (_, _) => _cursorHighlightPreview.Background = SafeBrush(_cursorHighlightColor.Text);
        _cursorHighlightColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _cursorHighlightColor.Text);

        p.Children.Add(Label("マーカーのコメント表示(仕様書7.4)", section: true));
        _markerFull.Margin = new Thickness(0, 0, 0, 2);
        _markerHead.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_markerFull);
        p.Children.Add(_markerHead);
        p.Children.Add(Label("先頭表示の文字数:"));
        p.Children.Add(_markerHeadChars);

        p.Children.Add(Label("レーン文字サイズ(ZoomScale=1.0時、pt)", section: true));
        p.Children.Add(Label("時間情報レーン(小節番号/frame/time):"));
        p.Children.Add(_timeInfoFontSize);
        p.Children.Add(Label("マーカーレーン:"));
        p.Children.Add(_markerFontSize);

        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>「テスト再生」カテゴリ: 全般(ノート音)→目視テスト→プレイテストの順に並べた統合パネル。
    /// 元は「目視テスト」「プレイテスト」の別カテゴリだったが、共通設定(ノート音)の置き場として
    /// 「全般」を新設した上で1つのカテゴリへ統合した。セクションの先頭には太字の見出しを、
    /// セクション間には区切り線(Separator)を入れて視認性を確保する。</summary>
    private UIElement BuildTestPlaybackPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };

        // --- 全般 ---
        p.Children.Add(Label("全般", section: true));
        p.Children.Add(Label("ノート音(目視テスト・プレイテストでノート通過時に鳴らす音):"));
        var soundsDir = AppPaths.FindAssetDir("sounds");
        if (soundsDir is null)
        {
            p.Children.Add(new TextBlock { Text = "(soundsフォルダが見つかりませんでした)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 4) });
        }
        else
        {
            foreach (var path in Directory.EnumerateFiles(soundsDir, "*.wav").OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                _noteSoundFile.Items.Add(System.IO.Path.GetFileName(path));
            p.Children.Add(_noteSoundFile);
        }

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });

        // --- 目視テスト ---
        p.Children.Add(Label("目視テスト", section: true));
        p.Children.Add(Label("再生位置ラインの追従方式:"));
        _followMode.Items.Add("ページ送り(画面外に出たら次の1画面へ)");
        _followMode.Items.Add("スムーズスクロール(ライン位置固定で譜面が流れる)");
        p.Children.Add(_followMode);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });

        // --- プレイテスト ---
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

        p.Children.Add(Label("ウィンドウ幅", section: true));
        _ptWidthAutoMode.Checked += (_, _) => UpdateWidthModeEnabled();
        _ptWidthPxMode.Checked += (_, _) => UpdateWidthModeEnabled();
        _ptWidthKeyTypeMode.Checked += (_, _) => UpdateWidthModeEnabled();
        p.Children.Add(_ptWidthAutoMode);
        p.Children.Add(_ptWidthPxMode);
        p.Children.Add(_ptWidthPx);
        p.Children.Add(_ptWidthKeyTypeMode);

        _ptWidthKeyTypeGroups.Clear();
        _ptWidthKeyTypeList.Children.Clear();
        if (_templates is null)
        {
            _ptWidthKeyTypeList.Children.Add(new TextBlock { Text = "(テンプレート一覧を取得できませんでした)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
        }
        else
        {
            // 幅ごとにグループ化し、幅の狭い順に並べる。各グループのラベルには、そのグループに属する
            // キー種のうち使用キー数が少ない順に最大3つを表示する(2つ以上併記する場合は末尾に「等」)。
            var groups = _templates.ListKeyTypeIds()
                .Select(id => _templates.Get(id))
                .GroupBy(t => PlaytestWindow.AutoSpreadWidth(t.KeyTypeId))
                .OrderBy(g => g.Key)
                .Select(g => new { Width = g.Key, Templates = g.OrderBy(t => t.KeyCount).ToList() });

            foreach (var g in groups)
            {
                var shown = g.Templates.Take(3).ToList();
                string names = string.Join(",", shown.Select(t => t.KeyTypeId));
                string label = $"{g.Width:0}px : {names}{(shown.Count >= 2 ? "等" : "")}";
                var radio = new RadioButton { Content = label, GroupName = "ptWidthKeyType", Margin = new Thickness(0, 0, 0, 2) };
                var members = g.Templates.Select(t => t.KeyTypeId).ToHashSet();
                _ptWidthKeyTypeGroups.Add((radio, shown[0].KeyTypeId, members));
                _ptWidthKeyTypeList.Children.Add(radio);
            }
        }
        p.Children.Add(_ptWidthKeyTypeList);

        p.Children.Add(Label("中断キー(BackSpaceは「再生開始フレームからやり直し」専用のため選択肢から除外)", section: true));
        _ptQuitDelete.Margin = new Thickness(0, 0, 0, 2);
        _ptQuitEscape.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_ptQuitDelete);
        p.Children.Add(_ptQuitEscape);

        p.Children.Add(Label("起動時ウェイト(ms単位)", section: true));
        p.Children.Add(Label("プレイテスト画面表示後、この時間だけ待ってから再生を開始する(0=待たない):"));
        p.Children.Add(_ptStartupWaitMs);

        p.Children.Add(Label("キー種ごとのReverse既定値", section: true));
        _ptReverseByKeyType.Clear();
        if (_templates is null)
        {
            p.Children.Add(new TextBlock { Text = "(テンプレート一覧を取得できませんでした)", Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });
        }
        else
        {
            foreach (var keyTypeId in _templates.ListKeyTypeIds())
            {
                var cb = new CheckBox { Content = $"{keyTypeId}k", Margin = new Thickness(0, 0, 0, 2) };
                _ptReverseByKeyType[keyTypeId] = cb;
                p.Children.Add(cb);
            }
        }

        // 2026-07-26e: キー種ごとの採用キーパターン。追加パターンを持つキー種のみ選択欄を出す
        // (danoniplus本家の「キーパターン」概念。エディタ本体の譜面ビュー・データ名等には影響せず、
        // プレイテストの見た目・キー入力にのみ反映される)。
        _ptPatternByKeyType.Clear();
        if (_templates is not null)
        {
            var withPatterns = new List<(string KeyTypeId, KeyTemplate Template)>();
            foreach (var keyTypeId in _templates.ListKeyTypeIds())
            {
                try
                {
                    var tpl = _templates.Get(keyTypeId);
                    if (tpl.PatternCount > 1) withPatterns.Add((keyTypeId, tpl));
                }
                catch { /* 読み込み失敗のキー種はここでは無視(他のテンプレート一覧欄でエラーが分かる) */ }
            }
            if (withPatterns.Count > 0)
            {
                p.Children.Add(Label("キー種ごとの採用キーパターン", section: true));
                foreach (var (keyTypeId, tpl) in withPatterns)
                {
                    p.Children.Add(Label($"{keyTypeId}k:"));
                    var combo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
                    combo.Items.Add("パターン0(既定)");
                    for (int i = 1; i < tpl.PatternCount; i++)
                    {
                        var name = tpl.ExtraPatterns[i - 1].Name;
                        combo.Items.Add(string.IsNullOrWhiteSpace(name) ? $"パターン{i}" : $"パターン{i}: {name}");
                    }
                    _ptPatternByKeyType[keyTypeId] = combo;
                    p.Children.Add(combo);
                }
            }
        }
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>ウィンドウ幅の指定方式ラジオ(直接入力/キー種から選択)に応じて、
    /// 対応する入力欄のIsEnabledを切り替える(2026-07-26)。</summary>
    private void UpdateWidthModeEnabled()
    {
        _ptWidthPx.IsEnabled = _ptWidthPxMode.IsChecked == true;
        _ptWidthKeyTypeList.IsEnabled = _ptWidthKeyTypeMode.IsChecked == true;
    }

    /// <summary>環境設定内部で使う「幅指定方式」の文字列表現(2026-07-26)</summary>
    private const string WidthModeAuto = "auto";
    private const string WidthModePx = "px";
    private const string WidthModeKeyType = "keyType";

    private UIElement BuildNewProjectPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("新規プロジェクトのデフォルト値(headerDefaults)", section: true));
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

        p.Children.Add(Label("自動保存・クラッシュ復旧", section: true));
        _autoSaveEnabled.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_autoSaveEnabled);
        p.Children.Add(Label("保存間隔(分):"));
        p.Children.Add(_autoSaveInterval);

        p.Children.Add(Label("色履歴", section: true));
        p.Children.Add(Label("色コード使用履歴の上限件数(デフォルト24):"));
        p.Children.Add(_colorHistLimit);

        p.Children.Add(Label("最近開いたファイル", section: true));
        p.Children.Add(Label("履歴の保持件数(デフォルト10):"));
        p.Children.Add(_recentFilesLimit);

        p.Children.Add(Label("全選択(Shift+Ctrl+A)の対象", section: true));
        foreach (var cb in new[] { _selAllNote, _selAllFreeze, _selAllSpeed, _selAllBoost, _selAllBpm, _selAllTimeSig, _selAllMarker })
            cb.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_selAllNote);
        p.Children.Add(_selAllFreeze);
        p.Children.Add(_selAllSpeed);
        p.Children.Add(_selAllBoost);
        p.Children.Add(_selAllBpm);
        p.Children.Add(_selAllTimeSig);
        p.Children.Add(_selAllMarker);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildKeyboardModePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("SKB操作モード(Ctrl+,)", section: true));
        p.Children.Add(Label("同時押し判定の閾値(ms、デフォルト30):"));
        p.Children.Add(_kbdThreshold);

        // --- グリッド分解能ショートカット(Ctrl+1〜9,0,-,^、2026-07-26) ---
        p.Children.Add(Label("グリッド分解能ショートカット(Ctrl+1〜9,0,-,^)", section: true));
        _gridShortcutPreset.Items.Add("オリジナルセット(分解能を単純な昇順で割り当て)");
        _gridShortcutPreset.Items.Add("SKB拡張セット(SKBエディタのCtrl+1〜7割り当てを踏襲)");
        p.Children.Add(_gridShortcutPreset);
        return p;
    }

    private UIElement BuildMusicUrlPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("musicURLからの楽曲取得", section: true));
        _musicUrlEnabled.Margin = new Thickness(0, 0, 0, 8);
        _musicUrlEnabled.Checked += (_, _) => _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = true;
        _musicUrlEnabled.Unchecked += (_, _) => _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = false;
        p.Children.Add(_musicUrlEnabled);

        p.Children.Add(Label("楽曲フォルダ:"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(_musicUrlFolder);
        _musicUrlBrowse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "楽曲フォルダを選択" };
            if (!string.IsNullOrWhiteSpace(_musicUrlFolder.Text)) dlg.InitialDirectory = _musicUrlFolder.Text;
            if (dlg.ShowDialog(this) == true) _musicUrlFolder.Text = dlg.FolderName;
        };
        row.Children.Add(_musicUrlBrowse);
        p.Children.Add(row);
        return p;
    }

    /// <summary>一覧行の表示用(2026-07-26要望: 「キー種 - ファイル名」形式)</summary>
    private sealed record TemplateListEntry(string KeyTypeId, string FileName, string Path)
    {
        public override string ToString() => $"{KeyTypeId} - {FileName}";
    }

    private UIElement BuildTemplatePanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("キー種テンプレート(temp_*.json)", section: true));
        p.Children.Add(_templateList);
        _templateList.SelectionChanged += (_, _) => _templateEditButton.IsEnabled = _templateList.SelectedItem is not null;
        _templateList.MouseDoubleClick += (_, _) =>
        {
            if (_templateList.SelectedItem is TemplateListEntry entry) OpenTemplateEditor(entry.Path);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _templateEditButton.Click += (_, _) =>
        {
            if (_templateList.SelectedItem is TemplateListEntry entry) OpenTemplateEditor(entry.Path);
        };
        _templateNewButton.Click += (_, _) => OpenTemplateEditor(null);
        row.Children.Add(_templateEditButton);
        row.Children.Add(_templateNewButton);
        p.Children.Add(row);

        RefreshTemplateList();
        return p;
    }

    private void RefreshTemplateList()
    {
        _templateList.Items.Clear();
        var dir = AppPaths.FindAssetDir("template");
        if (dir is null) return;
        foreach (var path in Directory.EnumerateFiles(dir, "temp_*.json").OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            string keyTypeId;
            try { keyTypeId = KeyTemplate.Load(path).KeyTypeId; }
            catch { keyTypeId = "?"; }
            _templateList.Items.Add(new TemplateListEntry(keyTypeId, System.IO.Path.GetFileName(path), path));
        }
    }

    private void OpenTemplateEditor(string? path)
    {
        var dir = AppPaths.FindAssetDir("template");
        if (dir is null) { _error.Text = "templateフォルダが見つかりませんの"; return; }
        var win = new TemplateEditorWindow(dir, path) { Owner = this };
        if (win.ShowDialog() != true) return;

        // 2026-07-26: 実行中のTemplateRepositoryキャッシュを破棄し、次回参照時にディスクの最新内容を
        // 再読込させる(編集直後にプロジェクトを新規作成/開いても古い内容のままになるのを防ぐ)。
        if (win.OriginalKeyTypeId is { } oldId) _templates?.Invalidate(oldId);
        if (win.SavedKeyTypeId is { } newId) _templates?.Invalidate(newId);
        RefreshTemplateList();
    }

    // =====================================================================
    // キーマクロ(2026-07-26要望対応、第三者要望): Ctrl+Shift+1〜9へ割り当てる、複数の機能を
    // 順番に実行するマクロ。既存の「レーン入替マクロ」(右パネル「マクロ」タブ)とは別機能。
    // レーン入替マクロと同じく「自分で決定して組み立てる」形にしてあり、選べる手順の種類
    // (KeyMacroStepKind)は今後の要望に応じて増やしていく想定(2026-07-26、ユーザー確定方針)。
    // _workを直接編集する(他カテゴリと異なり専用のLoadFrom/TryCommit処理を持たない。OK確定時に
    // Result=_workがそのまま返るため、ここでの編集は自動的に反映される)。
    // =====================================================================

    private static readonly (KeyMacroStepKind Kind, string Label, bool NeedsValue)[] KeyMacroKindItems =
    [
        (KeyMacroStepKind.SetPlaybackSpeed, "再生速度を設定(倍率)", true),
        (KeyMacroStepKind.SetPlaybackStartSeconds, "再生開始位置を設定(秒)", true),
        (KeyMacroStepKind.StartVisualTest, "目視テストを開始", false),
        (KeyMacroStepKind.StartPlaytest, "プレイテストを開始", false),
    ];

    private static string DescribeKeyMacroStep(KeyMacroStep step) => step.Kind switch
    {
        KeyMacroStepKind.SetPlaybackSpeed => $"再生速度を x{step.Value.ToString("0.00", CultureInfo.InvariantCulture)} に設定",
        KeyMacroStepKind.SetPlaybackStartSeconds => $"再生開始位置を {step.Value.ToString("0.00", CultureInfo.InvariantCulture)}秒 に設定",
        KeyMacroStepKind.StartVisualTest => "目視テストを開始",
        KeyMacroStepKind.StartPlaytest => "プレイテストを開始",
        _ => step.Kind.ToString(),
    };

    private UIElement BuildKeyMacroPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };

        p.Children.Add(Label("キーマクロ", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "Ctrl+Shift+数字キーへ割り当てる、複数の機能を順番に実行するマクロですわ。レーン入替マクロと同じく、手順を自分で組み立てる形になっております。選べる手順は今後の要望に応じて増やしていく予定ですわ。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        p.Children.Add(Label("スロット"));
        var slotCombo = new ComboBox
        {
            ItemsSource = Enumerable.Range(1, 9).Select(i => $"Ctrl+Shift+{i}").ToList(),
            SelectedIndex = 0,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
        };
        p.Children.Add(slotCombo);

        var stepsList = new ListBox { Height = 140, Margin = new Thickness(0, 0, 0, 8) };
        p.Children.Add(stepsList);

        KeyMacroDefinition? FindDef() => _work.KeyMacros.FirstOrDefault(m => m.Slot == slotCombo.SelectedIndex + 1);
        KeyMacroDefinition GetOrCreateDef()
        {
            var def = FindDef();
            if (def is null)
            {
                def = new KeyMacroDefinition { Slot = slotCombo.SelectedIndex + 1 };
                _work.KeyMacros.Add(def);
            }
            return def;
        }
        void RefreshSteps() => stepsList.ItemsSource = FindDef()?.Steps.Select(DescribeKeyMacroStep).ToList() ?? [];

        slotCombo.SelectionChanged += (_, _) => RefreshSteps();

        var reorderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var up = new Button { Content = "↑", Width = 32, Margin = new Thickness(0, 0, 4, 0) };
        var down = new Button { Content = "↓", Width = 32, Margin = new Thickness(0, 0, 4, 0) };
        var remove = new Button { Content = "削除", Width = 50 };
        up.Click += (_, _) =>
        {
            var def = FindDef();
            int idx = stepsList.SelectedIndex, newIdx = idx - 1;
            if (def is null || idx < 0 || newIdx < 0) return;
            (def.Steps[idx], def.Steps[newIdx]) = (def.Steps[newIdx], def.Steps[idx]);
            RefreshSteps();
            stepsList.SelectedIndex = newIdx;
        };
        down.Click += (_, _) =>
        {
            var def = FindDef();
            int idx = stepsList.SelectedIndex, newIdx = idx + 1;
            if (def is null || idx < 0 || newIdx >= def.Steps.Count) return;
            (def.Steps[idx], def.Steps[newIdx]) = (def.Steps[newIdx], def.Steps[idx]);
            RefreshSteps();
            stepsList.SelectedIndex = newIdx;
        };
        remove.Click += (_, _) =>
        {
            var def = FindDef();
            int idx = stepsList.SelectedIndex;
            if (def is null || idx < 0 || idx >= def.Steps.Count) return;
            def.Steps.RemoveAt(idx);
            _work.KeyMacros.RemoveAll(m => m.Steps.Count == 0); // 空になったスロット定義は残さない
            RefreshSteps();
        };
        reorderRow.Children.Add(up);
        reorderRow.Children.Add(down);
        reorderRow.Children.Add(remove);
        p.Children.Add(reorderRow);

        p.Children.Add(Label("手順を追加", section: true));
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var kindCombo = new ComboBox
        {
            ItemsSource = KeyMacroKindItems.Select(k => k.Label).ToList(),
            SelectedIndex = 0,
            Width = 220,
            Margin = new Thickness(0, 0, 4, 0),
        };
        var valueBox = new TextBox { Width = 70, Text = "1.0", Margin = new Thickness(0, 0, 4, 0) };
        kindCombo.SelectionChanged += (_, _) => valueBox.IsEnabled = KeyMacroKindItems[kindCombo.SelectedIndex].NeedsValue;
        var add = new Button { Content = "追加", Width = 50 };
        add.Click += (_, _) =>
        {
            var (kind, _, needsValue) = KeyMacroKindItems[kindCombo.SelectedIndex];
            double value = 0;
            if (needsValue && (!double.TryParse(valueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0))
            {
                _error.Text = "キーマクロの値は0以上の数値で入力してくださいませ。";
                return;
            }
            _error.Text = "";
            GetOrCreateDef().Steps.Add(new KeyMacroStep { Kind = kind, Value = value });
            RefreshSteps();
        };
        addRow.Children.Add(kindCombo);
        addRow.Children.Add(valueBox);
        addRow.Children.Add(add);
        p.Children.Add(addRow);

        RefreshSteps();
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // =====================================================================
    // 統計情報(2026-07-26、閲覧専用。ITTNアナライザー/おにスターの隠し機能解禁条件にも使う
    // カウンタだが、ここでは解禁段階等には一切触れず、純粋な利用実績として並べるだけにする)。
    // =====================================================================

    private UIElement BuildStatsPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("統計情報", section: true));

        void Row(string label, int value)
        {
            p.Children.Add(new TextBlock
            {
                Text = $"{label}: {value:N0}",
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        Row("オブジェクト設置回数", _work.StatObjectsPlaced);
        Row("オブジェクト削除回数", _work.StatObjectsDeleted);
        Row("オブジェクトコピー回数", _work.StatObjectsCopied);
        Row("オブジェクト切り取り回数", _work.StatObjectsCut);
        Row("オブジェクトペースト回数", _work.StatObjectsPasted);
        Row("算出・再算出ボタン押下回数", _work.StatOniStarRecalcPresses);
        Row("新規プロジェクト回数", _work.StatNewProjectCount);
        Row("プロジェクト保存回数", _work.StatProjectSaveCount);
        Row("dosエクスポート回数", _work.StatDosExportCount);
        Row("Undo/Redo実行回数(合計)", _work.StatUndoRedoCount);
        Row("プレイテスト起動回数", _work.StatPlaytestLaunchCount);
        Row("プレイテスト中に打鍵で消したノート数", _work.StatPlaytestNotesCleared);
        Row("マクロ実行回数", _work.StatMacroRunCount);
        Row("アプリ起動回数", _work.StatAppLaunchCount);
        Row("クラッシュ検出回数", _work.StatCrashCount);

        return p;
    }

    private void LoadFrom(AppSettings s)
    {
        _showImages.IsChecked = s.ShowNoteImages;
        _showGrid.IsChecked = s.ShowHighlightGrid;
        _excludeFreezeEndHighlight.IsChecked = s.ExcludeFreezeEndFromHighlight;
        _gridWidth.Text = s.HighlightLineWidth.ToString(CultureInfo.InvariantCulture);
        _gridColor.Text = s.HighlightLineColorHex;
        _gridPreview.Background = SafeBrush(s.HighlightLineColorHex);
        _startLineWidth.Text = s.PlaybackStartLineWidth.ToString(CultureInfo.InvariantCulture);
        _startLineColor.Text = s.PlaybackStartLineColorHex;
        _startLinePreview.Background = SafeBrush(s.PlaybackStartLineColorHex);
        _cursorLineWidth.Text = s.CursorLineWidth.ToString(CultureInfo.InvariantCulture);
        _cursorLineColor.Text = s.CursorLineColorHex;
        _cursorLinePreview.Background = SafeBrush(s.CursorLineColorHex);
        _cursorHighlightWidth.Text = s.CursorHighlightWidth.ToString(CultureInfo.InvariantCulture);
        _cursorHighlightColor.Text = s.CursorHighlightColorHex;
        _cursorHighlightPreview.Background = SafeBrush(s.CursorHighlightColorHex);
        if (_noteSoundFile.Items.Contains(s.NoteSoundFileName)) _noteSoundFile.SelectedItem = s.NoteSoundFileName;
        else if (_noteSoundFile.Items.Count > 0) _noteSoundFile.SelectedIndex = 0;
        _followMode.SelectedIndex = s.VisualTestFollowMode == "smooth" ? 1 : 0;
        _ptReverse.IsChecked = s.PlaytestReverse;
        _ptHiSpeed.SelectedItem = _ptHiSpeed.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestHiSpeed)).First();
        _ptOffset.Text = s.PlaytestOffsetFrames.ToString(CultureInfo.InvariantCulture);
        _ptScale.SelectedItem = _ptScale.Items.Cast<double>().OrderBy(v => Math.Abs(v - s.PlaytestWindowScale)).First();
        _ptWidthPx.Text = s.PlaytestWindowWidthPx.ToString(CultureInfo.InvariantCulture);
        _ptWidthAutoMode.IsChecked = s.PlaytestWindowWidthMode == WidthModeAuto;
        _ptWidthPxMode.IsChecked = s.PlaytestWindowWidthMode == WidthModePx;
        _ptWidthKeyTypeMode.IsChecked = s.PlaytestWindowWidthMode != WidthModeAuto && s.PlaytestWindowWidthMode != WidthModePx;
        foreach (var g in _ptWidthKeyTypeGroups)
            g.Radio.IsChecked = g.Members.Contains(s.PlaytestWindowWidthKeyType);
        // 保存済みのキー種が現在のテンプレート一覧に見当たらない場合(削除等)は先頭グループへフォールバック
        if (_ptWidthKeyTypeGroups.Count > 0 && !_ptWidthKeyTypeGroups.Any(g => g.Radio.IsChecked == true))
            _ptWidthKeyTypeGroups[0].Radio.IsChecked = true;
        UpdateWidthModeEnabled();
        _ptQuitDelete.IsChecked = s.PlaytestQuitKeyDelete;
        _ptQuitEscape.IsChecked = s.PlaytestQuitKeyEscape;
        _ptStartupWaitMs.Text = s.PlaytestStartupWaitMs.ToString(CultureInfo.InvariantCulture);
        foreach (var (keyTypeId, combo) in _ptPatternByKeyType)
        {
            int idx = s.PlaytestPatternByKeyType.TryGetValue(keyTypeId, out var pi) ? pi : 0;
            combo.SelectedIndex = idx >= 0 && idx < combo.Items.Count ? idx : 0;
        }
        foreach (var (keyTypeId, cb) in _ptReverseByKeyType)
            cb.IsChecked = s.PlaytestReverseByKeyType.TryGetValue(keyTypeId, out var rev) && rev;
        _markerFull.IsChecked = s.MarkerCommentFull;
        _markerHead.IsChecked = !s.MarkerCommentFull;
        _markerHeadChars.Text = s.MarkerCommentHeadChars.ToString(CultureInfo.InvariantCulture);
        _timeInfoFontSize.Text = s.TimeInfoFontSize.ToString(CultureInfo.InvariantCulture);
        _markerFontSize.Text = s.MarkerFontSize.ToString(CultureInfo.InvariantCulture);
        _chartViewReverse.IsChecked = s.ChartViewReverse;
        _spaceBModeTime.IsChecked = s.KeyboardModeSpaceBMode == "time";
        _spaceBModeVisual.IsChecked = s.KeyboardModeSpaceBMode != "time";
        _leftRightModeTime.IsChecked = s.KeyboardModeLeftRightMode == "time";
        _leftRightModeVisual.IsChecked = s.KeyboardModeLeftRightMode != "time";
        _defStartFrame.Text = s.DefaultStartFrame.ToString(CultureInfo.InvariantCulture);
        _defBlankFrame.Text = s.DefaultBlankFrame.ToString(CultureInfo.InvariantCulture);
        _defTuning.Text = s.DefaultTuning;
        _defFrzAttempt.Text = s.DefaultFrzAttempt.ToString(CultureInfo.InvariantCulture);
        _defBpm.Text = s.DefaultBpm.ToString(CultureInfo.InvariantCulture);
        _undoSize.Text = s.UndoHistorySize.ToString(CultureInfo.InvariantCulture);
        _confirmUnsaved.IsChecked = s.ConfirmUnsavedOnClose;
        _autoSaveEnabled.IsChecked = s.AutoSaveEnabled;
        _autoSaveInterval.Text = s.AutoSaveIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        _colorHistLimit.Text = s.ColorHistoryLimit.ToString(CultureInfo.InvariantCulture);
        _recentFilesLimit.Text = s.RecentFilesLimit.ToString(CultureInfo.InvariantCulture);
        _selAllNote.IsChecked = s.SelectAllTargetNote;
        _selAllFreeze.IsChecked = s.SelectAllTargetFreeze;
        _selAllSpeed.IsChecked = s.SelectAllTargetSpeed;
        _selAllBoost.IsChecked = s.SelectAllTargetBoost;
        _selAllBpm.IsChecked = s.SelectAllTargetBpm;
        _selAllTimeSig.IsChecked = s.SelectAllTargetTimeSignature;
        _selAllMarker.IsChecked = s.SelectAllTargetMarker;
        _kbdThreshold.Text = s.SimultaneousPressThresholdMs.ToString(CultureInfo.InvariantCulture);
        _gridShortcutPreset.SelectedIndex = s.GridShortcutPreset == GridShortcutPresets.SkbExtended ? 1 : 0;
        _musicUrlEnabled.IsChecked = s.MusicUrlAutoLoadEnabled;
        _musicUrlFolder.Text = s.MusicUrlBaseFolder;
        _musicUrlFolder.IsEnabled = _musicUrlBrowse.IsEnabled = s.MusicUrlAutoLoadEnabled;
    }

    private bool TryCommit()
    {
        _error.Text = "";
        if (_showImages.IsChecked != true && _showGrid.IsChecked != true)
        { _error.Text = "ノート画像と強調グリッドの両方をOFFにはできませんの(どちらかはONにしてくださいまし)"; return false; }
        if (_ptQuitDelete.IsChecked != true && _ptQuitEscape.IsChecked != true)
        { _error.Text = "プレイテストの中断キーは最低1つはcheckedにしてくださいまし"; return false; }
        if (!TryNonNegativeInt(_ptStartupWaitMs.Text, out var ptWait))
        { _error.Text = "プレイテスト起動時ウェイトは0以上の整数(ms)で入力してくださいまし"; return false; }
        if (!TryPositive(_gridWidth.Text, out var gw))
        { _error.Text = "強調グリッドの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_gridColor.Text))
        { _error.Text = "強調グリッドの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_startLineWidth.Text, out var sw))
        { _error.Text = "再生開始ラインの太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_startLineColor.Text))
        { _error.Text = "再生開始ラインの色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_cursorLineWidth.Text, out var clw))
        { _error.Text = "カーソルライン(細い線)の太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_cursorLineColor.Text))
        { _error.Text = "カーソルライン(細い線)の色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!TryPositive(_cursorHighlightWidth.Text, out var chw))
        { _error.Text = "カーソルライン(強調帯)の太さは正の数値で入力してくださいまし"; return false; }
        if (!TryColor(_cursorHighlightColor.Text))
        { _error.Text = "カーソルライン(強調帯)の色は #RRGGBB 形式で入力してくださいまし"; return false; }
        if (!double.TryParse(_ptOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ofs))
        { _error.Text = "調整オフセットは数値で入力してくださいまし"; return false; }
        if (!TryPositive(_ptWidthPx.Text, out var ptWidthPx))
        { _error.Text = "プレイテストのウィンドウ幅は正の数値で入力してくださいまし"; return false; }
        if (!int.TryParse(_markerHeadChars.Text, out var headChars) || headChars < 1)
        { _error.Text = "マーカー先頭表示の文字数は1以上の整数で入力してくださいまし"; return false; }
        if (!TryPositive(_timeInfoFontSize.Text, out var timeInfoFontSize))
        { _error.Text = "時間情報レーンの文字サイズは正の数値で入力してくださいまし"; return false; }
        if (!TryPositive(_markerFontSize.Text, out var markerFontSize))
        { _error.Text = "マーカーレーンの文字サイズは正の数値で入力してくださいまし"; return false; }
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
        if (!TryPositive(_autoSaveInterval.Text, out var autoSaveInterval))
        { _error.Text = "自動保存の間隔は正の数値(分)で入力してくださいまし"; return false; }
        if (!int.TryParse(_colorHistLimit.Text, out var colorLimit) || colorLimit < 1)
        { _error.Text = "色履歴の上限件数は1以上の整数で入力してくださいまし"; return false; }
        if (!int.TryParse(_recentFilesLimit.Text, out var recentLimit) || recentLimit < 1)
        { _error.Text = "最近開いたファイルの保持件数は1以上の整数で入力してくださいまし"; return false; }
        if (!TryPositive(_kbdThreshold.Text, out var kbdThreshold))
        { _error.Text = "同時押し判定の閾値は正の数値で入力してくださいまし"; return false; }
        if (_musicUrlEnabled.IsChecked == true && string.IsNullOrWhiteSpace(_musicUrlFolder.Text))
        { _error.Text = "musicURLからの楽曲取得をONにする場合、楽曲フォルダを指定してくださいまし"; return false; }

        _work.ShowNoteImages = _showImages.IsChecked == true;
        _work.ShowHighlightGrid = _showGrid.IsChecked == true;
        _work.ExcludeFreezeEndFromHighlight = _excludeFreezeEndHighlight.IsChecked == true;
        _work.HighlightLineWidth = gw;
        _work.HighlightLineColorHex = _gridColor.Text;
        _work.PlaybackStartLineWidth = sw;
        _work.PlaybackStartLineColorHex = _startLineColor.Text;
        _work.CursorLineWidth = clw;
        _work.CursorLineColorHex = _cursorLineColor.Text;
        _work.CursorHighlightWidth = chw;
        _work.CursorHighlightColorHex = _cursorHighlightColor.Text;
        if (_noteSoundFile.Items.Count > 0 && _noteSoundFile.SelectedItem is string ns) _work.NoteSoundFileName = ns;
        _work.VisualTestFollowMode = _followMode.SelectedIndex == 1 ? "smooth" : "page";
        _work.PlaytestReverse = _ptReverse.IsChecked == true;
        if (_ptHiSpeed.SelectedItem is double hs) _work.PlaytestHiSpeed = hs;
        _work.PlaytestOffsetFrames = ofs;
        if (_ptScale.SelectedItem is double sc) _work.PlaytestWindowScale = sc;
        _work.PlaytestWindowWidthMode = _ptWidthAutoMode.IsChecked == true ? WidthModeAuto
            : _ptWidthPxMode.IsChecked == true ? WidthModePx
            : WidthModeKeyType;
        _work.PlaytestWindowWidthPx = ptWidthPx;
        var selectedWidthGroup = _ptWidthKeyTypeGroups.FirstOrDefault(g => g.Radio.IsChecked == true);
        if (selectedWidthGroup.Radio is not null) _work.PlaytestWindowWidthKeyType = selectedWidthGroup.RepresentativeKeyTypeId;
        _work.PlaytestQuitKeyDelete = _ptQuitDelete.IsChecked == true;
        _work.PlaytestQuitKeyEscape = _ptQuitEscape.IsChecked == true;
        _work.PlaytestStartupWaitMs = ptWait;
        _work.PlaytestPatternByKeyType = _ptPatternByKeyType.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value.SelectedIndex));
        _work.PlaytestReverseByKeyType = _ptReverseByKeyType.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true);
        _work.MarkerCommentFull = _markerFull.IsChecked == true;
        _work.MarkerCommentHeadChars = headChars;
        _work.TimeInfoFontSize = timeInfoFontSize;
        _work.MarkerFontSize = markerFontSize;
        _work.ChartViewReverse = _chartViewReverse.IsChecked == true;
        _work.KeyboardModeSpaceBMode = _spaceBModeTime.IsChecked == true ? "time" : "visual";
        _work.KeyboardModeLeftRightMode = _leftRightModeTime.IsChecked == true ? "time" : "visual";
        _work.DefaultStartFrame = defSf;
        _work.DefaultBlankFrame = defBf;
        _work.DefaultTuning = _defTuning.Text;
        _work.DefaultFrzAttempt = defFa;
        _work.DefaultBpm = defBpm;
        _work.UndoHistorySize = undoSize;
        _work.ConfirmUnsavedOnClose = _confirmUnsaved.IsChecked == true;
        _work.AutoSaveEnabled = _autoSaveEnabled.IsChecked == true;
        _work.AutoSaveIntervalMinutes = autoSaveInterval;
        _work.ColorHistoryLimit = colorLimit;
        _work.RecentFilesLimit = recentLimit;
        if (_work.RecentFiles.Count > recentLimit) _work.RecentFiles.RemoveRange(recentLimit, _work.RecentFiles.Count - recentLimit);
        _work.SelectAllTargetNote = _selAllNote.IsChecked == true;
        _work.SelectAllTargetFreeze = _selAllFreeze.IsChecked == true;
        _work.SelectAllTargetSpeed = _selAllSpeed.IsChecked == true;
        _work.SelectAllTargetBoost = _selAllBoost.IsChecked == true;
        _work.SelectAllTargetBpm = _selAllBpm.IsChecked == true;
        _work.SelectAllTargetTimeSignature = _selAllTimeSig.IsChecked == true;
        _work.SelectAllTargetMarker = _selAllMarker.IsChecked == true;
        _work.SimultaneousPressThresholdMs = kbdThreshold;
        _work.GridShortcutPreset = _gridShortcutPreset.SelectedIndex == 1
            ? GridShortcutPresets.SkbExtended
            : GridShortcutPresets.Original;
        _work.MusicUrlAutoLoadEnabled = _musicUrlEnabled.IsChecked == true;
        _work.MusicUrlBaseFolder = _musicUrlFolder.Text;
        Result = _work;
        return true;
    }

    private static bool TryPositive(string text, out double v) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;

    /// <summary>0以上の整数(2026-07-26d、プレイテスト起動時ウェイト等)</summary>
    private static bool TryNonNegativeInt(string text, out int v) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 0;

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
