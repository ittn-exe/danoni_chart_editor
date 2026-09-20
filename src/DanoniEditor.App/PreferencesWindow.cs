using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly CheckBox _useNoteColorForHighlight = new() { Content = "強調表示の色をノートの色にする" };
    private readonly TextBox _gridWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _gridColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _gridPreview = MakePreview();
    private readonly TextBox _startLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _startLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _startLinePreview = MakePreview();
    private readonly CheckBox _carryOverPlaybackStart = new() { Content = "タブ複製時に再生開始ラインを複製先へ引き継ぐ" };
    // --- カーソルライン(マウスモード、2026-07-25) ---
    private readonly TextBox _cursorLineWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorLineColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorLinePreview = MakePreview();
    private readonly TextBox _cursorHighlightWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _cursorHighlightColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _cursorHighlightPreview = MakePreview();
    private readonly TextBox _macroRangeWidth = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _macroRangeColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _macroRangePreview = MakePreview();
    private readonly TextBox _linkedNoteSizeRatio = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _linkedNoteColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _linkedNotePreview = MakePreview();
    private readonly TextBox _linkedHighlightWidthRatio = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _linkedHighlightHeight = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _linkedHighlightColor = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _linkedHighlightPreview = MakePreview();

    // --- カラーピッカー > お気に入りの管理(2026-08-08新設、お気に入りの色機能)。
    // 登録(☆登録ボタン)は各色欄側で完結するため、ここでは一覧表示と削除のみを扱う。
    // SelectionMode.Multiple: クリックのたびに選択がトグルする(Ctrl/Shift不要、「トグル式で複数選択可」
    // というユーザー確定仕様)。
    private readonly ListBox _favoriteColorsList = new() { SelectionMode = SelectionMode.Multiple, Height = 320 };

    // --- カラーピッカー > お気に入りのD&D並び替え(2026-08-08要望対応)。難易度タブの入替えと同じ
    // 「掴んだ内容をドロップ先に挿入する」「挿入先がわかるよう線でオーバーレイ表示」方式。 ---
    private readonly Border _favColorInsertIndicator = new()
    {
        Height = 2, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private Point _favColorDragStartPoint;
    private int _favColorDragSourceIndex = -1;

    // --- テスト再生 > 全般: ノート音として鳴らす./sounds内の音声ファイル選択 ---
    private readonly ComboBox _noteSoundFile = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 目視テスト ---
    private readonly ComboBox _followMode = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _visualTestAcceptNotes = new() { Content = "ノート配置受付(ONの間、キー配置をプレイテスト用に切り替えて配置できます)" };
    // --- 目視テスト自動復帰(2026-07-29要望対応) ---
    private readonly CheckBox _vtAutoReturnEnabled = new() { Content = "再生開始ラインへ自動で戻る" };
    private readonly RadioButton _vtAutoReturnByMeasures = new() { Content = "小節数で指定", GroupName = "vtAutoReturnUnit" };
    private readonly RadioButton _vtAutoReturnBySeconds = new() { Content = "秒数で指定", GroupName = "vtAutoReturnUnit" };
    private readonly TextBox _vtAutoReturnMeasures = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _vtAutoReturnSeconds = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RadioButton _vtAutoReturnContinue = new() { Content = "再生を継続する(先頭からループ)", GroupName = "vtAutoReturnAction" };
    private readonly RadioButton _vtAutoReturnStop = new() { Content = "目視テストを終了する", GroupName = "vtAutoReturnAction" };

    // --- プレイテスト ---
    private readonly CheckBox _ptReverse = new() { Content = "Reverse(スクロール反転)" };
    private readonly ComboBox _ptHiSpeed = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _ptOffset = new() { Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _ptScale = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _ptQuitDelete = new() { Content = "Delete" };
    private readonly CheckBox _ptQuitEscape = new() { Content = "Escape" };
    // --- プレイテスト起動時ウェイト(2026-07-26d要望対応、ms単位) ---
    private readonly TextBox _ptStartupWaitMs = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    // --- プレイテスト中の小節線表示(2026-07-27要望対応、既定OFF) ---
    private readonly CheckBox _ptShowMeasureLines = new() { Content = "小節線を表示(小節番号付き)" };
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
    private readonly CheckBox _autoSaveEnabled = new() { Content = "自動保存を有効にする(クラッシュ復旧用、通常の保存とは別領域に保存されます)" };
    private readonly TextBox _autoSaveInterval = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };

    // --- 譜面ビューReverse(2026-07-22、環境設定のみで切替) ---
    private readonly CheckBox _chartViewReverse = new() { Content = "譜面ビューをReverse表示する(tick0を下端・末尾を上端にする)" };

    // --- レーンラベルヘッダーの表示位置(2026-08-08要望対応、ChartViewReverseとは独立) ---
    private readonly RadioButton _laneHeaderTop = new() { Content = "上部に固定表示", GroupName = "laneHeaderPosition", Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _laneHeaderBottom = new() { Content = "下部に固定表示", GroupName = "laneHeaderPosition", Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _laneHeaderHidden = new() { Content = "非表示", GroupName = "laneHeaderPosition", Margin = new Thickness(0, 0, 0, 2) };

    // --- フレーム数のblankFrame込み表示(2026-08-08要望対応) ---
    private readonly CheckBox _showFrameWithBlankFrame = new() { Content = "フレーム数をblankFrame込みの値で表示する(dos.txt出力値と一致させたい場合はON)" };

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

    // --- ショートカットキーカスタマイズ(2026-07-27要望対応) ---
    private readonly ListBox _shortcutsList = new() { Height = 220, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _shortcutCaptureStatus = new() { Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Bold };
    private ShortcutId? _capturingShortcutId;

    // --- キーボードモード専用ショートカットキーカスタマイズ(2026-07-29要望対応) ---
    private readonly ListBox _keyboardShortcutsList = new() { Height = 220, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _keyboardShortcutCaptureStatus = new() { Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Bold };
    private KeyboardModeShortcutId? _capturingKeyboardShortcutId;

    // --- テンプレート(temp_*.json、2026-07-26) ---
    private readonly ListBox _templateList = new() { Margin = new Thickness(0, 0, 0, 8), Height = 260 };
    private readonly Button _templateEditButton = new() { Content = "編集", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _templateNewButton = new() { Content = "新規作成", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
    /// <summary>2026-08-02要望対応: 選択中テンプレートを本体互換のカスタムキー定義テキストへ
    /// エクスポートするウィンドウを開くボタン。</summary>
    private readonly Button _templateExportButton = new() { Content = "カスタムキー定義へエクスポート", Width = 190, IsEnabled = false };
    private readonly Button _templateImportButton = new() { Content = "カスタムキー定義からインポート", Width = 190 };
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

        // 2026-07-29要望対応: 前回終了時のウィンドウサイズを復元する。
        if (current.PreferencesWindowWidth is { } prefW && double.IsFinite(prefW) && prefW > 0) Width = prefW;
        if (current.PreferencesWindowHeight is { } prefH && double.IsFinite(prefH) && prefH > 0) Height = prefH;

        // OK/キャンセルどちらで閉じてもサイズは保存する(MainWindow本体の位置保存と同じ考え方。
        // _workは「キャンセル時は破棄される作業コピー」のため、渡された現行のcurrentへ直接書き込む)。
        Closed += (_, _) =>
        {
            current.PreferencesWindowWidth = ActualWidth;
            current.PreferencesWindowHeight = ActualHeight;
            current.Save(AppPaths.SettingsFilePath);
        };

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
        categories.Items.Add("ショートカットキー");
        categories.Items.Add("カラーピッカー");
        categories.Items.Add("統計情報");

        var panels = new[] { BuildDisplayPanel(), BuildTestPlaybackPanel(), BuildNewProjectPanel(), BuildEditSavePanel(), BuildKeyboardModePanel(), BuildMusicUrlPanel(), BuildTemplatePanel(), BuildKeyMacroPanel(), BuildShortcutsPanel(), BuildColorPickerPanel(), BuildStatsPanel() };
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

        // 2026-07-27要望対応: ショートカットキーのキーキャプチャ。PreviewKeyDownで先取りすることで、
        // OK/キャンセルボタンのアクセスキーやIsCancel(Escape)の既定動作より先に処理する。
        PreviewKeyDown += PreferencesWindow_PreviewKeyDown;

        LoadFrom(_work);
    }

    // =====================================================================
    // ショートカットキーカスタマイズ(2026-07-27要望対応)。一覧表示+選択項目のダブルクリックで
    // キーキャプチャモードに入り、次に押されたキーをそのショートカットへ割り当てる。
    // 衝突時は警告(確認ダイアログ)した上で、入れ替え(既存の割り当て先には元のキーを譲る)を許可する。
    // =====================================================================

    private sealed record ShortcutRow(ShortcutId Id, string Display);

    private void RefreshShortcutsList()
    {
        int selected = _shortcutsList.SelectedIndex;
        _shortcutsList.ItemsSource = Enum.GetValues<ShortcutId>().Select(id =>
        {
            var meta = ShortcutDefaults.All[id];
            var binding = _work.GetShortcut(id);
            return new ShortcutRow(id, $"{meta.DisplayName}　　[{binding.DisplayText()}]");
        }).ToList();
        if (selected >= 0 && selected < _shortcutsList.Items.Count) _shortcutsList.SelectedIndex = selected;
    }

    private UIElement BuildShortcutsPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("ショートカットキー", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "一覧から変更したい操作をダブルクリックすると、次に押したキーがそのまま新しい割り当てになります。既に他の操作へ割り当て済みのキーを選んだ場合は、警告した上で入れ替えいたします。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _shortcutCaptureStatus.Text = "";
        p.Children.Add(_shortcutCaptureStatus);

        _shortcutsList.DisplayMemberPath = "Display";
        _shortcutsList.MouseDoubleClick += (_, _) =>
        {
            if (_shortcutsList.SelectedItem is ShortcutRow row) BeginShortcutCapture(row.Id);
        };
        p.Children.Add(_shortcutsList);

        var resetAll = new Button { Content = "デフォルト値へのリセット", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        resetAll.Click += (_, _) =>
        {
            CancelShortcutCapture();
            _work.ResetAllShortcutsToDefault();
            RefreshShortcutsList();
        };
        p.Children.Add(resetAll);

        // --- キーボードモード専用ショートカット(2026-07-29要望対応) ---
        p.Children.Add(Label("キーボードモード中のショートカット", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "キーボードモードON中だけ有効なショートカットです。上のマウスモード側と同じ物理キーを割り当てても衝突扱いにはなりません(キーボードモードのON/OFFで挙動が変わる仕様のため)。ノート入力キー(各キーテンプレートで宣言)はここには含まれません。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _keyboardShortcutCaptureStatus.Text = "";
        p.Children.Add(_keyboardShortcutCaptureStatus);

        _keyboardShortcutsList.DisplayMemberPath = "Display";
        _keyboardShortcutsList.MouseDoubleClick += (_, _) =>
        {
            if (_keyboardShortcutsList.SelectedItem is KeyboardShortcutRow row) BeginKeyboardShortcutCapture(row.Id);
        };
        p.Children.Add(_keyboardShortcutsList);

        var resetAllKb = new Button { Content = "デフォルト値へのリセット", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        resetAllKb.Click += (_, _) =>
        {
            CancelKeyboardShortcutCapture();
            _work.ResetAllKeyboardModeShortcutsToDefault();
            RefreshKeyboardShortcutsList();
        };
        p.Children.Add(resetAllKb);

        RefreshShortcutsList();
        RefreshKeyboardShortcutsList();
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private sealed record KeyboardShortcutRow(KeyboardModeShortcutId Id, string Display);

    private void RefreshKeyboardShortcutsList()
    {
        int selected = _keyboardShortcutsList.SelectedIndex;
        _keyboardShortcutsList.ItemsSource = Enum.GetValues<KeyboardModeShortcutId>().Select(id =>
        {
            var meta = KeyboardModeShortcutDefaults.All[id];
            var key = _work.GetKeyboardModeShortcutKey(id);
            return new KeyboardShortcutRow(id, $"{meta.DisplayName}　　[{key}]");
        }).ToList();
        if (selected >= 0 && selected < _keyboardShortcutsList.Items.Count) _keyboardShortcutsList.SelectedIndex = selected;
    }

    private void BeginKeyboardShortcutCapture(KeyboardModeShortcutId id)
    {
        _capturingKeyboardShortcutId = id;
        _keyboardShortcutCaptureStatus.Text = $"「{KeyboardModeShortcutDefaults.All[id].DisplayName}」: 割り当てたいキーを押してください(Escapeで取消、修飾キーは無視されます)";
    }

    private void CancelKeyboardShortcutCapture()
    {
        _capturingKeyboardShortcutId = null;
        _keyboardShortcutCaptureStatus.Text = "";
    }

    private void BeginShortcutCapture(ShortcutId id)
    {
        _capturingShortcutId = id;
        _shortcutCaptureStatus.Text = $"「{ShortcutDefaults.All[id].DisplayName}」: 割り当てたいキーを押してください(Escapeで取消)";
    }

    private void CancelShortcutCapture()
    {
        _capturingShortcutId = null;
        _shortcutCaptureStatus.Text = "";
    }

    private void PreferencesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingKeyboardShortcutId is { } kbId) { HandleKeyboardShortcutCaptureKey(e, kbId); return; }
        if (_capturingShortcutId is not { } id) return;
        e.Handled = true; // キャプチャ中はOK/キャンセルの既定キー動作(Escape等)より優先する

        if (e.Key == Key.Escape) { CancelShortcutCapture(); RefreshShortcutsList(); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsPureModifierKey(key)) return; // 修飾キー単独では確定しない、次のキー入力を待つ

        var newBinding = new ShortcutBinding(
            key.ToString(),
            ctrl: Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            alt: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));

        ShortcutId? conflictId = null;
        foreach (var other in Enum.GetValues<ShortcutId>())
        {
            if (other == id) continue;
            if (_work.GetShortcut(other).ConflictsWith(newBinding)) { conflictId = other; break; }
        }

        if (conflictId is { } cid)
        {
            var conflictName = ShortcutDefaults.All[cid].DisplayName;
            var result = MessageBox.Show(
                this,
                $"「{newBinding.DisplayText()}」は既に「{conflictName}」に割り当てられています。\n入れ替えてよろしいですか?(「{conflictName}」には元の「{ShortcutDefaults.All[id].DisplayName}」のキーを割り当てます)",
                "ショートカットキーの衝突",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { CancelShortcutCapture(); RefreshShortcutsList(); return; }

            var oldBinding = _work.GetShortcut(id);
            _work.SetShortcut(cid, oldBinding.Clone());
        }

        _work.SetShortcut(id, newBinding);
        CancelShortcutCapture();
        RefreshShortcutsList();
    }

    /// <summary>キーボードモード専用ショートカットのキーキャプチャ(2026-07-29要望対応)。修飾キーの
    /// 有無は無視し、物理キーのみを記録する(マウスモード側と違い、Shiftは各操作内部で「範囲選択」の
    /// 補助フラグとして使うため、キー割り当てそのものには含めない)。マウスモード側の一覧とは
    /// 衝突チェックしない(同じ物理キーの重複割り当てを意図的に許容する仕様のため)。</summary>
    private void HandleKeyboardShortcutCaptureKey(KeyEventArgs e, KeyboardModeShortcutId id)
    {
        e.Handled = true;

        if (e.Key == Key.Escape) { CancelKeyboardShortcutCapture(); RefreshKeyboardShortcutsList(); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsPureModifierKey(key)) return; // 修飾キー単独では確定しない、次のキー入力を待つ

        KeyboardModeShortcutId? conflictId = null;
        foreach (var other in Enum.GetValues<KeyboardModeShortcutId>())
        {
            if (other == id) continue;
            if (string.Equals(_work.GetKeyboardModeShortcutKey(other), key.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                conflictId = other;
                break;
            }
        }

        if (conflictId is { } cid)
        {
            var conflictName = KeyboardModeShortcutDefaults.All[cid].DisplayName;
            var result = MessageBox.Show(
                this,
                $"「{key}」は既に(キーボードモード中の)「{conflictName}」に割り当てられています。\n入れ替えてよろしいですか?(「{conflictName}」には元の「{KeyboardModeShortcutDefaults.All[id].DisplayName}」のキーを割り当てます)",
                "ショートカットキーの衝突",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { CancelKeyboardShortcutCapture(); RefreshKeyboardShortcutsList(); return; }

            var oldKey = _work.GetKeyboardModeShortcutKey(id);
            _work.SetKeyboardModeShortcutKey(cid, oldKey);
        }

        _work.SetKeyboardModeShortcutKey(id, key.ToString());
        CancelKeyboardShortcutCapture();
        RefreshKeyboardShortcutsList();
    }

    private static bool IsPureModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

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

    /// <summary>色コード入力欄+ピッカー呼び出し+お気に入り登録ボタンの横並び行を作る(2026-07-23、
    /// 2026-08-08: 「履歴」ボタンを統合カラーピッカー(履歴+お気に入り+HSV視覚選択+RGB/HEX入力、
    /// ColorPickerPopup)呼び出しへ置き換え、「☆登録」ボタン(現在値をお気に入りへ追加)を新設した
    /// (進捗まとめ5-2、お気に入りの色機能)。いずれも_work(環境設定の作業コピー)を対象とするため、
    /// OK確定まで実際の設定へは反映されない(他の環境設定項目と同じ挙動)。押しても見た目の変化が
    /// 分かりづらいとの指摘を受け、登録成功時に「OK」を2秒間表示する(TransientOkFeedback)。</summary>
    private UIElement ColorFieldRow(TextBox colorBox)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var pickerBtn = new Button { Content = "色", Width = 32, Margin = new Thickness(4, 0, 0, 0) };
        pickerBtn.Click += (_, _) => ColorPickerPopup.Show(_work, pickerBtn, colorBox.Text, hex => colorBox.Text = hex);
        var favBtn = new Button { Content = "☆登録", Width = 44, Margin = new Thickness(4, 0, 0, 0) };
        var favOkText = new TextBlock
        {
            Text = "OK", Foreground = Brushes.Green, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        favBtn.Click += (_, _) =>
        {
            if (FavoriteColorPicker.Register(_work, colorBox.Text))
                TransientOkFeedback.Show(favOkText);
        };
        row.Children.Add(colorBox);
        row.Children.Add(pickerBtn);
        row.Children.Add(favBtn);
        row.Children.Add(favOkText);
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
        // 2026-08-08要望対応: レーンラベルヘッダー(speed/boost/BPM等の見出しバー)の表示位置。
        // 従来はChartCanvas上へのオーバーレイ描画のため、スクロールでその位置に来たオブジェクトが
        // ラベルの下に隠れて操作できなくなる不具合があり、別領域(LaneHeaderBar)へ分離した。
        // ChartViewReverse(進行方向の反転)とは独立して選べる。
        p.Children.Add(Label("レーンラベルヘッダー(speed/boost/BPM等の見出しバー)の表示位置:"));
        p.Children.Add(_laneHeaderTop);
        p.Children.Add(_laneHeaderBottom);
        p.Children.Add(_laneHeaderHidden);
        // 2026-08-08要望対応: フレーム数のblankFrame込み表示トグル。マイナスフレームに置いた
        // オブジェクトの表示フレームとdos.txt出力フレームの不一致による勘違いを予防する。
        _showFrameWithBlankFrame.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(_showFrameWithBlankFrame);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(Label("ノート表示", section: true));
        _showImages.Margin = new Thickness(0, 0, 0, 4);
        _showGrid.Margin = new Thickness(0, 0, 0, 4);
        _excludeFreezeEndHighlight.Margin = new Thickness(16, 0, 0, 4); // 強調グリッドの子項目として少し字下げ
        _useNoteColorForHighlight.Margin = new Thickness(16, 0, 0, 4); // 同上
        p.Children.Add(_showImages);
        p.Children.Add(_showGrid);
        p.Children.Add(_excludeFreezeEndHighlight);
        p.Children.Add(_useNoteColorForHighlight);
        p.Children.Add(Label("強調グリッドの太さ(px):"));
        p.Children.Add(_gridWidth);
        p.Children.Add(Label("強調グリッドの色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_gridColor));
        p.Children.Add(_gridPreview);
        _gridColor.TextChanged += (_, _) => _gridPreview.Background = SafeBrush(_gridColor.Text);
        _gridColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _gridColor.Text);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(Label("再生開始フレームライン", section: true));
        p.Children.Add(Label("太さ(px):"));
        p.Children.Add(_startLineWidth);
        p.Children.Add(Label("色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_startLineColor));
        p.Children.Add(_startLinePreview);
        _startLineColor.TextChanged += (_, _) => _startLinePreview.Background = SafeBrush(_startLineColor.Text);
        _startLineColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _startLineColor.Text);
        _carryOverPlaybackStart.Margin = new Thickness(0, 4, 0, 4);
        p.Children.Add(_carryOverPlaybackStart);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
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

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(Label("マクロ範囲マーカー(レーン入替マクロの選択範囲)", section: true));
        p.Children.Add(Label("マーカー線の太さ(px):"));
        p.Children.Add(_macroRangeWidth);
        p.Children.Add(Label("マーカー線・ハイライト帯の色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_macroRangeColor));
        p.Children.Add(_macroRangePreview);
        _macroRangeColor.TextChanged += (_, _) => _macroRangePreview.Background = SafeBrush(_macroRangeColor.Text);
        _macroRangeColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _macroRangeColor.Text);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(Label("タブリンクの背景ノート(右パネル「リンク」タブ)", section: true));
        p.Children.Add(Label("ノートのサイズ比率(1.0=通常サイズ、既定0.85=-15%):"));
        p.Children.Add(_linkedNoteSizeRatio);
        p.Children.Add(Label("ノートの色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_linkedNoteColor));
        p.Children.Add(_linkedNotePreview);
        _linkedNoteColor.TextChanged += (_, _) => _linkedNotePreview.Background = SafeBrush(_linkedNoteColor.Text);
        _linkedNoteColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _linkedNoteColor.Text);
        p.Children.Add(Label("強調表示バーの幅比率(レーン幅に対する倍率、既定0.5=50%):"));
        p.Children.Add(_linkedHighlightWidthRatio);
        p.Children.Add(Label("強調表示バーの高さ(px):"));
        p.Children.Add(_linkedHighlightHeight);
        p.Children.Add(Label("強調表示バーの色(#RRGGBB):"));
        p.Children.Add(ColorFieldRow(_linkedHighlightColor));
        p.Children.Add(_linkedHighlightPreview);
        _linkedHighlightColor.TextChanged += (_, _) => _linkedHighlightPreview.Background = SafeBrush(_linkedHighlightColor.Text);
        _linkedHighlightColor.LostFocus += (_, _) => ColorHistoryPicker.Record(_work, _linkedHighlightColor.Text);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        p.Children.Add(Label("マーカーのコメント表示(仕様書7.4)", section: true));
        _markerFull.Margin = new Thickness(0, 0, 0, 2);
        _markerHead.Margin = new Thickness(0, 0, 0, 2);
        p.Children.Add(_markerFull);
        p.Children.Add(_markerHead);
        p.Children.Add(Label("先頭表示の文字数:"));
        p.Children.Add(_markerHeadChars);

        p.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
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
        _visualTestAcceptNotes.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(_visualTestAcceptNotes);

        _vtAutoReturnEnabled.Margin = new Thickness(0, 12, 0, 4);
        p.Children.Add(_vtAutoReturnEnabled);
        var vtAutoReturnUnitPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 2) };
        vtAutoReturnUnitPanel.Children.Add(_vtAutoReturnByMeasures);
        _vtAutoReturnMeasures.Margin = new Thickness(4, 0, 4, 0);
        vtAutoReturnUnitPanel.Children.Add(_vtAutoReturnMeasures);
        vtAutoReturnUnitPanel.Children.Add(new TextBlock { Text = "小節", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        vtAutoReturnUnitPanel.Children.Add(_vtAutoReturnBySeconds);
        _vtAutoReturnSeconds.Margin = new Thickness(4, 0, 4, 0);
        vtAutoReturnUnitPanel.Children.Add(_vtAutoReturnSeconds);
        vtAutoReturnUnitPanel.Children.Add(new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(vtAutoReturnUnitPanel);
        p.Children.Add(new TextBlock { Text = "戻った時の動作:", Margin = new Thickness(16, 4, 0, 2) });
        _vtAutoReturnContinue.Margin = new Thickness(16, 0, 0, 2);
        p.Children.Add(_vtAutoReturnContinue);
        _vtAutoReturnStop.Margin = new Thickness(16, 0, 0, 0);
        p.Children.Add(_vtAutoReturnStop);

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
        p.Children.Add(Label("再生開始ラインより指定時間だけ手前から再生を始める、いわゆるリードイン(0=無し)。" +
            "この区間にあるノート/フリーズは判定対象外(再生開始ラインから始まる譜面として扱う):"));
        p.Children.Add(_ptStartupWaitMs);

        p.Children.Add(Label("小節線表示", section: true));
        _ptShowMeasureLines.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(_ptShowMeasureLines);

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
        _templateList.SelectionChanged += (_, _) =>
        {
            bool hasSelection = _templateList.SelectedItem is not null;
            _templateEditButton.IsEnabled = hasSelection;
            _templateExportButton.IsEnabled = hasSelection;
        };
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
        _templateExportButton.Click += (_, _) =>
        {
            if (_templateList.SelectedItem is not TemplateListEntry entry) return;
            var dir = AppPaths.FindAssetDir("template");
            if (dir is null) { _error.Text = "templateフォルダが見つかりません"; return; }
            KeyTemplate template;
            try { template = KeyTemplate.Load(entry.Path); }
            catch (Exception ex) { _error.Text = $"テンプレートの読み込みに失敗いたしました: {ex.Message}"; return; }
            new CustomKeyExportWindow(template, dir) { Owner = this }.ShowDialog();
        };
        _templateImportButton.Click += (_, _) =>
        {
            var dir = AppPaths.FindAssetDir("template");
            if (dir is null) { _error.Text = "templateフォルダが見つかりません"; return; }
            var win = new CustomKeyImportWindow(dir) { Owner = this };
            if (win.ShowDialog() != true || win.SavedPath is null) return;
            RefreshTemplateList();
            // 2026-08-03要望対応: 取り込み直後は仮生成した項目(laneId/engineLaneNum等)の確認が
            // 必要なため、そのままテンプレートエディタを開いて確認・編集を促す。取得できなかった
            // 項目(FieldStatus)も併せて渡し、テンプレートエディタ側でハイライト表示させる。
            OpenTemplateEditor(win.SavedPath, win.FieldStatus);
        };
        row.Children.Add(_templateEditButton);
        row.Children.Add(_templateNewButton);
        row.Children.Add(_templateExportButton);
        row.Children.Add(_templateImportButton);
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

    private void OpenTemplateEditor(string? path, DanoniEditor.Core.Import.ImportFieldStatus? pendingIssues = null)
    {
        var dir = AppPaths.FindAssetDir("template");
        if (dir is null) { _error.Text = "templateフォルダが見つかりません"; return; }
        var win = new TemplateEditorWindow(dir, path, pendingIssues) { Owner = this };
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
            Text = "Ctrl+Shift+数字キーへ割り当てる、複数の機能を順番に実行するマクロです。レーン入替マクロと同じく、手順を自分で組み立てる形になっております。選べる手順は今後の要望に応じて増やしていく予定です。",
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
                _error.Text = "キーマクロの値は0以上の数値で入力してください。";
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

    /// <summary>「カラーピッカー」カテゴリ(2026-08-08新設)。お気に入りの色(AppSettings.FavoriteColors)は
    /// 各色欄の「☆登録」ボタンから追加する運用のため、ここでは一覧表示と削除のみを扱う
    /// (登録・削除の役割分担はユーザー確定仕様)。</summary>
    private UIElement BuildColorPickerPanel()
    {
        var p = new StackPanel { Margin = new Thickness(4) };
        p.Children.Add(Label("お気に入りの管理", section: true));
        p.Children.Add(new TextBlock
        {
            Text = "色欄の「☆登録」ボタンで追加したお気に入りの色の一覧です。削除する項目をクリックで選択し"
                 + "(複数選択可)、「削除」を押してください。OKを押すまでは確定しません。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 8),
        });

        RefreshFavoriteColorsList();

        // 2026-08-08要望対応: D&Dで並び替え可能にする(難易度タブの入替えと同じ、掴んだ内容を
        // ドロップ先へ挿入する方式+挿入先を示す線のオーバーレイ)。ListBoxと同じ位置・サイズに
        // インジケータを重ねるため、Gridで包む。
        var favColorListHost = new Grid();
        favColorListHost.Children.Add(_favoriteColorsList);
        favColorListHost.Children.Add(_favColorInsertIndicator);
        p.Children.Add(favColorListHost);

        _favoriteColorsList.AllowDrop = true;
        _favoriteColorsList.PreviewMouseLeftButtonDown += FavoriteColorsList_PreviewMouseLeftButtonDown;
        _favoriteColorsList.PreviewMouseMove += FavoriteColorsList_PreviewMouseMove;
        _favoriteColorsList.PreviewDragOver += FavoriteColorsList_PreviewDragOver;
        _favoriteColorsList.DragLeave += (_, _) => HideFavColorInsertIndicator();
        _favoriteColorsList.Drop += FavoriteColorsList_Drop;

        var deleteBtn = new Button { Content = "削除", Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        deleteBtn.Click += (_, _) =>
        {
            var targets = _favoriteColorsList.SelectedItems.Cast<ListBoxItem>()
                .Select(i => i.Tag as string).Where(t => t is not null).Cast<string>().ToList();
            if (targets.Count == 0) return;
            foreach (var hex in targets) _work.FavoriteColors.Remove(hex);
            RefreshFavoriteColorsList();
        };
        p.Children.Add(deleteBtn);

        return p;
    }

    /// <summary>お気に入り一覧の再描画。色見本+カラーコードを1行とし、Tagへ実データ(hex文字列)を
    /// 保持する(削除処理での選択項目特定用)。0件の場合は選択不可の案内行のみ表示する。</summary>
    private void RefreshFavoriteColorsList()
    {
        _favoriteColorsList.Items.Clear();
        if (_work.FavoriteColors.Count == 0)
        {
            _favoriteColorsList.Items.Add(new ListBoxItem { Content = "登録されているお気に入りはありません。", IsEnabled = false });
            return;
        }

        foreach (var hex in _work.FavoriteColors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Background = SafeBrush(hex),
            });
            row.Children.Add(new TextBlock { Text = hex, VerticalAlignment = VerticalAlignment.Center });
            _favoriteColorsList.Items.Add(new ListBoxItem { Content = row, Tag = hex });
        }
    }

    // --- お気に入り一覧のD&D並び替え(2026-08-08要望対応)。MainWindow.xaml.csのタブ入替
    // (TabControl_PreviewMouseMove等)と同じ考え方: ドラッグ開始→ドラッグ中は挿入予定位置に線を
    // 表示→ドロップ位置(カーソルが各行の上半分/下半分どちらにあるか)へ挿入する。 ---

    private static ListBoxItem? FindListBoxItemAncestor(DependencyObject? d)
    {
        while (d is not null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    /// <summary>ドロップ先index(0〜FavoriteColors.Count)を、カーソルが乗っている行の上半分/下半分の
    /// どちらかで判定する(タブ入替のComputeTabInsertIndexと同じ考え方、横→縦に置き換え)。</summary>
    private int ComputeFavColorInsertIndex(Point posOnListBox, DependencyObject? originalSource)
    {
        int count = _work.FavoriteColors.Count;
        if (count == 0) return 0;

        var item = FindListBoxItemAncestor(originalSource);
        if (item is not null)
        {
            int idx = _favoriteColorsList.ItemContainerGenerator.IndexFromContainer(item);
            if (idx < 0 || idx >= count) return count; // 0件時のプレースホルダ行等は末尾扱い
            double itemTop = item.TranslatePoint(new Point(0, 0), _favoriteColorsList).Y;
            double midY = itemTop + item.ActualHeight / 2.0;
            return posOnListBox.Y < midY ? idx : idx + 1;
        }

        // 行そのものには乗っていない(リスト下部の余白等)。先頭行との位置関係で判定する。
        if (_favoriteColorsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first &&
            posOnListBox.Y < first.TranslatePoint(new Point(0, 0), _favoriteColorsList).Y)
            return 0;
        return count;
    }

    private void ShowFavColorInsertIndicator(int insertIndex)
    {
        int count = _work.FavoriteColors.Count;
        if (count == 0) { HideFavColorInsertIndicator(); return; }

        double y;
        if (insertIndex <= 0)
            y = _favoriteColorsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first
                ? first.TranslatePoint(new Point(0, 0), _favoriteColorsList).Y : 0;
        else if (insertIndex >= count)
            y = _favoriteColorsList.ItemContainerGenerator.ContainerFromIndex(count - 1) is ListBoxItem last
                ? last.TranslatePoint(new Point(0, 0), _favoriteColorsList).Y + last.ActualHeight : 0;
        else
            y = _favoriteColorsList.ItemContainerGenerator.ContainerFromIndex(insertIndex) is ListBoxItem mid
                ? mid.TranslatePoint(new Point(0, 0), _favoriteColorsList).Y : 0;

        _favColorInsertIndicator.Margin = new Thickness(0, y - _favColorInsertIndicator.Height / 2.0, 0, 0);
        _favColorInsertIndicator.Visibility = Visibility.Visible;
    }

    private void HideFavColorInsertIndicator() => _favColorInsertIndicator.Visibility = Visibility.Collapsed;

    private void FavoriteColorsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindListBoxItemAncestor(e.OriginalSource as DependencyObject);
        if (item is null) return;
        int idx = _favoriteColorsList.ItemContainerGenerator.IndexFromContainer(item);
        if (idx < 0 || idx >= _work.FavoriteColors.Count) return; // 0件時のプレースホルダ行はドラッグ対象外
        _favColorDragStartPoint = e.GetPosition(null);
        _favColorDragSourceIndex = idx;
    }

    private void FavoriteColorsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_favColorDragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _favColorDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _favColorDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        int from = _favColorDragSourceIndex;
        _favColorDragSourceIndex = -1;
        DragDrop.DoDragDrop(_favoriteColorsList, from, DragDropEffects.Move);
        // DoDragDropはドラッグ操作が終わるまで戻らないため、終了理由によらずここで確実にインジケータを消す
        // (Drop/DragLeaveの呼び忘れ経路をカバーする保険、タブ入替と同じ考え方)。
        HideFavColorInsertIndicator();
    }

    private void FavoriteColorsList_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int)))
        {
            e.Effects = DragDropEffects.None;
            HideFavColorInsertIndicator();
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int insertIndex = ComputeFavColorInsertIndex(e.GetPosition(_favoriteColorsList), e.OriginalSource as DependencyObject);
        ShowFavColorInsertIndicator(insertIndex);
        e.Handled = true;
    }

    /// <summary>「離した場所の項目と入替え」ではなく「離した位置(カーソルが行の上半分/下半分どちらに
    /// あるか)に挿入」する(タブ入替のDrop処理と同じ方式)。</summary>
    private void FavoriteColorsList_Drop(object sender, DragEventArgs e)
    {
        HideFavColorInsertIndicator();
        if (!e.Data.GetDataPresent(typeof(int))) return;
        int from = (int)e.Data.GetData(typeof(int));
        if (from < 0 || from >= _work.FavoriteColors.Count) return;

        int rawTarget = ComputeFavColorInsertIndex(e.GetPosition(_favoriteColorsList), e.OriginalSource as DependencyObject);
        int to = rawTarget > from ? rawTarget - 1 : rawTarget; // fromを取り除いた後のインデックスへ変換
        if (to == from) return;

        var moved = _work.FavoriteColors[from];
        _work.FavoriteColors.RemoveAt(from);
        _work.FavoriteColors.Insert(to, moved);
        RefreshFavoriteColorsList();
    }

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
        _useNoteColorForHighlight.IsChecked = s.UseNoteColorForHighlight;
        _gridWidth.Text = s.HighlightLineWidth.ToString(CultureInfo.InvariantCulture);
        _gridColor.Text = s.HighlightLineColorHex;
        _gridPreview.Background = SafeBrush(s.HighlightLineColorHex);
        _startLineWidth.Text = s.PlaybackStartLineWidth.ToString(CultureInfo.InvariantCulture);
        _startLineColor.Text = s.PlaybackStartLineColorHex;
        _startLinePreview.Background = SafeBrush(s.PlaybackStartLineColorHex);
        _carryOverPlaybackStart.IsChecked = s.CarryOverPlaybackStartOnTabDuplicate;
        _cursorLineWidth.Text = s.CursorLineWidth.ToString(CultureInfo.InvariantCulture);
        _cursorLineColor.Text = s.CursorLineColorHex;
        _cursorLinePreview.Background = SafeBrush(s.CursorLineColorHex);
        _cursorHighlightWidth.Text = s.CursorHighlightWidth.ToString(CultureInfo.InvariantCulture);
        _cursorHighlightColor.Text = s.CursorHighlightColorHex;
        _cursorHighlightPreview.Background = SafeBrush(s.CursorHighlightColorHex);
        _macroRangeWidth.Text = s.MacroRangeMarkerWidth.ToString(CultureInfo.InvariantCulture);
        _macroRangeColor.Text = s.MacroRangeHighlightColorHex;
        _macroRangePreview.Background = SafeBrush(s.MacroRangeHighlightColorHex);
        _linkedNoteSizeRatio.Text = s.LinkedNoteSizeRatio.ToString(CultureInfo.InvariantCulture);
        _linkedNoteColor.Text = s.LinkedNoteColorHex;
        _linkedNotePreview.Background = SafeBrush(s.LinkedNoteColorHex);
        _linkedHighlightWidthRatio.Text = s.LinkedHighlightWidthRatio.ToString(CultureInfo.InvariantCulture);
        _linkedHighlightHeight.Text = s.LinkedHighlightHeight.ToString(CultureInfo.InvariantCulture);
        _linkedHighlightColor.Text = s.LinkedHighlightColorHex;
        _linkedHighlightPreview.Background = SafeBrush(s.LinkedHighlightColorHex);
        if (_noteSoundFile.Items.Contains(s.NoteSoundFileName)) _noteSoundFile.SelectedItem = s.NoteSoundFileName;
        else if (_noteSoundFile.Items.Count > 0) _noteSoundFile.SelectedIndex = 0;
        _followMode.SelectedIndex = s.VisualTestFollowMode == "smooth" ? 1 : 0;
        _visualTestAcceptNotes.IsChecked = s.VisualTestAcceptNoteInput;
        _vtAutoReturnEnabled.IsChecked = s.VisualTestAutoReturnEnabled;
        _vtAutoReturnByMeasures.IsChecked = s.VisualTestAutoReturnUnit != "seconds";
        _vtAutoReturnBySeconds.IsChecked = s.VisualTestAutoReturnUnit == "seconds";
        _vtAutoReturnMeasures.Text = s.VisualTestAutoReturnMeasures.ToString(CultureInfo.InvariantCulture);
        _vtAutoReturnSeconds.Text = s.VisualTestAutoReturnSeconds.ToString(CultureInfo.InvariantCulture);
        _vtAutoReturnContinue.IsChecked = s.VisualTestAutoReturnContinuePlayback;
        _vtAutoReturnStop.IsChecked = !s.VisualTestAutoReturnContinuePlayback;
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
        _ptShowMeasureLines.IsChecked = s.PlaytestShowMeasureLines;
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
        _laneHeaderTop.IsChecked = s.LaneLabelHeaderPosition != "bottom" && s.LaneLabelHeaderPosition != "hidden"; // 既定"top"扱い
        _laneHeaderBottom.IsChecked = s.LaneLabelHeaderPosition == "bottom";
        _laneHeaderHidden.IsChecked = s.LaneLabelHeaderPosition == "hidden";
        _showFrameWithBlankFrame.IsChecked = s.ShowFrameWithBlankFrame;
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
        { _error.Text = "ノート画像と強調グリッドの両方をOFFにはできません(どちらかはONにしてください)"; return false; }
        if (_ptQuitDelete.IsChecked != true && _ptQuitEscape.IsChecked != true)
        { _error.Text = "プレイテストの中断キーは最低1つはcheckedにしてください"; return false; }
        if (!TryNonNegativeInt(_ptStartupWaitMs.Text, out var ptWait))
        { _error.Text = "プレイテスト起動時ウェイトは0以上の整数(ms)で入力してください"; return false; }
        if (!TryPositive(_gridWidth.Text, out var gw))
        { _error.Text = "強調グリッドの太さは正の数値で入力してください"; return false; }
        if (!TryColor(_gridColor.Text))
        { _error.Text = "強調グリッドの色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_startLineWidth.Text, out var sw))
        { _error.Text = "再生開始ラインの太さは正の数値で入力してください"; return false; }
        if (!TryColor(_startLineColor.Text))
        { _error.Text = "再生開始ラインの色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_cursorLineWidth.Text, out var clw))
        { _error.Text = "カーソルライン(細い線)の太さは正の数値で入力してください"; return false; }
        if (!TryColor(_cursorLineColor.Text))
        { _error.Text = "カーソルライン(細い線)の色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_cursorHighlightWidth.Text, out var chw))
        { _error.Text = "カーソルライン(強調帯)の太さは正の数値で入力してください"; return false; }
        if (!TryColor(_cursorHighlightColor.Text))
        { _error.Text = "カーソルライン(強調帯)の色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_macroRangeWidth.Text, out var mrw))
        { _error.Text = "マクロ範囲マーカーの太さは正の数値で入力してください"; return false; }
        if (!TryColor(_macroRangeColor.Text))
        { _error.Text = "マクロ範囲マーカーの色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_linkedNoteSizeRatio.Text, out var lnsr))
        { _error.Text = "タブリンク背景ノートのサイズ比率は正の数値で入力してください"; return false; }
        if (!TryColor(_linkedNoteColor.Text))
        { _error.Text = "タブリンク背景ノートの色は #RRGGBB 形式で入力してください"; return false; }
        if (!TryPositive(_linkedHighlightWidthRatio.Text, out var lhwr))
        { _error.Text = "タブリンク強調表示バーの幅比率は正の数値で入力してください"; return false; }
        if (!TryPositive(_linkedHighlightHeight.Text, out var lhh))
        { _error.Text = "タブリンク強調表示バーの高さは正の数値で入力してください"; return false; }
        if (!TryColor(_linkedHighlightColor.Text))
        { _error.Text = "タブリンク強調表示バーの色は #RRGGBB 形式で入力してください"; return false; }
        if (!double.TryParse(_ptOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ofs))
        { _error.Text = "調整オフセットは数値で入力してください"; return false; }
        if (!TryPositive(_ptWidthPx.Text, out var ptWidthPx))
        { _error.Text = "プレイテストのウィンドウ幅は正の数値で入力してください"; return false; }
        if (!int.TryParse(_markerHeadChars.Text, out var headChars) || headChars < 1)
        { _error.Text = "マーカー先頭表示の文字数は1以上の整数で入力してください"; return false; }
        if (!TryPositive(_timeInfoFontSize.Text, out var timeInfoFontSize))
        { _error.Text = "時間情報レーンの文字サイズは正の数値で入力してください"; return false; }
        if (!TryPositive(_markerFontSize.Text, out var markerFontSize))
        { _error.Text = "マーカーレーンの文字サイズは正の数値で入力してください"; return false; }
        if (!int.TryParse(_defStartFrame.Text, out var defSf) || defSf < 0)
        { _error.Text = "startFrameは0以上の整数で入力してください"; return false; }
        if (!int.TryParse(_defBlankFrame.Text, out var defBf) || defBf < 0)
        { _error.Text = "blankFrameは0以上の整数で入力してください"; return false; }
        if (!int.TryParse(_defFrzAttempt.Text, out var defFa) || defFa < 0)
        { _error.Text = "frzAttemptは0以上の整数で入力してください"; return false; }
        if (!TryPositive(_defBpm.Text, out var defBpm))
        { _error.Text = "BPM初期値は正の数値で入力してください"; return false; }
        if (!int.TryParse(_undoSize.Text, out var undoSize) || undoSize < 1)
        { _error.Text = "Undo履歴件数は1以上の整数で入力してください"; return false; }
        if (!TryPositive(_autoSaveInterval.Text, out var autoSaveInterval))
        { _error.Text = "自動保存の間隔は正の数値(分)で入力してください"; return false; }
        if (!int.TryParse(_colorHistLimit.Text, out var colorLimit) || colorLimit < 1)
        { _error.Text = "色履歴の上限件数は1以上の整数で入力してください"; return false; }
        if (!int.TryParse(_recentFilesLimit.Text, out var recentLimit) || recentLimit < 1)
        { _error.Text = "最近開いたファイルの保持件数は1以上の整数で入力してください"; return false; }
        if (!TryPositive(_kbdThreshold.Text, out var kbdThreshold))
        { _error.Text = "同時押し判定の閾値は正の数値で入力してください"; return false; }
        if (_musicUrlEnabled.IsChecked == true && string.IsNullOrWhiteSpace(_musicUrlFolder.Text))
        { _error.Text = "musicURLからの楽曲取得をONにする場合、楽曲フォルダを指定してください"; return false; }
        if (!int.TryParse(_vtAutoReturnMeasures.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vtAutoReturnMeasures) || vtAutoReturnMeasures < 1)
        { _error.Text = "目視テスト自動復帰の小節数は1以上の整数で入力してください"; return false; }
        if (!TryPositive(_vtAutoReturnSeconds.Text, out var vtAutoReturnSeconds))
        { _error.Text = "目視テスト自動復帰の秒数は正の数値で入力してください"; return false; }

        _work.ShowNoteImages = _showImages.IsChecked == true;
        _work.ShowHighlightGrid = _showGrid.IsChecked == true;
        _work.ExcludeFreezeEndFromHighlight = _excludeFreezeEndHighlight.IsChecked == true;
        _work.UseNoteColorForHighlight = _useNoteColorForHighlight.IsChecked == true;
        _work.HighlightLineWidth = gw;
        _work.HighlightLineColorHex = _gridColor.Text;
        _work.PlaybackStartLineWidth = sw;
        _work.PlaybackStartLineColorHex = _startLineColor.Text;
        _work.CarryOverPlaybackStartOnTabDuplicate = _carryOverPlaybackStart.IsChecked == true;
        _work.CursorLineWidth = clw;
        _work.CursorLineColorHex = _cursorLineColor.Text;
        _work.CursorHighlightWidth = chw;
        _work.CursorHighlightColorHex = _cursorHighlightColor.Text;
        _work.MacroRangeMarkerWidth = mrw;
        _work.MacroRangeHighlightColorHex = _macroRangeColor.Text;
        _work.LinkedNoteSizeRatio = lnsr;
        _work.LinkedNoteColorHex = _linkedNoteColor.Text;
        _work.LinkedHighlightWidthRatio = lhwr;
        _work.LinkedHighlightHeight = lhh;
        _work.LinkedHighlightColorHex = _linkedHighlightColor.Text;
        if (_noteSoundFile.Items.Count > 0 && _noteSoundFile.SelectedItem is string ns) _work.NoteSoundFileName = ns;
        _work.VisualTestFollowMode = _followMode.SelectedIndex == 1 ? "smooth" : "page";
        _work.VisualTestAcceptNoteInput = _visualTestAcceptNotes.IsChecked == true;
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
        _work.PlaytestShowMeasureLines = _ptShowMeasureLines.IsChecked == true;
        _work.PlaytestPatternByKeyType = _ptPatternByKeyType.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value.SelectedIndex));
        _work.PlaytestReverseByKeyType = _ptReverseByKeyType.ToDictionary(kv => kv.Key, kv => kv.Value.IsChecked == true);
        _work.MarkerCommentFull = _markerFull.IsChecked == true;
        _work.MarkerCommentHeadChars = headChars;
        _work.TimeInfoFontSize = timeInfoFontSize;
        _work.MarkerFontSize = markerFontSize;
        _work.ChartViewReverse = _chartViewReverse.IsChecked == true;
        _work.LaneLabelHeaderPosition = _laneHeaderBottom.IsChecked == true ? "bottom"
            : _laneHeaderHidden.IsChecked == true ? "hidden" : "top";
        _work.ShowFrameWithBlankFrame = _showFrameWithBlankFrame.IsChecked == true;
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
        _work.VisualTestAutoReturnEnabled = _vtAutoReturnEnabled.IsChecked == true;
        _work.VisualTestAutoReturnUnit = _vtAutoReturnBySeconds.IsChecked == true ? "seconds" : "measures";
        _work.VisualTestAutoReturnMeasures = vtAutoReturnMeasures;
        _work.VisualTestAutoReturnSeconds = vtAutoReturnSeconds;
        _work.VisualTestAutoReturnContinuePlayback = _vtAutoReturnContinue.IsChecked == true;
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
