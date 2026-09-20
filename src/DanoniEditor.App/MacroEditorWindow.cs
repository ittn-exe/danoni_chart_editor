using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// レーン入替マクロ(仕様書11章)の作成・編集ウィンドウ(2026-07-26)。
/// - 左パネル: 対象キー種(選ぶと上下のレーンプレビューがそのキー種の構成で組み直される)・マクロ名
/// - 上段: 「元のレーン」プレビュー(選択キー種のテンプレート順、並び替え不可。基準として常時表示)
/// - 下段: 「入れ替え後」プレビュー(D&Dで並び替え可能)。ここの左からの並びがそのまま
///   LaneMapping配列になる(各セルは元々どのレーン(インデックス)だったかを保持している)。
/// </summary>
internal sealed class MacroEditorWindow : Window
{
    private readonly TemplateRepository _templates;
    private readonly string? _originalMacroId; // 編集時のみ設定(保存時にIDを引き継ぐ)
    private readonly IReadOnlyCollection<string> _existingNames; // 名前重複チェック用(自分自身は呼び出し元で除外済み)

    private readonly ComboBox _keyTypeCombo = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _macroNameBox = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private readonly LaneRowPanel _originalRow = new(draggable: false);
    private readonly LaneRowPanel _mappedRow = new(draggable: true);

    private KeyTemplate? _currentTemplate;
    private string? _lastConfirmedKeyTypeId; // 2026-07-26: キー種変更確認ダイアログ用(直前に実際に適用されていたキー種)
    private bool _suppressKeyTypeConfirm; // 初期構築中: SelectionChangedは発火してよいが確認ダイアログは出さない
    private bool _suppressKeyTypeChangeEntirely; // キャンセル時の選択巻き戻し用: SelectionChangedの処理自体を丸ごとスキップする

    /// <summary>保存に成功した場合の結果(呼び出し元がAppSettings.Macrosへ反映する用)</summary>
    public LaneSwapMacro? SavedMacro { get; private set; }

    public MacroEditorWindow(TemplateRepository templates, LaneSwapMacro? existing, IReadOnlyCollection<string> existingNames, string? initialKeyTypeId = null)
    {
        _templates = templates;
        _originalMacroId = existing?.MacroId;
        _existingNames = existingNames;

        Title = existing is null ? "マクロ新規作成" : $"マクロ編集 - {existing.MacroName}";
        Width = 820;
        Height = 500;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        foreach (var id in templates.ListKeyTypeIds()) _keyTypeCombo.Items.Add(id);
        _keyTypeCombo.SelectionChanged += KeyTypeCombo_SelectionChanged;

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

        var leftPanel = (FrameworkElement)BuildLeftPanel();
        leftPanel.Width = 220;
        DockPanel.SetDock(leftPanel, Dock.Left);
        root.Children.Add(leftPanel);

        var centerPanel = new StackPanel { Margin = new Thickness(12) };
        centerPanel.Children.Add(new TextBlock { Text = "元のレーン(並び替え不可)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        centerPanel.Children.Add(new ScrollViewer
        {
            Content = _originalRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 84,
            Background = Brushes.Black, // 2026-07-26: 明るい色見本の視認性対応
        });
        centerPanel.Children.Add(new TextBlock
        {
            Text = "↓",
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 4),
        });
        centerPanel.Children.Add(new TextBlock { Text = "入れ替え後(ドラッグで並び替え)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        centerPanel.Children.Add(new ScrollViewer
        {
            Content = _mappedRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 84,
            Background = Brushes.Black, // 2026-07-26: 明るい色見本の視認性対応
        });
        root.Children.Add(centerPanel);

        Content = root;

        _suppressKeyTypeConfirm = true; // 初期選択時は確認ダイアログを出さない
        if (existing is not null)
        {
            _macroNameBox.Text = existing.MacroName;
            _keyTypeCombo.SelectedItem = existing.TargetKeyTypeId; // SelectionChangedが一旦走る(確認抑制中)
            LoadTemplateForSelection(resetMapping: false, presetMapping: existing.LaneMapping); // ここで正しい並びに上書き
        }
        else if (initialKeyTypeId is not null && _keyTypeCombo.Items.Contains(initialKeyTypeId))
        {
            // 2026-07-26要望対応: 新規作成時はカレント難易度タブのキー種を初期選択しておく
            // (別のキー種を編集したい場合はここから選び直せる)
            _keyTypeCombo.SelectedItem = initialKeyTypeId;
        }
        else if (_keyTypeCombo.Items.Count > 0)
        {
            _keyTypeCombo.SelectedIndex = 0;
        }
        _lastConfirmedKeyTypeId = _keyTypeCombo.SelectedItem as string;
        _suppressKeyTypeConfirm = false;
    }

    /// <summary>
    /// 対象キー種コンボの変更ハンドラ(2026-07-26)。変更すると下段プレビューの入れ替え内容が
    /// 無条件にリセットされてしまう事故を防ぐため、既にレーン構成が読み込まれている状態からの
    /// 変更時は確認ダイアログを出す。キャンセルした場合は選択を直前のキー種へ戻すが、この巻き戻し
    /// 自体は「キー種変更」ではなく単なる取り消しなので、_suppressKeyTypeChangeEntirelyを立てて
    /// LoadTemplateForSelectionすら呼ばないようにする(呼んでしまうと、まだ何も変更していない
    /// 現在の入れ替え内容をresetMapping:trueで誤ってリセットしてしまうため)。
    /// </summary>
    private void KeyTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressKeyTypeChangeEntirely) return;
        if (_suppressKeyTypeConfirm) { LoadTemplateForSelection(resetMapping: true); return; }

