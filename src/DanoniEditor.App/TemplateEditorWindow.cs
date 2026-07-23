using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// キー種テンプレート(temp_{keyTypeId}.json)の作成・編集ウィンドウ(2026-07-29)。
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

    // --- 全体設定(左パネル) ---
    private readonly TextBox _keyTypeId = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 10 };
    private readonly TextBox _keyTypeName = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _comment = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _blank = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _divideCnt = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _posMax = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _laneCountLabel = new() { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray };

    private readonly TabControl _laneTabs = new();
    private readonly PreviewStripElement _previewStrip;
    private readonly StackPanel _fujiLaneNumStrip = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _removeLaneButton = new() { Content = "レーン削除", Width = 90, IsEnabled = false };

    // --- レーンタブのD&D並び替え ---
    private Point _tabDragStartPoint;
    private int _tabDragSourceIndex = -1;

    /// <summary>保存に成功した場合の保存先パス(呼び出し元がテンプレート一覧を再読込する用)</summary>
    public string? SavedPath { get; private set; }

    /// <summary>保存に成功した場合のkeyTypeId(呼び出し元がTemplateRepositoryのキャッシュを破棄する用)。
    /// リネーム(keyTypeId変更)時は旧IDも合わせて破棄する必要があるため、旧IDは別途OriginalKeyTypeIdで返す。</summary>
    public string? SavedKeyTypeId { get; private set; }

    /// <summary>編集開始時点のkeyTypeId(新規作成時はnull)</summary>
    public string? OriginalKeyTypeId { get; }

    public TemplateEditorWindow(string templateDir, string? existingPath)
    {
        _templateDir = templateDir;
        _originalPath = existingPath;
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

        _laneTabs.AllowDrop = true;
        _laneTabs.PreviewMouseLeftButtonDown += LaneTabs_PreviewMouseLeftButtonDown;
        _laneTabs.PreviewMouseMove += LaneTabs_PreviewMouseMove;
        _laneTabs.PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        _laneTabs.Drop += LaneTabs_Drop;
        _laneTabs.SelectionChanged += (_, _) => _removeLaneButton.IsEnabled = _laneTabs.SelectedItem is not null;

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

        // 2026-07-31: プレビュー帯の下にfujiLaneNum入力欄の帯を追加(要望)。同じScrollViewerに
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
            Background = Brushes.Black, // 2026-07-30: 明るい色見本(#ccffff/#ffffff等)の視認性対応
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
        centerPanel.Children.Add(_laneTabs);
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
        p.Children.Add(Label("blank(px):"));
        p.Children.Add(_blank);
        p.Children.Add(Label("divideCnt:"));
        p.Children.Add(_divideCnt);
        p.Children.Add(Label("posMax:"));
        p.Children.Add(_posMax);
        p.Children.Add(_laneCountLabel);
        p.Children.Add(new TextBlock
        {
            Text = "keyCount(レーン数)はレーンタブの数から自動算出しますの。displayOrderも" +
                   "保存時にタブの左からの順番(一番左=0)で自動採番しますわ。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
    /// プレビュー帯の下の専用欄で編集する(2026-07-31、レーンタブ本体の入力欄一覧には含めない)。
    /// 空欄は「未指定(displayOrderを使う)」を表す。</summary>
    private sealed class LaneEditVM
    {
        public string LaneId = "";
        public string DataName = "";
        public string FrzDataNameOverride = "";
        public string KeyAssign = "";
        public string KeyboardInputKeys = "";
        public string ColorGroup = "0";
        public string PosIndex = "0";
        public string ScrollDirection = "down";
        public string NoteGraphic = "arrow";
        public string RotationAngle = "0";
        public string EngineLaneNum = "0";
        public string FujiLaneNumText = "";
    }

    private static LaneEditVM FromLaneDef(LaneDef d) => new()
    {
        LaneId = d.LaneId,
        DataName = d.DataName,
        FrzDataNameOverride = d.FrzDataNameOverride ?? "",
        KeyAssign = string.Join("/", d.KeyAssign),
        KeyboardInputKeys = string.Join("/", d.KeyboardInputKeys),
        ColorGroup = d.ColorGroup.ToString(CultureInfo.InvariantCulture),
        PosIndex = d.PosIndex.ToString(CultureInfo.InvariantCulture),
        ScrollDirection = d.ScrollDirection,
        NoteGraphic = d.NoteGraphic,
        RotationAngle = d.RotationAngle.ToString(CultureInfo.InvariantCulture),
        EngineLaneNum = d.EngineLaneNum.ToString(CultureInfo.InvariantCulture),
        FujiLaneNumText = d.FujiLaneNum?.ToString(CultureInfo.InvariantCulture) ?? "",
    };

    private static string HeaderText(LaneEditVM vm) => string.IsNullOrWhiteSpace(vm.LaneId) ? "(無名)" : vm.LaneId;

    private void AddLaneTab(LaneEditVM vm)
    {
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

        AddTextRow("laneId:", vm.LaneId, v => { vm.LaneId = v; tab.Header = HeaderText(vm); RefreshPreview(); });
        AddTextRow("dataName:", vm.DataName, v => vm.DataName = v);
        AddTextRow("frzDataNameOverride(空欄=frz+dataNameの規定通り):", vm.FrzDataNameOverride, v => vm.FrzDataNameOverride = v);
        AddTextRow("keyAssign(複数キーは/区切り、例 E/R):", vm.KeyAssign, v => vm.KeyAssign = v);
        AddTextRow("keyboardInputKeys(空欄可、複数は/区切り):", vm.KeyboardInputKeys, v => vm.KeyboardInputKeys = v);
        AddTextRow("colorGroup(整数):", vm.ColorGroup, v => { vm.ColorGroup = v; RefreshPreview(); });
        AddTextRow("posIndex(数値、小数可):", vm.PosIndex, v => vm.PosIndex = v);

        grid.Children.Add(Label("scrollDirection:"));
        var scrollCombo = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        scrollCombo.Items.Add("up");
        scrollCombo.Items.Add("down");
        scrollCombo.SelectedItem = vm.ScrollDirection is "up" or "down" ? vm.ScrollDirection : "down";
        scrollCombo.SelectionChanged += (_, _) => vm.ScrollDirection = scrollCombo.SelectedItem as string ?? "down";
        grid.Children.Add(scrollCombo);

        grid.Children.Add(Label("noteGraphic:"));
        var graphicCombo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var g in _noteGraphicOptions) graphicCombo.Items.Add(g);
        if (!_noteGraphicOptions.Contains(vm.NoteGraphic)) graphicCombo.Items.Add(vm.NoteGraphic);
        graphicCombo.SelectedItem = vm.NoteGraphic;
        graphicCombo.SelectionChanged += (_, _) =>
        {
            vm.NoteGraphic = graphicCombo.SelectedItem as string ?? vm.NoteGraphic;
            RefreshPreview();
        };
        grid.Children.Add(graphicCombo);

        AddTextRow("rotationAngle(度):", vm.RotationAngle, v => { vm.RotationAngle = v; RefreshPreview(); });
        AddTextRow("engineLaneNum(整数、本体エンジンの内部レーン番号):", vm.EngineLaneNum, v => vm.EngineLaneNum = v);

        tab.Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return tab;
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
    }

    private void LaneTabs_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int))) return;
        int from = (int)e.Data.GetData(typeof(int));
        var item = FindTabItemAncestor(e.OriginalSource as DependencyObject);
        if (item is null) return;
        int to = _laneTabs.Items.IndexOf(item);
        if (from < 0 || to < 0 || from >= _laneTabs.Items.Count || from == to) return;

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
    /// プレビュー帯の下のfujiLaneNum入力欄を、現在のタブ順で再構築する(2026-07-31)。
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
    /// プレビュー帯の描画本体(2026-07-30要望: rotationAngle・colorGroupの反映)。
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
                double rot = double.TryParse(vm.RotationAngle, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
                string graphic = string.IsNullOrWhiteSpace(vm.NoteGraphic) ? "arrow" : vm.NoteGraphic;
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

                int colorGroup = int.TryParse(vm.ColorGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cg) ? cg : 0;
                var color = ChartCanvas.PreviewSampleColorForGroup(colorGroup);

                double cy = NoteSize / 2 + 6;
                ChartCanvas.DrawLaneIcon(dc, lane, x, cy, NoteSize, color);

                var ft = new FormattedText(HeaderText(vm), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Meiryo UI"), 10, Brushes.White, 1.25); // 2026-07-30: 背景を黒にしたため白文字に変更
                dc.DrawText(ft, new Point(x - ft.Width / 2, NoteSize + 8));

                x += CellWidth;
            }
        }
    }

    private static List<string> LoadNoteGraphicOptions()
    {
        var imgDir = AppPaths.FindAssetDir("img");
        if (imgDir is null) return ["arrow"];
        // 2026-08-03: pngに加えてsvgも素材として認識する(要望対応)。同名のpng/svgが両方ある場合は
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

    private void LoadFrom(KeyTemplate tpl)
    {
        _keyTypeId.Text = tpl.KeyTypeId;
        _keyTypeName.Text = tpl.KeyTypeName;
        _comment.Text = tpl.Comment ?? "";
        _blank.Text = tpl.Blank.ToString(CultureInfo.InvariantCulture);
        _divideCnt.Text = tpl.DivideCnt.ToString(CultureInfo.InvariantCulture);
        _posMax.Text = tpl.PosMax.ToString(CultureInfo.InvariantCulture);
        foreach (var lane in tpl.Lanes.OrderBy(l => l.DisplayOrder))
            AddLaneTab(FromLaneDef(lane));
    }

    private static List<string> SplitKeys(string text) =>
        text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool TryBuildLaneDef(LaneEditVM vm, int displayOrder, out LaneDef? lane, out string? error)
    {
        lane = null;
        string laneLabel = HeaderText(vm);
        if (string.IsNullOrWhiteSpace(vm.LaneId)) { error = "laneIdが空欄のレーンがありますの"; return false; }
        if (string.IsNullOrWhiteSpace(vm.DataName)) { error = $"'{laneLabel}': dataNameを入力してくださいまし"; return false; }
        var keyAssign = SplitKeys(vm.KeyAssign);
        if (keyAssign.Count == 0) { error = $"'{laneLabel}': keyAssignを1つ以上指定してくださいまし"; return false; }
        var kbdKeys = SplitKeys(vm.KeyboardInputKeys);
        if (!int.TryParse(vm.ColorGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorGroup))
        { error = $"'{laneLabel}': colorGroupは整数で入力してくださいまし"; return false; }
        if (!double.TryParse(vm.PosIndex, NumberStyles.Float, CultureInfo.InvariantCulture, out var posIndex))
        { error = $"'{laneLabel}': posIndexは数値で入力してくださいまし"; return false; }
        if (vm.ScrollDirection is not ("up" or "down"))
        { error = $"'{laneLabel}': scrollDirectionはup/downのいずれかにしてくださいまし"; return false; }
        if (string.IsNullOrWhiteSpace(vm.NoteGraphic)) { error = $"'{laneLabel}': noteGraphicを選択してくださいまし"; return false; }
        if (!double.TryParse(vm.RotationAngle, NumberStyles.Float, CultureInfo.InvariantCulture, out var rot))
        { error = $"'{laneLabel}': rotationAngleは数値で入力してくださいまし"; return false; }
        if (!int.TryParse(vm.EngineLaneNum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var engineLaneNum))
        { error = $"'{laneLabel}': engineLaneNumは整数で入力してくださいまし"; return false; }
        int? fujiLaneNum = null;
        if (!string.IsNullOrWhiteSpace(vm.FujiLaneNumText))
        {
            if (!int.TryParse(vm.FujiLaneNumText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFuji))
            { error = $"'{laneLabel}': fujiLaneNumは整数で入力するか、空欄にしてくださいまし"; return false; }
            fujiLaneNum = parsedFuji;
        }

        lane = new LaneDef
        {
            LaneId = vm.LaneId,
            DataName = vm.DataName,
            FrzDataNameOverride = string.IsNullOrWhiteSpace(vm.FrzDataNameOverride) ? null : vm.FrzDataNameOverride,
            DisplayOrder = displayOrder,
            KeyAssign = keyAssign,
            KeyboardInputKeys = kbdKeys,
            ColorGroup = colorGroup,
            PosIndex = posIndex,
            ScrollDirection = vm.ScrollDirection,
            NoteGraphic = vm.NoteGraphic,
            RotationAngle = rot,
            EngineLaneNum = engineLaneNum,
            FujiLaneNum = fujiLaneNum,
        };
        error = null;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";
        var keyTypeId = _keyTypeId.Text.Trim();
        if (keyTypeId.Length == 0 || keyTypeId.Length > 10 || !keyTypeId.All(char.IsAsciiLetterOrDigit))
        { _error.Text = "keyTypeIdは半角英数字1〜10文字で入力してくださいまし"; return; }
        if (string.IsNullOrWhiteSpace(_keyTypeName.Text))
        { _error.Text = "keyTypeNameを入力してくださいまし"; return; }
        if (!double.TryParse(_blank.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var blank))
        { _error.Text = "blankは数値で入力してくださいまし"; return; }
        if (!double.TryParse(_divideCnt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var divideCnt))
        { _error.Text = "divideCntは数値で入力してくださいまし"; return; }
        if (!double.TryParse(_posMax.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var posMax))
        { _error.Text = "posMaxは数値で入力してくださいまし"; return; }
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

        var template = new KeyTemplate
        {
            KeyTypeId = keyTypeId,
            KeyTypeName = _keyTypeName.Text.Trim(),
            KeyCount = lanes.Count,
            Comment = string.IsNullOrWhiteSpace(_comment.Text) ? null : _comment.Text,
            Blank = blank,
            DivideCnt = divideCnt,
            PosMax = posMax,
            Lanes = lanes,
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
