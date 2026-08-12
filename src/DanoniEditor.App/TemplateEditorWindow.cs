using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// キー種テンプレート(temp_{keyTypeId}.json)の作成・編集ウィンドウ(2026-07-26)。
/// 環境設定「テンプレート」カテゴリから開く。既存ファイルを渡せば編集、nullなら新規作成
/// (同じウィンドウを引数で使い分ける)。
/// - 左パネル: テンプレート全体設定(keyTypeId等)。keyCountはレーンタブ数から自動算出。
/// - 中央: レーン(LaneDef)1件ごとのタブ。タブ見出し=laneId。D&Dで並び替え可能で、
///   保存時にタブの左からの順番をdisplayOrderとして自動採番する(一番左=0)。
/// - 上部: 現在のタブ順でnoteGraphicの画像を並べたプレビュー(補助表示、並び替えに追従)。
/// </summary>
internal sealed class TemplateEditorWindow : Window
{
    private readonly string _templateDir;
    private readonly string? _originalPath; // null = 新規作成
    private readonly List<string> _noteGraphicOptions;

    // --- 取り込み時の未確定項目ハイライト(2026-08-03要望対応) ---
    // カスタムキー定義インポート直後にこのウィンドウを開く場合のみ渡される。値そのものは
    // LoadFromで各VMのUnresolvedFieldsへ展開し、以降はこのフィールドを直接参照しない。
    private readonly ImportFieldStatus? _pendingIssues;
    /// <summary>プログラム側でコントロールのText/SelectedItemを書き戻している間はtrueにし、
    /// TextChanged等のハンドラが「ユーザーが入力した」と誤認してUnresolvedFieldsを
    /// クリアしてしまわないようにする(パターン切替時の表示同期用)。</summary>
    private bool _suppressFieldChangeTracking;
    private static readonly Brush UnresolvedHighlightBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x44, 0x00));

    // --- 全体設定(左パネル) ---
    private readonly TextBox _keyTypeId = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 10 };
    private readonly TextBox _keyTypeName = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _comment = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    // 2026-08-02要望対応: "@"/"["/"]"のようにJIS配列/US配列で対応する本体エンジンコードが食い違う
    // 記号キーの解釈をテンプレート単位で明示するための選択欄(カスタムキーエクスポート時に参照される)。
    private readonly ComboBox _keyboardLayout = new()
    {
        Width = 180, HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[] { KeyboardLayout.Us, KeyboardLayout.Jis }, SelectedIndex = 0,
    };
    private readonly TextBox _blank = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _divideCnt = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _posMax = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _laneCountLabel = new() { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray };

    // --- キーパターン(2026-07-26e要望対応、danoniplus本家の「キーパターン」概念への対応) ---
    private readonly ComboBox _patternCombo = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _addPatternButton = new() { Content = "パターン追加", Width = 95, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _removePatternButton = new() { Content = "パターン削除", Width = 95, IsEnabled = false };
    private readonly TextBox _patternNameBox = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly PatternVM _basePattern = new();
    private readonly List<PatternVM> _extraPatterns = [];
    private int _currentPatternIndex;
    private bool _suppressPatternComboEvent;

    private readonly TabControl _laneTabs = new();
    // 2026-08-08要望対応: レーンタブのD&D並び替えを「離した場所と入替え」から「離した位置(左右どちらの
    // 半分か)に挿入」+挿入先を示す縦線オーバーレイへ変更(MainWindow.xaml.csの難易度タブ入替と同じ方式)。
    private readonly Border _laneTabsInsertIndicator = new()
    {
        Width = 2, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private readonly PreviewStripElement _previewStrip;
    private readonly StackPanel _fujiLaneNumStrip = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _removeLaneButton = new() { Content = "レーン削除", Width = 90, IsEnabled = false };

    // --- レーンタブのD&D並び替え ---
    private Point _tabDragStartPoint;
    private int _tabDragSourceIndex = -1;

    // --- keyboardInputKeysのキー入力キャプチャ(2026-07-26、「入力開始→キー押下→指定」フロー) ---
    private bool _capturingKey;
    private TextBox? _captureTargetBox;
    private Button? _captureButton;

    /// <summary>保存に成功した場合の保存先パス(呼び出し元がテンプレート一覧を再読込する用)</summary>
    public string? SavedPath { get; private set; }

    /// <summary>保存に成功した場合のkeyTypeId(呼び出し元がTemplateRepositoryのキャッシュを破棄する用)。
    /// リネーム(keyTypeId変更)時は旧IDも合わせて破棄する必要があるため、旧IDは別途OriginalKeyTypeIdで返す。</summary>
    public string? SavedKeyTypeId { get; private set; }

    /// <summary>編集開始時点のkeyTypeId(新規作成時はnull)</summary>
    public string? OriginalKeyTypeId { get; }

    public TemplateEditorWindow(string templateDir, string? existingPath, ImportFieldStatus? pendingIssues = null)
    {
        _templateDir = templateDir;
        _originalPath = existingPath;
        _pendingIssues = pendingIssues;
        _noteGraphicOptions = LoadNoteGraphicOptions();

        Title = existingPath is null ? "テンプレート新規作成" : $"テンプレート編集 - {Path.GetFileName(existingPath)}";
        Width = 900;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        _previewStrip = new PreviewStripElement(this);

        _keyTypeId.PreviewTextInput += (_, e) => { if (!e.Text.All(char.IsAsciiLetterOrDigit)) e.Handled = true; };
        DataObject.AddPastingHandler(_keyTypeId, (_, e) =>
        {
            if (e.DataObject.GetDataPresent(DataFormats.Text) &&
                ((string)e.DataObject.GetData(DataFormats.Text)!).All(char.IsAsciiLetterOrDigit)) return;
            e.CancelCommand();
        });

        // 2026-07-26: keyboardInputKeysのキー入力キャプチャ用。Window全体でPreview段階(トンネリング、
        // 子コントロールより先)に捕まえることで、キャプチャ中はEnter/EscapeがSave/Cancelボタンの
        // 既定動作(IsDefault/IsCancel)に奪われないようにする。
        PreviewKeyDown += Window_PreviewKeyDown_KeyCapture;

        _laneTabs.AllowDrop = true;
        _laneTabs.PreviewMouseLeftButtonDown += LaneTabs_PreviewMouseLeftButtonDown;
        _laneTabs.PreviewMouseMove += LaneTabs_PreviewMouseMove;
        _laneTabs.PreviewDragOver += LaneTabs_PreviewDragOver;
        _laneTabs.DragLeave += (_, _) => HideLaneTabsInsertIndicator();
        _laneTabs.Drop += LaneTabs_Drop;
        _laneTabs.SelectionChanged += (_, _) => _removeLaneButton.IsEnabled = _laneTabs.SelectedItem is not null;

        // 2026-07-26e: blank/divideCnt/posMaxはパターンごとに独立するため、入力の都度
        // 選択中パターン(ActivePattern)へ即座に書き込む(パターン切替時はLoadPatternFieldsIntoUIで
        // 表示側を差し替えるだけで、書き込み先は常に「その時点のActivePattern」なので同期漏れが無い)。
        _blank.TextChanged += (_, _) =>
        {
            ActivePattern.Blank = _blank.Text;
            if (_suppressFieldChangeTracking) return;
            ActivePattern.UnresolvedFields.Remove("blank");
            SetHighlight(_blank, false);
        };
        _divideCnt.TextChanged += (_, _) =>
        {
            ActivePattern.DivideCnt = _divideCnt.Text;
            if (_suppressFieldChangeTracking) return;
            ActivePattern.UnresolvedFields.Remove("divideCnt");
            SetHighlight(_divideCnt, false);
        };
        _posMax.TextChanged += (_, _) =>
        {
            ActivePattern.PosMax = _posMax.Text;
            if (_suppressFieldChangeTracking) return;
            ActivePattern.UnresolvedFields.Remove("posMax");
            SetHighlight(_posMax, false);
        };

        _patternCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressPatternComboEvent) return;
            _currentPatternIndex = Math.Max(0, _patternCombo.SelectedIndex);
            LoadPatternFieldsIntoUI();
            UpdatePatternButtonsState();
        };
        _addPatternButton.Click += AddPattern_Click;
        _removePatternButton.Click += RemovePattern_Click;
        _patternNameBox.TextChanged += (_, _) =>
        {
            if (_currentPatternIndex <= 0 || _currentPatternIndex > _extraPatterns.Count) return;
            _extraPatterns[_currentPatternIndex - 1].Name = _patternNameBox.Text;
        };

        // --- レイアウト ---
        var root = new DockPanel();

        var saveButton = new Button { Content = "保存", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        saveButton.Click += Save_Click;
        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttonsPanel.Children.Add(saveButton);
        buttonsPanel.Children.Add(cancelButton);
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        root.Children.Add(buttonsPanel);

        var errBorder = new Border { Child = _error, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(errBorder, Dock.Bottom);
        root.Children.Add(errBorder);

        // 2026-07-26: プレビュー帯の下にfujiLaneNum入力欄の帯を追加(要望)。同じScrollViewerに
        // 縦に並べて入れることで、横スクロールが両者で常に同期する。
        var previewAndFujiPanel = new StackPanel { Orientation = Orientation.Vertical };
        previewAndFujiPanel.Children.Add(_previewStrip);
        var fujiLabel = new TextBlock
        {
            Text = "fujiLaneNum(空欄可、未指定時はdisplayOrderを使用):",
            Foreground = Brushes.LightGray,
            FontSize = 10,
            Margin = new Thickness(4, 2, 0, 2),
        };
        previewAndFujiPanel.Children.Add(fujiLabel);
        previewAndFujiPanel.Children.Add(_fujiLaneNumStrip);

        var previewScroll = new ScrollViewer
        {
            Content = previewAndFujiPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 130,
            Background = Brushes.Black, // 2026-07-26: 明るい色見本(#ccffff/#ffffff等)の視認性対応
        };
        var previewBorder = new Border
        {
            Child = previewScroll,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4, 8, 4),
        };
        DockPanel.SetDock(previewBorder, Dock.Top);
        root.Children.Add(previewBorder);

        var leftPanel = (FrameworkElement)BuildLeftPanel();
        leftPanel.Width = 240;
        DockPanel.SetDock(leftPanel, Dock.Left);
        root.Children.Add(leftPanel);

        var centerPanel = new DockPanel();
        var addLaneButton = new Button { Content = "レーン追加", Width = 90, Margin = new Thickness(4, 4, 4, 4) };
        addLaneButton.Click += (_, _) =>
        {
            int n = _laneTabs.Items.Count;
            AddLaneTab(new LaneEditVM { LaneId = $"lane{n}", DataName = $"lane{n}" });
        };
        _removeLaneButton.Margin = new Thickness(0, 4, 4, 4);
        _removeLaneButton.Click += RemoveLane_Click;
        var laneButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        laneButtonsPanel.Children.Add(addLaneButton);
        laneButtonsPanel.Children.Add(_removeLaneButton);
        DockPanel.SetDock(laneButtonsPanel, Dock.Top);
        centerPanel.Children.Add(laneButtonsPanel);
        var laneTabsHost = new Grid();
        laneTabsHost.Children.Add(_laneTabs);
        laneTabsHost.Children.Add(_laneTabsInsertIndicator);
        centerPanel.Children.Add(laneTabsHost);
        root.Children.Add(centerPanel);

        Content = root;

        if (existingPath is not null)
        {
            var tpl = KeyTemplate.Load(existingPath);
            OriginalKeyTypeId = tpl.KeyTypeId;
            LoadFrom(tpl);
        }
        else
        {
            _blank.Text = "50";
            _divideCnt.Text = "0";
            _posMax.Text = "0";
        }
        RefreshPatternCombo();
        UpdateLaneCountLabel();
        RefreshPreview();
    }

    // =====================================================================
    // 左パネル(全体設定)
    // =====================================================================

    private UIElement BuildLeftPanel()
    {
        var p = new StackPanel { Margin = new Thickness(8) };
        p.Children.Add(Label("全体設定", section: true));
        p.Children.Add(Label("keyTypeId(半角英数1〜10文字、ファイル名を決定):"));
        p.Children.Add(_keyTypeId);
        p.Children.Add(Label("keyTypeName:"));
        p.Children.Add(_keyTypeName);
        p.Children.Add(Label("comment:"));
        p.Children.Add(_comment);
        p.Children.Add(Label("キーボード配列(記号キー\"@\"/\"[\"/\"]\"のエクスポート先を決定):"));
        p.Children.Add(_keyboardLayout);

        p.Children.Add(Label("キーパターン(以下の項目はパターンごとに独立)", section: true));
        p.Children.Add(_patternCombo);
        var patternButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        patternButtons.Children.Add(_addPatternButton);
        patternButtons.Children.Add(_removePatternButton);
        p.Children.Add(patternButtons);
        p.Children.Add(Label("パターン名(任意、既定パターンには設定不可):"));
        p.Children.Add(_patternNameBox);

        p.Children.Add(Label("blank(px):"));
        p.Children.Add(_blank);
        p.Children.Add(Label("divideCnt:"));
        p.Children.Add(_divideCnt);
        p.Children.Add(Label("posMax:"));
        p.Children.Add(_posMax);
        p.Children.Add(_laneCountLabel);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>2026-08-03要望対応: 取り込みで確定できなかった項目のコントロールを目立つ背景色で
    /// ハイライトする(解除時はコントロールの既定スタイルへ戻す)。</summary>
    private static void SetHighlight(Control? control, bool unresolved)
    {
        if (control is null) return;
        if (unresolved) control.Background = UnresolvedHighlightBrush;
        else control.ClearValue(Control.BackgroundProperty);
    }

    private static TextBlock Label(string text, bool section = false) => new()
    {
        Text = text,
        FontWeight = section ? FontWeights.Bold : FontWeights.Normal,
        Margin = section ? new Thickness(0, 0, 0, 6) : new Thickness(0, 8, 0, 2),
    };

    private void UpdateLaneCountLabel() => _laneCountLabel.Text = $"keyCount(自動算出): {_laneTabs.Items.Count}";

    // =====================================================================
    // レーンタブ
    // =====================================================================

    /// <summary>LaneDefの手編集用ミュータブルコピー。数値項目も文字列のまま保持し、保存時にまとめて検証する
    /// (入力中の一時的な不正値でエラーダイアログが出ないようにするため)。fujiLaneNumは
    /// プレビュー帯の下の専用欄で編集する(2026-07-26、レーンタブ本体の入力欄一覧には含めない)。
    /// 空欄は「未指定(displayOrderを使う)」を表す。</summary>
    private sealed class LaneEditVM
    {
        public string LaneId = "";
        public string DataName = "";
        public string FrzDataNameOverride = "";
        public string KeyboardInputKeys = "";
        public string EngineLaneNum = "0";
        public string FujiLaneNumText = "";

        /// <summary>パターン0(既定)のプレゼンテーション値(2026-07-26e)</summary>
        public LanePatternFieldsVM Base = new();
        /// <summary>パターン1以降の上書き値。インデックス0がパターン1に対応する(2026-07-26e)。</summary>
        public List<LanePatternFieldsVM> PatternOverrides = [];

        // --- UI参照(パターン切替時の表示更新用、保存対象データではない、2026-07-26e) ---
        public TextBox? KeyAssignBox;
        public TextBox? ColorGroupBox;
        public TextBox? PosIndexBox;
        public ComboBox? ScrollDirCombo;
        public ComboBox? NoteGraphicCombo;
        public TextBox? RotationAngleBox;
    }

    /// <summary>キーパターンごとに独立するレーンのプレゼンテーション値(2026-07-26e)。
    /// danoniplus本家のkeyCtrlX_Y/colorX_Y/posX_Y/scrollDirX_Y/stepRtnX_Y相当。</summary>
    private sealed class LanePatternFieldsVM
    {
        public string KeyAssign = "";
        public string ColorGroup = "0";
        public string PosIndex = "0";
        public string ScrollDirection = "down";
        public string NoteGraphic = "arrow";
        public string RotationAngle = "0";

        /// <summary>2026-08-03要望対応: カスタムキー定義の取り込みで確定できず仮の値を入れた項目名
        /// ("colorGroup"/"posIndex"/"scrollDirection"/"noteGraphic"/"rotationAngle")の集合。
        /// ハイライト表示・保存時の入力必須チェックに使う。ユーザーが該当欄を編集すると除去される。</summary>
        public HashSet<string> UnresolvedFields { get; } = [];
    }

    /// <summary>キーパターン1件分のテンプレートレベル設定(2026-07-26e)。blank/divideCnt/posMaxは
    /// danoniplus本家でもパターンごとに変わり得るため、ここに持たせる。</summary>
    private sealed class PatternVM
    {
        public string Name = "";
        public string Blank = "50";
        public string DivideCnt = "0";
        public string PosMax = "0";

        /// <summary>2026-08-03要望対応: 取り込みで確定できず仮の値を入れた項目名
        /// ("blank"/"divideCnt"/"posMax")の集合。</summary>
        public HashSet<string> UnresolvedFields { get; } = [];
    }

    private static LaneEditVM FromLaneDef(LaneDef d) => new()
    {
        LaneId = d.LaneId,
        DataName = d.DataName,
        FrzDataNameOverride = d.FrzDataNameOverride ?? "",
        KeyboardInputKeys = string.Join("/", d.KeyboardInputKeys),
        EngineLaneNum = d.EngineLaneNum.ToString(CultureInfo.InvariantCulture),
        FujiLaneNumText = d.FujiLaneNum?.ToString(CultureInfo.InvariantCulture) ?? "",
        Base = new LanePatternFieldsVM
        {
            KeyAssign = string.Join("/", d.KeyAssign),
            ColorGroup = d.ColorGroup.ToString(CultureInfo.InvariantCulture),
            PosIndex = d.PosIndex.ToString(CultureInfo.InvariantCulture),
            ScrollDirection = d.ScrollDirection,
            NoteGraphic = d.NoteGraphic,
            RotationAngle = d.RotationAngle.ToString(CultureInfo.InvariantCulture),
        },
    };

    private static LanePatternFieldsVM FromOverride(LanePatternOverride o) => new()
    {
        KeyAssign = string.Join("/", o.KeyAssign),
        ColorGroup = o.ColorGroup.ToString(CultureInfo.InvariantCulture),
        PosIndex = o.PosIndex.ToString(CultureInfo.InvariantCulture),
        ScrollDirection = o.ScrollDirection,
        NoteGraphic = o.NoteGraphic,
        RotationAngle = o.RotationAngle.ToString(CultureInfo.InvariantCulture),
    };

    private static string HeaderText(LaneEditVM vm) => string.IsNullOrWhiteSpace(vm.LaneId) ? "(無名)" : vm.LaneId;

    /// <summary>現在選択中パターンのテンプレートレベル設定(blank/divideCnt/posMax)を返す(2026-07-26e)。</summary>
    private PatternVM ActivePattern =>
        _currentPatternIndex <= 0 || _currentPatternIndex > _extraPatterns.Count
            ? _basePattern : _extraPatterns[_currentPatternIndex - 1];

    /// <summary>指定レーンの、現在選択中パターンにおけるプレゼンテーション値を返す(2026-07-26e)。</summary>
    private LanePatternFieldsVM ActiveFields(LaneEditVM vm) =>
        _currentPatternIndex <= 0 || _currentPatternIndex > vm.PatternOverrides.Count
            ? vm.Base : vm.PatternOverrides[_currentPatternIndex - 1];

    private void AddLaneTab(LaneEditVM vm)
    {
        // 2026-07-26e: 既存パターン数に満たない場合は既定値で埋めておく(新規レーン追加時)。
        // LoadFromから呼ばれる場合は既に全パターン分埋まっているため、この処理は何もしない。
        while (vm.PatternOverrides.Count < _extraPatterns.Count) vm.PatternOverrides.Add(new LanePatternFieldsVM());

        var tab = BuildLaneTab(vm);
        _laneTabs.Items.Add(tab);
        _laneTabs.SelectedItem = tab;
        UpdateLaneCountLabel();
        RefreshPreview();
    }

    private TabItem BuildLaneTab(LaneEditVM vm)
    {
        var tab = new TabItem { Tag = vm, Header = HeaderText(vm) };
        var grid = new StackPanel { Margin = new Thickness(8) };

        TextBox AddTextRow(string label, string initial, Action<string> onChange)
        {
            grid.Children.Add(Label(label));
            var box = new TextBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Text = initial };
            box.TextChanged += (_, _) => onChange(box.Text);
            grid.Children.Add(box);
            return box;
        }

        // 2026-07-26: 「入力開始」ボタン付きのテキスト行。ボタンを押してから任意のキーを押下すると、
        // Window_PreviewKeyDown_KeyCaptureがそのキーをラベルへ変換してこの欄へ追加する
        // (テンキーのキーも含め、KeyLabelMapper.LabelForKeyが対応するキーなら何でも拾える)。
        // 手入力での直接編集(「/」区切りで複数指定等)も従来通り可能。
        // 2026-08-02要望対応: 以前はkeyboardInputKeys欄だけがこのキャプチャUIを持っていたが、
        // 「keyAssignにテンキーを指定できるか」という質問への対応として、keyAssign欄にも
        // 同じキャプチャUIを追加した(手入力で"Num5"等と直接打ち込む方法は元々可能だったが、
        // 実キー押下だけで指定できた方が分かりやすいため)。呼び出し元でTextBoxをvmの
        // UI参照フィールドへ保持できるよう、AddTextRowと同様にTextBoxを返す。
        TextBox AddKeyCaptureTextRow(string label, string initial, Action<string> onChange)
        {
            grid.Children.Add(Label(label));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new TextBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Text = initial };
            box.TextChanged += (_, _) => onChange(box.Text);
            var captureButton = new Button { Content = "入力開始", Width = 90, Margin = new Thickness(6, 0, 0, 0) };
            captureButton.Click += (_, _) => BeginKeyCapture(captureButton, box);
            row.Children.Add(box);
            row.Children.Add(captureButton);
            grid.Children.Add(row);
            return box;
        }

        AddTextRow("laneId:", vm.LaneId, v => { vm.LaneId = v; tab.Header = HeaderText(vm); RefreshPreview(); });
        AddTextRow("dataName:", vm.DataName, v => vm.DataName = v);
        AddTextRow("frzDataNameOverride(空欄=frz+dataNameの規定通り):", vm.FrzDataNameOverride, v => vm.FrzDataNameOverride = v);
        // 2026-07-26e: keyAssign以下6項目はキーパターンごとに独立するため、書き込み先を
        // ActiveFields(vm)経由(=現在選択中パターン)にする。パターン切替時はLoadPatternFieldsIntoUIが
        // 各コントロールの表示だけを差し替え、コントロール自体は使い回す(vmにUI参照を保持)。
        vm.KeyAssignBox = AddKeyCaptureTextRow("keyAssign(複数キーは/区切り、例 E/R。テンキーは「入力開始」→" +
            "テンキー押下でも指定可。パターンごとに独立):",
            ActiveFields(vm).KeyAssign, v => ActiveFields(vm).KeyAssign = v);
        AddKeyCaptureTextRow("keyboardInputKeys(空欄可、複数は/区切り。「入力開始」→実キー押下でも追加可):",
            vm.KeyboardInputKeys, v => vm.KeyboardInputKeys = v);
        vm.ColorGroupBox = AddTextRow("colorGroup(整数、パターンごとに独立):",
            ActiveFields(vm).ColorGroup, v =>
            {
                ActiveFields(vm).ColorGroup = v;
                if (!_suppressFieldChangeTracking) { ActiveFields(vm).UnresolvedFields.Remove("colorGroup"); SetHighlight(vm.ColorGroupBox, false); }
                RefreshPreview();
            });
        vm.PosIndexBox = AddTextRow("posIndex(数値、小数可、パターンごとに独立):",
            ActiveFields(vm).PosIndex, v =>
            {
                ActiveFields(vm).PosIndex = v;
                if (!_suppressFieldChangeTracking) { ActiveFields(vm).UnresolvedFields.Remove("posIndex"); SetHighlight(vm.PosIndexBox, false); }
            });

        grid.Children.Add(Label("scrollDirection(パターンごとに独立):"));
        var scrollCombo = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        scrollCombo.Items.Add("up");
        scrollCombo.Items.Add("down");
        scrollCombo.SelectedItem = ActiveFields(vm).ScrollDirection is "up" or "down" ? ActiveFields(vm).ScrollDirection : "down";
        scrollCombo.SelectionChanged += (_, _) =>
        {
            ActiveFields(vm).ScrollDirection = scrollCombo.SelectedItem as string ?? "down";
            if (!_suppressFieldChangeTracking) { ActiveFields(vm).UnresolvedFields.Remove("scrollDirection"); SetHighlight(scrollCombo, false); }
        };
        grid.Children.Add(scrollCombo);
        vm.ScrollDirCombo = scrollCombo;

        grid.Children.Add(Label("noteGraphic(パターンごとに独立):"));
        var graphicCombo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var g in _noteGraphicOptions) graphicCombo.Items.Add(g);
        EnsureGraphicOption(graphicCombo, ActiveFields(vm).NoteGraphic);
        graphicCombo.SelectedItem = ActiveFields(vm).NoteGraphic;
        graphicCombo.SelectionChanged += (_, _) =>
        {
            ActiveFields(vm).NoteGraphic = graphicCombo.SelectedItem as string ?? ActiveFields(vm).NoteGraphic;
            if (!_suppressFieldChangeTracking) { ActiveFields(vm).UnresolvedFields.Remove("noteGraphic"); SetHighlight(graphicCombo, false); }
            RefreshPreview();
        };
        grid.Children.Add(graphicCombo);
        vm.NoteGraphicCombo = graphicCombo;

        vm.RotationAngleBox = AddTextRow("rotationAngle(度、パターンごとに独立):",
            ActiveFields(vm).RotationAngle, v =>
            {
                ActiveFields(vm).RotationAngle = v;
                if (!_suppressFieldChangeTracking) { ActiveFields(vm).UnresolvedFields.Remove("rotationAngle"); SetHighlight(vm.RotationAngleBox, false); }
                RefreshPreview();
            });
        AddTextRow("engineLaneNum(整数、本体エンジンの内部レーン番号):", vm.EngineLaneNum, v => vm.EngineLaneNum = v);

        tab.Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return tab;
    }

    // =====================================================================
    // キーパターン切替(2026-07-26e要望対応)
    // =====================================================================

    private static void EnsureGraphicOption(ComboBox combo, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!combo.Items.Cast<string>().Contains(value)) combo.Items.Add(value);
    }

    /// <summary>パターン選択コンボの選択肢を作り直す(件数変化時に呼ぶ)。選択中パターン番号は
    /// 可能な限り維持し、範囲外なら既定パターン(0)へフォールバックする。呼び出し後、
    /// 表示コントロールも選択中パターンの値へ同期する。</summary>
    private void RefreshPatternCombo()
    {
        _suppressPatternComboEvent = true;
        _patternCombo.Items.Clear();
        _patternCombo.Items.Add("パターン0(既定)");
        for (int i = 0; i < _extraPatterns.Count; i++)
        {
            var name = _extraPatterns[i].Name;
            _patternCombo.Items.Add(string.IsNullOrWhiteSpace(name) ? $"パターン{i + 1}" : $"パターン{i + 1}: {name}");
        }
        _currentPatternIndex = Math.Clamp(_currentPatternIndex, 0, _extraPatterns.Count);
        _patternCombo.SelectedIndex = _currentPatternIndex;
        _suppressPatternComboEvent = false;

        LoadPatternFieldsIntoUI();
        UpdatePatternButtonsState();
    }

    private void UpdatePatternButtonsState()
    {
        _removePatternButton.IsEnabled = _currentPatternIndex > 0;
        _patternNameBox.IsEnabled = _currentPatternIndex > 0;
        _patternNameBox.Text = _currentPatternIndex > 0 ? _extraPatterns[_currentPatternIndex - 1].Name : "";
    }

    /// <summary>選択中パターンの値を各コントロールへ反映する(パターン切替の都度呼ぶ)。
    /// コントロールのText/SelectedItemを書き換えると対応するイベントが再発火するが、
    /// 書き込み先は常に「その時点のActivePattern/ActiveFields」なので同じ値を書き戻すだけで
    /// 実害は無い(2026-07-26e)。</summary>
    private void LoadPatternFieldsIntoUI()
    {
        _suppressFieldChangeTracking = true;
        var tp = ActivePattern;
        _blank.Text = tp.Blank;
        SetHighlight(_blank, tp.UnresolvedFields.Contains("blank"));
        _divideCnt.Text = tp.DivideCnt;
        SetHighlight(_divideCnt, tp.UnresolvedFields.Contains("divideCnt"));
        _posMax.Text = tp.PosMax;
        SetHighlight(_posMax, tp.UnresolvedFields.Contains("posMax"));

        foreach (TabItem item in _laneTabs.Items)
        {
            var vm = (LaneEditVM)item.Tag!;
            var f = ActiveFields(vm);
            if (vm.KeyAssignBox is not null) vm.KeyAssignBox.Text = f.KeyAssign;
            if (vm.ColorGroupBox is not null) { vm.ColorGroupBox.Text = f.ColorGroup; SetHighlight(vm.ColorGroupBox, f.UnresolvedFields.Contains("colorGroup")); }
            if (vm.PosIndexBox is not null) { vm.PosIndexBox.Text = f.PosIndex; SetHighlight(vm.PosIndexBox, f.UnresolvedFields.Contains("posIndex")); }
            if (vm.ScrollDirCombo is not null) { vm.ScrollDirCombo.SelectedItem = f.ScrollDirection; SetHighlight(vm.ScrollDirCombo, f.UnresolvedFields.Contains("scrollDirection")); }
            if (vm.NoteGraphicCombo is not null)
            {
                EnsureGraphicOption(vm.NoteGraphicCombo, f.NoteGraphic);
                vm.NoteGraphicCombo.SelectedItem = f.NoteGraphic;
                SetHighlight(vm.NoteGraphicCombo, f.UnresolvedFields.Contains("noteGraphic"));
            }
            if (vm.RotationAngleBox is not null) { vm.RotationAngleBox.Text = f.RotationAngle; SetHighlight(vm.RotationAngleBox, f.UnresolvedFields.Contains("rotationAngle")); }
        }
        RefreshPreview();
        _suppressFieldChangeTracking = false;
    }

    /// <summary>現在選択中パターンの値をコピーして新規パターンを追加し、そちらへ切り替える
    /// (2026-07-26e確定仕様: ゼロから入力させず、差分だけ調整すればよいようにする)。</summary>
    private void AddPattern_Click(object sender, RoutedEventArgs e)
    {
        var src = ActivePattern;
        _extraPatterns.Add(new PatternVM { Name = "", Blank = src.Blank, DivideCnt = src.DivideCnt, PosMax = src.PosMax });

        foreach (TabItem item in _laneTabs.Items)
        {
            var vm = (LaneEditVM)item.Tag!;
            var f = ActiveFields(vm);
            vm.PatternOverrides.Add(new LanePatternFieldsVM
            {
                KeyAssign = f.KeyAssign,
                ColorGroup = f.ColorGroup,
                PosIndex = f.PosIndex,
                ScrollDirection = f.ScrollDirection,
                NoteGraphic = f.NoteGraphic,
                RotationAngle = f.RotationAngle,
            });
        }

        _currentPatternIndex = _extraPatterns.Count;
        RefreshPatternCombo();
    }

    private void RemovePattern_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPatternIndex <= 0 || _currentPatternIndex > _extraPatterns.Count) return;
        int idx = _currentPatternIndex - 1;
        var name = string.IsNullOrWhiteSpace(_extraPatterns[idx].Name) ? $"パターン{_currentPatternIndex}" : _extraPatterns[idx].Name;
        var confirm = MessageBox.Show(this, $"'{name}' を削除しますか?", "パターン削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _extraPatterns.RemoveAt(idx);
        foreach (TabItem item in _laneTabs.Items)
        {
            var vm = (LaneEditVM)item.Tag!;
            if (idx < vm.PatternOverrides.Count) vm.PatternOverrides.RemoveAt(idx);
        }

        _currentPatternIndex = 0;
        RefreshPatternCombo();
    }

    // =====================================================================
    // keyboardInputKeysのキー入力キャプチャ(2026-07-26)
    // =====================================================================

    /// <summary>「入力開始」ボタン押下時。次に押された物理キー1つを対象欄へ追加する待機状態にする。</summary>
    private void BeginKeyCapture(Button button, TextBox targetBox)
    {
        if (_capturingKey) EndKeyCapture(); // 別の欄で入力待ち中だった場合は切り替える
        _capturingKey = true;
        _captureTargetBox = targetBox;
        _captureButton = button;
        button.Content = "キー入力待ち...(Escで中止)";
        button.IsEnabled = false;
        Keyboard.Focus(this); // テキストボックス等にフォーカスが残ってキー入力を横取りしないようにする
    }

    private void EndKeyCapture()
    {
        if (_captureButton is not null) { _captureButton.Content = "入力開始"; _captureButton.IsEnabled = true; }
        _capturingKey = false;
        _captureTargetBox = null;
        _captureButton = null;
    }

    /// <summary>キー入力待ち状態の間だけ、Window全体でPreview段階のキー押下を横取りする
    /// (Save/CancelボタンのIsDefault/IsCancel等、通常のショートカットに奪われないようにするため)。
    /// Escapeは「キー自体の指定」ではなく「キャプチャの中止」として扱う(2026-07-26確定仕様)。</summary>
    private void Window_PreviewKeyDown_KeyCapture(object sender, KeyEventArgs e)
    {
        if (!_capturingKey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt同時押し時はSystemKey側に実キーが入る
        if (key == Key.Escape) { EndKeyCapture(); return; }

        var label = KeyLabelMapper.LabelForKey(key);
        if (label is null)
        {
            MessageBox.Show(this, $"このキー({key})は対応表に無いため指定できませんの。", "キー入力",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            EndKeyCapture();
            return;
        }

        if (_captureTargetBox is not null)
        {
            var keys = SplitKeys(_captureTargetBox.Text);
            if (!keys.Contains(label)) keys.Add(label);
            _captureTargetBox.Text = string.Join("/", keys);
        }
        EndKeyCapture();
    }

    private void RemoveLane_Click(object sender, RoutedEventArgs e)
    {
        if (_laneTabs.SelectedItem is not TabItem item) return;
        var vm = (LaneEditVM)item.Tag!;
        var confirm = MessageBox.Show(this, $"レーン '{HeaderText(vm)}' を削除しますか?",
            "レーン削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        _laneTabs.Items.Remove(item);
        UpdateLaneCountLabel();
        RefreshPreview();
    }

    // =====================================================================
    // レーンタブのD&D並び替え(MainWindow.xaml.csの難易度タブ並び替えと同じ方式:
    // TabItemのヒットテスト+IndexFromContainer。displayOrderはタブの並び順そのものなので
    // 別途データを移動する必要はなく、TabControl.Itemsの並びを直接入れ替えるだけでよい)
    // =====================================================================

    private static TabItem? FindTabItemAncestor(DependencyObject? source)
    {
        while (source is not null and not TabItem)
            source = VisualTreeHelper.GetParent(source);
        return source as TabItem;
    }

    private void LaneTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindTabItemAncestor(e.OriginalSource as DependencyObject);
        if (item is null) { _tabDragSourceIndex = -1; return; }
        _tabDragStartPoint = e.GetPosition(null);
        _tabDragSourceIndex = _laneTabs.Items.IndexOf(item);
    }

    private void LaneTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        int from = _tabDragSourceIndex;
        _tabDragSourceIndex = -1;
        DragDrop.DoDragDrop(_laneTabs, from, DragDropEffects.Move);
        // DoDragDropはドラッグ操作が終わるまで戻らないため、終了理由によらずここで確実にインジケータを消す
        // (Drop/DragLeaveの呼び忘れ経路をカバーする保険、MainWindowのタブ入替と同じ考え方)。
        HideLaneTabsInsertIndicator();
    }

    /// <summary>マウス位置(_laneTabs基準の座標)から「挿入先index」(0～Items.Count、Countなら末尾へ挿入)を
    /// 判定する(2026-08-08要望対応、MainWindow.xaml.csのComputeTabInsertIndexと同じ方式)。</summary>
    private int ComputeLaneTabInsertIndex(Point posOnTabControl, DependencyObject? hitSource)
    {
        int count = _laneTabs.Items.Count;
        if (count == 0) return 0;

        var item = FindTabItemAncestor(hitSource);
        if (item is not null)
        {
            int idx = _laneTabs.ItemContainerGenerator.IndexFromContainer(item);
            if (idx < 0) return count;
            double itemLeft = item.TranslatePoint(new Point(0, 0), _laneTabs).X;
            double midX = itemLeft + item.ActualWidth / 2.0;
            return posOnTabControl.X < midX ? idx : idx + 1;
        }

        // TabItemそのものには乗っていない(タブ行の余白部分)。先頭・末尾タブとの位置関係で判定する。
        if (_laneTabs.ItemContainerGenerator.ContainerFromIndex(0) is TabItem first &&
            posOnTabControl.X < first.TranslatePoint(new Point(0, 0), _laneTabs).X)
            return 0;
        return count;
    }

    private void ShowLaneTabsInsertIndicator(int insertIndex)
    {
        int count = _laneTabs.Items.Count;
        double x;
        if (count == 0) { HideLaneTabsInsertIndicator(); return; }
        if (insertIndex <= 0)
            x = _laneTabs.ItemContainerGenerator.ContainerFromIndex(0) is TabItem first
                ? first.TranslatePoint(new Point(0, 0), _laneTabs).X : 0;
        else if (insertIndex >= count)
            x = _laneTabs.ItemContainerGenerator.ContainerFromIndex(count - 1) is TabItem last
                ? last.TranslatePoint(new Point(0, 0), _laneTabs).X + last.ActualWidth : 0;
        else
            x = _laneTabs.ItemContainerGenerator.ContainerFromIndex(insertIndex) is TabItem mid
                ? mid.TranslatePoint(new Point(0, 0), _laneTabs).X : 0;

        _laneTabsInsertIndicator.Margin = new Thickness(x - _laneTabsInsertIndicator.Width / 2.0, 0, 0, 0);
        _laneTabsInsertIndicator.Visibility = Visibility.Visible;
    }

    private void HideLaneTabsInsertIndicator() => _laneTabsInsertIndicator.Visibility = Visibility.Collapsed;

    private void LaneTabs_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int)))
        {
            e.Effects = DragDropEffects.None;
            HideLaneTabsInsertIndicator();
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int insertIndex = ComputeLaneTabInsertIndex(e.GetPosition(_laneTabs), e.OriginalSource as DependencyObject);
        ShowLaneTabsInsertIndicator(insertIndex);
        e.Handled = true;
    }

    /// <summary>2026-08-08要望対応: 「離した場所のタブと入替え」ではなく「離した位置(カーソルがタブの
    /// 左右どちらの半分にあるか)に挿入」する(MainWindowの難易度タブ入替と同じ方式)。</summary>
    private void LaneTabs_Drop(object sender, DragEventArgs e)
    {
        HideLaneTabsInsertIndicator();
        if (!e.Data.GetDataPresent(typeof(int))) return;
        int from = (int)e.Data.GetData(typeof(int));
        if (from < 0 || from >= _laneTabs.Items.Count) return;

        int rawTarget = ComputeLaneTabInsertIndex(e.GetPosition(_laneTabs), e.OriginalSource as DependencyObject);
        int to = rawTarget > from ? rawTarget - 1 : rawTarget; // fromを取り除いた後のインデックスへ変換
        if (to == from) return;

        var moving = _laneTabs.Items[from];
        _laneTabs.Items.RemoveAt(from);
        _laneTabs.Items.Insert(to, moving);
        _laneTabs.SelectedItem = moving;
        RefreshPreview();
    }

    // =====================================================================
    // プレビュー(補助表示): タブの並び順でnoteGraphicの画像を並べる
    // =====================================================================

    private void RefreshPreview()
    {
        _previewStrip.InvalidateMeasure();
        _previewStrip.InvalidateVisual();
        RefreshFujiLaneNumStrip();
    }

    /// <summary>
    /// プレビュー帯の下のfujiLaneNum入力欄を、現在のタブ順で再構築する(2026-07-26)。
    /// 各TextBoxはタブのLaneEditVMを直接クロージャで捕まえているため、D&Dでタブの並びが
    /// 変わっても値そのものはレーンに紐付いたまま移動する(値を並び替える処理は不要で、
    /// 単に表示順を作り直すだけでよい)。
    /// </summary>
    private void RefreshFujiLaneNumStrip()
    {
        _fujiLaneNumStrip.Children.Clear();
        const double cellWidth = 64;
        foreach (TabItem item in _laneTabs.Items)
        {
            var vm = (LaneEditVM)item.Tag!;
            var box = new TextBox
            {
                Width = cellWidth - 8,
                Margin = new Thickness(4, 0, 4, 4),
                Text = vm.FujiLaneNumText,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = $"{HeaderText(vm)} のfujiLaneNum(空欄=displayOrderを使用)",
            };
            box.TextChanged += (_, _) => vm.FujiLaneNumText = box.Text;
            _fujiLaneNumStrip.Children.Add(box);
        }
    }

    /// <summary>
    /// プレビュー帯の描画本体(2026-07-26要望: rotationAngle・colorGroupの反映)。
    /// 実際の譜面ビュー描画(ChartCanvas.DrawNoteImage)と同じロジックをそのまま使うため、
    /// rotationAngleの反映は本家準拠でnoteGraphic="arrow"の時のみ(それ以外は固定向きの専用画像)。
    /// 色はcolorGroupに応じて要望の見本色(setColor=#9999ff,#ccffff,#ffffff,#ffff99,#ff9966)を
    /// 循環で割り当てる(テンプレート編集時の見分け用であり、実際のsetColorとは無関係・保存もしない)。
    /// </summary>
    private sealed class PreviewStripElement(TemplateEditorWindow owner) : FrameworkElement
    {
        private const double CellWidth = 64;
        private const double NoteSize = 40;

        protected override Size MeasureOverride(Size availableSize) =>
            new(Math.Max(1, owner._laneTabs.Items.Count) * CellWidth, 76);

        protected override void OnRender(DrawingContext dc)
        {
            double x = CellWidth / 2;
            foreach (TabItem item in owner._laneTabs.Items)
            {
                var vm = (LaneEditVM)item.Tag!;
                var f = owner.ActiveFields(vm); // 2026-07-26e: プレビューも選択中パターンの見た目を表示する
                double rot = double.TryParse(f.RotationAngle, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
                string graphic = string.IsNullOrWhiteSpace(f.NoteGraphic) ? "arrow" : f.NoteGraphic;
                var lane = new LaneDef
                {
                    LaneId = vm.LaneId,
                    DataName = vm.DataName,
                    DisplayOrder = 0,
                    KeyAssign = ["x"],
                    ColorGroup = 0,
                    PosIndex = 0,
                    ScrollDirection = "down",
                    NoteGraphic = graphic,
                    RotationAngle = rot,
                    EngineLaneNum = 0,
                };

                int colorGroup = int.TryParse(f.ColorGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cg) ? cg : 0;
                var color = ChartCanvas.PreviewSampleColorForGroup(colorGroup);

                double cy = NoteSize / 2 + 6;
                ChartCanvas.DrawLaneIcon(dc, lane, x, cy, NoteSize, color);

                var ft = new FormattedText(HeaderText(vm), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Meiryo UI"), 10, Brushes.White, 1.25); // 2026-07-26: 背景を黒にしたため白文字に変更
                dc.DrawText(ft, new Point(x - ft.Width / 2, NoteSize + 8));

                x += CellWidth;
            }
        }
    }

    private static List<string> LoadNoteGraphicOptions()
    {
        var imgDir = AppPaths.FindAssetDir("img");
        if (imgDir is null) return ["arrow"];
        // 2026-07-26: pngに加えてsvgも素材として認識する(要望対応)。同名のpng/svgが両方ある場合は
        // 1項目にまとめる(実際の読込優先順位はChartCanvas.GetNoteImageと同じくpng優先)。
        return Directory.EnumerateFiles(imgDir, "*.png")
            .Concat(Directory.EnumerateFiles(imgDir, "*.svg"))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !n.Contains("shadow", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // =====================================================================
    // 読み込み・保存
    // =====================================================================

    private static readonly string[] LaneUnresolvedFieldNames = ["colorGroup", "posIndex", "scrollDirection", "noteGraphic", "rotationAngle"];
    private static readonly string[] PatternUnresolvedFieldNames = ["blank", "divideCnt", "posMax"];

    private void LoadFrom(KeyTemplate tpl)
    {
        _keyTypeId.Text = tpl.KeyTypeId;
        _keyTypeName.Text = tpl.KeyTypeName;
        _comment.Text = tpl.Comment ?? "";
        _keyboardLayout.SelectedItem = tpl.KeyboardLayout;

        _suppressFieldChangeTracking = true;
        _basePattern.Blank = tpl.Blank.ToString(CultureInfo.InvariantCulture);
        _basePattern.DivideCnt = tpl.DivideCnt.ToString(CultureInfo.InvariantCulture);
        _basePattern.PosMax = tpl.PosMax.ToString(CultureInfo.InvariantCulture);
        // 2026-08-03要望対応: カスタムキー定義の取り込み直後に開かれた場合、確定できなかった
        // 項目(パターン0=既定パターン分)をここで反映する。ハイライトの実際の表示はこの後の
        // RefreshPatternCombo→LoadPatternFieldsIntoUIが行う。
        if (_pendingIssues is not null)
            foreach (var fn in PatternUnresolvedFieldNames)
                if (_pendingIssues.UnresolvedPatternFields.Contains((0, fn))) _basePattern.UnresolvedFields.Add(fn);
        _blank.Text = _basePattern.Blank;
        _divideCnt.Text = _basePattern.DivideCnt;
        _posMax.Text = _basePattern.PosMax;
        _suppressFieldChangeTracking = false;

        // 2026-07-26e: 追加パターン(ExtraPatterns)を読み込む。tpl.Lanesは保存時にタブ順=displayOrder順で
        // 書き出されているため、DisplayOrder順に並べ直せば各パターンのLaneOverridesと同じインデックスで
        // 対応が取れる(KeyTemplate.WithPatternと同じ前提)。
        _extraPatterns.Clear();
        for (int pi = 0; pi < tpl.ExtraPatterns.Count; pi++)
        {
            var p = tpl.ExtraPatterns[pi];
            var pvm = new PatternVM
            {
                Name = p.Name ?? "",
                Blank = p.Blank.ToString(CultureInfo.InvariantCulture),
                DivideCnt = p.DivideCnt.ToString(CultureInfo.InvariantCulture),
                PosMax = p.PosMax.ToString(CultureInfo.InvariantCulture),
            };
            if (_pendingIssues is not null)
            {
                int patternNumber = pi + 1;
                foreach (var fn in PatternUnresolvedFieldNames)
                    if (_pendingIssues.UnresolvedPatternFields.Contains((patternNumber, fn))) pvm.UnresolvedFields.Add(fn);
            }
            _extraPatterns.Add(pvm);
        }

        var orderedLanes = tpl.Lanes.OrderBy(l => l.DisplayOrder).ToList();
        for (int i = 0; i < orderedLanes.Count; i++)
        {
            var vm = FromLaneDef(orderedLanes[i]);
            if (_pendingIssues is not null)
                foreach (var fn in LaneUnresolvedFieldNames)
                    if (_pendingIssues.UnresolvedLaneFields.Contains((0, i, fn))) vm.Base.UnresolvedFields.Add(fn);

            for (int pi = 0; pi < tpl.ExtraPatterns.Count; pi++)
            {
                var ov = FromOverride(tpl.ExtraPatterns[pi].LaneOverrides[i]);
                if (_pendingIssues is not null)
                {
                    int patternNumber = pi + 1;
                    foreach (var fn in LaneUnresolvedFieldNames)
                        if (_pendingIssues.UnresolvedLaneFields.Contains((patternNumber, i, fn))) ov.UnresolvedFields.Add(fn);
                }
                vm.PatternOverrides.Add(ov);
            }
            AddLaneTab(vm);
        }
    }

    private static List<string> SplitKeys(string text) =>
        text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool TryBuildLaneDef(LaneEditVM vm, int displayOrder, out LaneDef? lane, out string? error)
    {
        lane = null;
        string laneLabel = HeaderText(vm);
        if (string.IsNullOrWhiteSpace(vm.LaneId)) { error = "laneIdが空欄のレーンがありますの"; return false; }
        if (string.IsNullOrWhiteSpace(vm.DataName)) { error = $"'{laneLabel}': dataNameを入力してくださいまし"; return false; }
        var kbdKeys = SplitKeys(vm.KeyboardInputKeys);
        if (!int.TryParse(vm.EngineLaneNum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var engineLaneNum))
        { error = $"'{laneLabel}': engineLaneNumは整数で入力してくださいまし"; return false; }
        int? fujiLaneNum = null;
        if (!string.IsNullOrWhiteSpace(vm.FujiLaneNumText))
        {
            if (!int.TryParse(vm.FujiLaneNumText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFuji))
            { error = $"'{laneLabel}': fujiLaneNumは整数で入力するか、空欄にしてくださいまし"; return false; }
            fujiLaneNum = parsedFuji;
        }
        // 2026-07-26e: keyAssign以下6項目(パターン0=既定パターン分)はvm.Baseから検証する
        if (!TryBuildPatternOverride(vm.Base, laneLabel, 0, out var baseFields, out error)) return false;

        lane = new LaneDef
        {
            LaneId = vm.LaneId,
            DataName = vm.DataName,
            FrzDataNameOverride = string.IsNullOrWhiteSpace(vm.FrzDataNameOverride) ? null : vm.FrzDataNameOverride,
            DisplayOrder = displayOrder,
            KeyAssign = baseFields!.KeyAssign,
            KeyboardInputKeys = kbdKeys,
            ColorGroup = baseFields.ColorGroup,
            PosIndex = baseFields.PosIndex,
            ScrollDirection = baseFields.ScrollDirection,
            NoteGraphic = baseFields.NoteGraphic,
            RotationAngle = baseFields.RotationAngle,
            EngineLaneNum = engineLaneNum,
            FujiLaneNum = fujiLaneNum,
        };
        error = null;
        return true;
    }

    /// <summary>キーパターン1件・1レーン分のプレゼンテーション値を検証してLanePatternOverrideを作る
    /// (2026-07-26e)。patternNumber=0は既定パターン(エラーメッセージ用の表記のみ、"パターン0"は
    /// 付けずベースのレーンとして表示する)。</summary>
    private static bool TryBuildPatternOverride(LanePatternFieldsVM f, string laneLabel, int patternNumber,
        out LanePatternOverride? result, out string? error)
    {
        result = null;
        string suffix = patternNumber > 0 ? $"(パターン{patternNumber})" : "";
        var keyAssign = SplitKeys(f.KeyAssign);
        if (keyAssign.Count == 0) { error = $"'{laneLabel}'{suffix}: keyAssignを1つ以上指定してくださいまし"; return false; }
        if (!int.TryParse(f.ColorGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorGroup))
        { error = $"'{laneLabel}'{suffix}: colorGroupは整数で入力してくださいまし"; return false; }
        if (!double.TryParse(f.PosIndex, NumberStyles.Float, CultureInfo.InvariantCulture, out var posIndex))
        { error = $"'{laneLabel}'{suffix}: posIndexは数値で入力してくださいまし"; return false; }
        if (f.ScrollDirection is not ("up" or "down"))
        { error = $"'{laneLabel}'{suffix}: scrollDirectionはup/downのいずれかにしてくださいまし"; return false; }
        if (string.IsNullOrWhiteSpace(f.NoteGraphic)) { error = $"'{laneLabel}'{suffix}: noteGraphicを選択してくださいまし"; return false; }
        if (!double.TryParse(f.RotationAngle, NumberStyles.Float, CultureInfo.InvariantCulture, out var rot))
        { error = $"'{laneLabel}'{suffix}: rotationAngleは数値で入力してくださいまし"; return false; }

        result = new LanePatternOverride
        {
            KeyAssign = keyAssign,
            ColorGroup = colorGroup,
            PosIndex = posIndex,
            ScrollDirection = f.ScrollDirection,
            NoteGraphic = f.NoteGraphic,
            RotationAngle = rot,
        };
        error = null;
        return true;
    }

    /// <summary>2026-08-03要望対応: 取り込みで確定できなかった項目(プレイテスト・プレビューの
    /// 画面構成に関わる項目のみ)を、パターン0→追加パターンの順、各パターン内はグローバル設定→
    /// タブ順のレーンの順で列挙する。保存ブロック時のメッセージ・切替先の決定に使う。</summary>
    private List<(int PatternIndex, TabItem? LaneItem, string FieldName)> CollectUnresolvedFields()
    {
        var result = new List<(int, TabItem?, string)>();

        void AddPatternFields(int patternIndex, HashSet<string> set)
        {
            foreach (var fn in PatternUnresolvedFieldNames)
                if (set.Contains(fn)) result.Add((patternIndex, null, fn));
        }
        void AddLaneFields(int patternIndex, TabItem item, HashSet<string> set)
        {
            foreach (var fn in LaneUnresolvedFieldNames)
                if (set.Contains(fn)) result.Add((patternIndex, item, fn));
        }

        AddPatternFields(0, _basePattern.UnresolvedFields);
        foreach (TabItem item in _laneTabs.Items)
            AddLaneFields(0, item, ((LaneEditVM)item.Tag!).Base.UnresolvedFields);

        for (int pi = 0; pi < _extraPatterns.Count; pi++)
        {
            int patternNumber = pi + 1;
            AddPatternFields(patternNumber, _extraPatterns[pi].UnresolvedFields);
            foreach (TabItem item in _laneTabs.Items)
            {
                var vm = (LaneEditVM)item.Tag!;
                if (pi < vm.PatternOverrides.Count) AddLaneFields(patternNumber, item, vm.PatternOverrides[pi].UnresolvedFields);
            }
        }
        return result;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";

        // 2026-08-03要望対応: カスタムキー定義の取り込みで確定できなかった項目(プレイテスト・
        // プレビューの表示に直接関わる項目のみ、keyAssign等のキーボードモード用の項目は対象外)が
        // 残っている間は保存させない。OKを押してもダイアログで入力を促し、該当のパターン/レーンへ
        // 切り替えた上でテンプレートエディタでの編集を続行させる(要望どおり、保存自体はブロックする)。
        var unresolved = CollectUnresolvedFields();
        if (unresolved.Count > 0)
        {
            var (patternIndex, laneItem, fieldName) = unresolved[0];
            _patternCombo.SelectedIndex = patternIndex;
            if (laneItem is not null) _laneTabs.SelectedItem = laneItem;
            MessageBox.Show(this,
                $"プレイテスト・プレビューの表示に必要な項目が未入力のままですの(あと{unresolved.Count}件、" +
                $"まず「{fieldName}」をご確認くださいまし)。カスタムキー定義の取り込みで自動取得できなかった" +
                "項目ですわ。黄色くハイライトされた欄をすべて入力してから、改めて保存してくださいまし。",
                "入力が必要ですの", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var keyTypeId = _keyTypeId.Text.Trim();
        if (keyTypeId.Length == 0 || keyTypeId.Length > 10 || !keyTypeId.All(char.IsAsciiLetterOrDigit))
        { _error.Text = "keyTypeIdは半角英数字1〜10文字で入力してくださいまし"; return; }
        if (string.IsNullOrWhiteSpace(_keyTypeName.Text))
        { _error.Text = "keyTypeNameを入力してくださいまし"; return; }
        // 2026-07-26e: blank/divideCnt/posMaxは現在表示中のパターンの値であり、既定パターン(0)の
        // 値は_basePatternに常に同期済み(TextChangedがActivePattern経由で書き込むため)。
        if (!double.TryParse(_basePattern.Blank, NumberStyles.Float, CultureInfo.InvariantCulture, out var blank))
        { _error.Text = "blank(パターン0)は数値で入力してくださいまし"; return; }
        if (!double.TryParse(_basePattern.DivideCnt, NumberStyles.Float, CultureInfo.InvariantCulture, out var divideCnt))
        { _error.Text = "divideCnt(パターン0)は数値で入力してくださいまし"; return; }
        if (!double.TryParse(_basePattern.PosMax, NumberStyles.Float, CultureInfo.InvariantCulture, out var posMax))
        { _error.Text = "posMax(パターン0)は数値で入力してくださいまし"; return; }
        if (_laneTabs.Items.Count == 0)
        { _error.Text = "レーンを1つ以上追加してくださいまし"; return; }

        var lanes = new List<LaneDef>();
        var engineLaneSeen = new HashSet<int>();
        int order = 0;
        foreach (TabItem item in _laneTabs.Items)
        {
            var vm = (LaneEditVM)item.Tag!;
            if (!TryBuildLaneDef(vm, order, out var lane, out var err))
            { _error.Text = err; return; }
            if (!engineLaneSeen.Add(lane!.EngineLaneNum))
            { _error.Text = $"engineLaneNum({lane.EngineLaneNum})がレーン間で重複していますの"; return; }
            lanes.Add(lane);
            order++;
        }

        // 2026-07-26e: 追加パターンの検証。LaneOverridesはlanes(=_laneTabs.Itemsの並び順)と
        // 同じ順序で組み立てる(KeyTemplate.WithPatternの前提=配列インデックス対応)。
        var extraPatterns = new List<KeyPattern>();
        for (int pi = 0; pi < _extraPatterns.Count; pi++)
        {
            var pvm = _extraPatterns[pi];
            int patternNumber = pi + 1;
            if (!double.TryParse(pvm.Blank, NumberStyles.Float, CultureInfo.InvariantCulture, out var pBlank))
            { _error.Text = $"blank(パターン{patternNumber})は数値で入力してくださいまし"; return; }
            if (!double.TryParse(pvm.DivideCnt, NumberStyles.Float, CultureInfo.InvariantCulture, out var pDivideCnt))
            { _error.Text = $"divideCnt(パターン{patternNumber})は数値で入力してくださいまし"; return; }
            if (!double.TryParse(pvm.PosMax, NumberStyles.Float, CultureInfo.InvariantCulture, out var pPosMax))
            { _error.Text = $"posMax(パターン{patternNumber})は数値で入力してくださいまし"; return; }

            var overrides = new List<LanePatternOverride>();
            foreach (TabItem item in _laneTabs.Items)
            {
                var vm = (LaneEditVM)item.Tag!;
                if (!TryBuildPatternOverride(vm.PatternOverrides[pi], HeaderText(vm), patternNumber, out var ov, out var err))
                { _error.Text = err; return; }
                overrides.Add(ov!);
            }

            extraPatterns.Add(new KeyPattern
            {
                Name = string.IsNullOrWhiteSpace(pvm.Name) ? null : pvm.Name.Trim(),
                Blank = pBlank,
                DivideCnt = pDivideCnt,
                PosMax = pPosMax,
                LaneOverrides = overrides,
            });
        }

        var template = new KeyTemplate
        {
            KeyTypeId = keyTypeId,
            KeyTypeName = _keyTypeName.Text.Trim(),
            KeyCount = lanes.Count,
            Comment = string.IsNullOrWhiteSpace(_comment.Text) ? null : _comment.Text,
            KeyboardLayout = _keyboardLayout.SelectedItem is KeyboardLayout kbl ? kbl : KeyboardLayout.Us,
            Blank = blank,
            DivideCnt = divideCnt,
            PosMax = posMax,
            Lanes = lanes,
            ExtraPatterns = extraPatterns,
        };

        var destPath = Path.Combine(_templateDir, $"temp_{keyTypeId}.json");
        bool isNew = _originalPath is null;
        bool renaming = !isNew && !string.Equals(Path.GetFullPath(_originalPath!), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);

        if ((isNew || renaming) && File.Exists(destPath))
        { _error.Text = $"temp_{keyTypeId}.json は既に存在しますの。別のkeyTypeIdにしてくださいまし"; return; }

        try
        {
            template.Save(destPath);
            if (renaming) File.Delete(_originalPath!);
        }
        catch (Exception ex)
        {
            _error.Text = $"保存に失敗しましたわ: {ex.Message}";
            return;
        }

        SavedPath = destPath;
        SavedKeyTypeId = keyTypeId;
        DialogResult = true;
    }
}