        var newKeyTypeId = _keyTypeCombo.SelectedItem as string;
        if (_currentTemplate is not null && !string.Equals(newKeyTypeId, _lastConfirmedKeyTypeId, StringComparison.Ordinal))
        {
            var confirm = MessageBox.Show(this,
                "対象キー種を変更すると、入れ替え後プレビューの内容がリセットされます。よろしいですか?",
                "キー種の変更", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                _suppressKeyTypeChangeEntirely = true;
                _keyTypeCombo.SelectedItem = _lastConfirmedKeyTypeId;
                _suppressKeyTypeChangeEntirely = false;
                return;
            }
        }

        LoadTemplateForSelection(resetMapping: true);
        _lastConfirmedKeyTypeId = newKeyTypeId;
    }

    private UIElement BuildLeftPanel()
    {
        var p = new StackPanel { Margin = new Thickness(8) };
        p.Children.Add(Label("対象キー種", section: true));
        p.Children.Add(_keyTypeCombo);
        p.Children.Add(Label("マクロ名"));
        p.Children.Add(_macroNameBox);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Label(string text, bool section = false) => new()
    {
        Text = text,
        FontWeight = section ? FontWeights.Bold : FontWeights.Normal,
        Margin = section ? new Thickness(0, 0, 0, 6) : new Thickness(0, 8, 0, 2),
    };

    private void LoadTemplateForSelection(bool resetMapping, IReadOnlyList<int>? presetMapping = null)
    {
        if (_keyTypeCombo.SelectedItem is not string keyTypeId) return;
        KeyTemplate tpl;
        try { tpl = _templates.Get(keyTypeId); }
        catch (Exception ex) { _error.Text = $"テンプレートの読み込みに失敗しました: {ex.Message}"; return; }

        _currentTemplate = tpl;
        var identity = Enumerable.Range(0, tpl.Lanes.Count).ToList();
        _originalRow.SetLanes(tpl.Lanes, identity);

        if (!resetMapping && presetMapping is { Count: > 0 } pm && pm.Count == tpl.Lanes.Count)
            _mappedRow.SetLanes(tpl.Lanes, pm);
        else
            _mappedRow.SetLanes(tpl.Lanes, identity);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";
        if (_keyTypeCombo.SelectedItem is not string keyTypeId)
        { _error.Text = "対象キー種を選択してください"; return; }
        var name = _macroNameBox.Text.Trim();
        if (name.Length == 0)
        { _error.Text = "マクロ名を入力してください"; return; }
        if (_existingNames.Contains(name))
        { _error.Text = "同じ名前のマクロが既にあります。別の名前にしてください"; return; }
        if (_currentTemplate is null || _mappedRow.Count == 0)
        { _error.Text = "レーン構成を読み込めませんでした"; return; }

        var mapping = _mappedRow.CurrentOrderOriginalIndices();

        SavedMacro = new LaneSwapMacro
        {
            MacroId = _originalMacroId ?? Guid.NewGuid().ToString("N"),
            MacroName = name,
            TargetKeyTypeId = keyTypeId,
            LaneMapping = mapping,
        };
        DialogResult = true;
    }

    // =====================================================================
    // レーン列パネル(1行分)。draggable=trueならセルをD&Dで並び替えられる
    // (MainWindow.xaml.csの難易度タブ並び替えと同じヒットテスト+挿入方式)。
    // =====================================================================
    private sealed class LaneRowPanel : StackPanel
    {
        private static readonly Brush InsertIndicatorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));

        private Point _dragStart;
        private int _dragSourceIndex = -1;
        // 2026-08-08要望対応: 「離した場所と入替え」から「離した位置に挿入」+挿入先を示す縦線オーバーレイへ
        // 変更(MainWindow.xaml.csの難易度タブ入替と同じ方式)。Children自体はレーンセルのみで構成される
        // 前提(CurrentOrderOriginalIndices等がChildrenをそのまま走査する)ため、インジケータ用の子要素は
        // 追加せず、OnRenderで自前描画する(挿入位置は常にセル間の隙間なので他要素と重ならない)。
        private int _insertIndicatorIndex = -1; // -1 = 非表示

        public LaneRowPanel(bool draggable)
        {
            Orientation = Orientation.Horizontal;
            Background = Brushes.Black; // 2026-07-26: 明るい色見本の視認性対応
            if (!draggable) return;
            AllowDrop = true;
            PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewDragOver += OnPreviewDragOver;
            DragLeave += (_, _) => HideInsertIndicator();
            Drop += OnDrop;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); // Backgroundの塗りつぶし
            if (_insertIndicatorIndex < 0) return;
            double x = ComputeInsertIndicatorX(_insertIndicatorIndex);
            dc.DrawRectangle(InsertIndicatorBrush, null, new Rect(x - 1, 0, 2, ActualHeight));
        }

        private double ComputeInsertIndicatorX(int index)
        {
            if (Children.Count == 0) return 0;
            if (index <= 0) return ((FrameworkElement)Children[0]).TranslatePoint(new Point(0, 0), this).X;
            if (index >= Children.Count)
            {
                var last = (FrameworkElement)Children[^1];
                return last.TranslatePoint(new Point(0, 0), this).X + last.ActualWidth;
            }
            return ((FrameworkElement)Children[index]).TranslatePoint(new Point(0, 0), this).X;
        }

        private void ShowInsertIndicator(int index)
        {
            if (_insertIndicatorIndex == index) return;
            _insertIndicatorIndex = index;
            InvalidateVisual();
        }

        private void HideInsertIndicator()
        {
            if (_insertIndicatorIndex < 0) return;
            _insertIndicatorIndex = -1;
            InvalidateVisual();
        }

        public int Count => Children.Count;

        public void SetLanes(IReadOnlyList<LaneDef> lanes, IReadOnlyList<int> order)
        {
            Children.Clear();
            foreach (var originalIndex in order)
                Children.Add(MakeCell(lanes[originalIndex], originalIndex));
        }

        /// <summary>現在の左から右の並びを、各セルが保持する元レーンインデックスの配列として返す
        /// (=そのままLaneMapping)。</summary>
        public List<int> CurrentOrderOriginalIndices() =>
            Children.Cast<FrameworkElement>().Select(c => (int)c.Tag!).ToList();

        private static Border MakeCell(LaneDef lane, int originalIndex)
        {
            var cell = new Border
            {
                Tag = originalIndex,
                Width = 56,
                Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            var stack = new StackPanel();
            stack.Children.Add(new LaneIconElement(lane, 40));
            stack.Children.Add(new TextBlock
            {
                Text = lane.LaneId,
                FontSize = 10,
                Foreground = Brushes.White, // 2026-07-26: 背景を黒にしたため白文字に変更
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            cell.Child = stack;
            return cell;
        }

        private static Border? FindCellAncestor(DependencyObject? source)
        {
            while (source is not null and not Border) source = VisualTreeHelper.GetParent(source);
            return source as Border;
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var cell = FindCellAncestor(e.OriginalSource as DependencyObject);
            if (cell is null) { _dragSourceIndex = -1; return; }
            _dragStart = e.GetPosition(null);
            _dragSourceIndex = Children.IndexOf(cell);
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            int from = _dragSourceIndex;
            _dragSourceIndex = -1;
            DragDrop.DoDragDrop(this, from, DragDropEffects.Move);
            // DoDragDropはドラッグ操作が終わるまで戻らないため、終了理由によらずここで確実にインジケータを消す
            // (Drop/DragLeaveの呼び忘れ経路をカバーする保険、MainWindowのタブ入替と同じ考え方)。
            HideInsertIndicator();
        }

        /// <summary>マウス位置(this基準の座標)から「挿入先index」(0～Children.Count、Countなら末尾へ挿入)を
        /// 判定する(2026-08-08要望対応、MainWindow.xaml.csのComputeTabInsertIndexと同じ方式)。</summary>
        private int ComputeInsertIndex(Point posOnPanel, DependencyObject? hitSource)
        {
            int count = Children.Count;
            if (count == 0) return 0;

            var cell = FindCellAncestor(hitSource);
            if (cell is not null)
            {
                int idx = Children.IndexOf(cell);
                if (idx < 0) return count;
                double cellLeft = cell.TranslatePoint(new Point(0, 0), this).X;
                double midX = cellLeft + cell.ActualWidth / 2.0;
                return posOnPanel.X < midX ? idx : idx + 1;
            }

            // セルそのものには乗っていない(行の余白部分)。先頭セルとの位置関係で判定する。
            if (Children[0] is FrameworkElement first && posOnPanel.X < first.TranslatePoint(new Point(0, 0), this).X)
                return 0;
            return count;
        }

        private void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(int)))
            {
                e.Effects = DragDropEffects.None;
                HideInsertIndicator();
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.Move;
            int insertIndex = ComputeInsertIndex(e.GetPosition(this), e.OriginalSource as DependencyObject);
            ShowInsertIndicator(insertIndex);
            e.Handled = true;
        }

        /// <summary>2026-08-08要望対応: 「離した場所のセルと入替え」ではなく「離した位置(カーソルがセルの
        /// 左右どちらの半分にあるか)に挿入」する(MainWindowの難易度タブ入替と同じ方式)。</summary>
        private void OnDrop(object sender, DragEventArgs e)
        {
            HideInsertIndicator();
            if (!e.Data.GetDataPresent(typeof(int))) return;
            int from = (int)e.Data.GetData(typeof(int));
            if (from < 0 || from >= Children.Count) return;

            int rawTarget = ComputeInsertIndex(e.GetPosition(this), e.OriginalSource as DependencyObject);
            int to = rawTarget > from ? rawTarget - 1 : rawTarget; // fromを取り除いた後のインデックスへ変換
            if (to == from) return;

            var moving = Children[from];
            Children.RemoveAt(from);
            Children.Insert(to, moving);
        }
    }

    /// <summary>1レーン分のプレビューアイコン(ChartCanvas.DrawLaneIconを再利用、テンプレート編集
    /// ウィンドウのプレビューと同じ見た目・色規則にする)。</summary>
    private sealed class LaneIconElement(LaneDef lane, double size) : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(size, size);

        protected override void OnRender(DrawingContext dc)
        {
            var color = ChartCanvas.PreviewSampleColorForGroup(lane.ColorGroup);
            ChartCanvas.DrawLaneIcon(dc, lane, size / 2, size / 2, size, color);
        }
    }
}
