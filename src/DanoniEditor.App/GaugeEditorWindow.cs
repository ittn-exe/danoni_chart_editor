using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// customGauge/gaugeXXX(仕様dos-h0053/dos-h0022)の編集ウィンドウ(2026-08-01)。
/// 設定メニューから開く。ChartProjectを直接編集するのではなく編集用コピー(VM)上で作業し、
/// 「保存」時にのみ project.Tabs[].Gauge / project.GaugeParams / project.GaugeRawOverrideText へ反映する
/// (テンプレ編集・マクロ編集ウィンドウと同じ「保存確定まではキャンセル可能」の方針)。
/// - ①難易度タブ別ゲージ名リスト: タブごとにTabItemを持ち、「指定しない」「継承キーワード」
///   「明示リスト」の3択(ユーザー確定仕様: 難易度ごとに異なるリストを完全サポート)。
/// - ②ゲージ別パラメータ: プロジェクト全体で共有するゲージ名→タブ数分のCSV行のテーブル。
/// - ③直接入力モード(ユーザー確定仕様、2026-08-01): プロジェクト全体で1つのテキスト欄。
///   空でなければ①②の内容を完全に無視し、このテキストをそのままdos.txtへ出力する
///   (DosExporter.AppendGaugeHeaders参照)。内容がある間は①②のパネルを無効化して事故を防ぐ。
/// </summary>
internal sealed class GaugeEditorWindow : Window
{
    private static readonly string[] InheritKeywords = ["survival", "border", "customDefault"];

    private readonly ChartProject _project;
    private readonly List<TabGaugeVm> _tabVms;
    private readonly List<ParamRowVm> _paramRows;

    private readonly TextBox _rawOverrideBox = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        Height = 140,
    };

    private readonly TextBlock _rawActiveNotice = new()
    {
        Text = "直接入力が優先されています(①②は無視されます)。",
        Foreground = Brushes.OrangeRed,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 4, 0, 4),
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private readonly TabControl _tabGaugeTabs = new();
    private readonly StackPanel _paramTablePanel = new();
    private readonly Button _addParamButton = new() { Content = "ゲージ名を追加", Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>2026-08-01: ゲージ計算機(モードレス、開いている間は①のタブ切替に追随する)。
    /// 既に開いている場合は再利用してActivate()するのみにする。</summary>
    private GaugeCalculatorWindow? _calculatorWindow;

    /// <summary>保存に成功したかどうか(呼び出し元がNotifyChanged等を行う目安)</summary>
    public bool Saved { get; private set; }

    // --- GaugeCalculatorWindowから参照するための内部アクセサ ---
    internal ChartProject ProjectRef => _project;
    internal TabControl TabGaugeTabsControl => _tabGaugeTabs;
    internal List<ParamRowVm> ParamRowsRef => _paramRows;
    internal List<TabGaugeVm> TabVmsRef => _tabVms;
    internal void RefreshParamTableExternal() => RefreshParamTable();

    public GaugeEditorWindow(ChartProject project)
    {
        _project = project;

        Title = "ゲージ設定編集(customGauge / gaugeXXX)";
        Width = 920;
        Height = 720;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        _tabVms = project.Tabs.Select(t => TabGaugeVm.FromGaugeConfig(t.Gauge)).ToList();
        _paramRows = project.GaugeParams.Select(kv => new ParamRowVm
        {
            GaugeName = kv.Key,
            PerTabCsv = Enumerable.Range(0, project.Tabs.Count)
                .Select(i => i < kv.Value.PerTabCsv.Count ? kv.Value.PerTabCsv[i] : "")
                .ToList(),
        }).ToList();

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

        var outer = new StackPanel { Margin = new Thickness(12) };

        outer.Children.Add(SectionLabel("① 難易度タブ別ゲージ名リスト (customGauge)"));
        BuildTabGaugeTabs();
        outer.Children.Add(_tabGaugeTabs);

        outer.Children.Add(SectionLabel("② ゲージ別パラメータ (gaugeXXX)"));
        outer.Children.Add(new TextBlock
        {
            Text = "各セルは「ノルマ(またはx固定),回復,ダメージ,初期ライフ」のCSVで入力してくださいまし。" +
                   "空欄のタブは先頭タブと同じ値が使われます(本体側の既定フォールバック動作)。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        RefreshParamTable();
        var paramScroll = new ScrollViewer
        {
            Content = _paramTablePanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        outer.Children.Add(paramScroll);
        _addParamButton.Click += (_, _) =>
        {
            _paramRows.Add(new ParamRowVm { GaugeName = "", PerTabCsv = Enumerable.Repeat("", _project.Tabs.Count).ToList() });
            RefreshParamTable();
        };
        var openCalculatorButton = new Button
        {
            Content = "ゲージ計算機を開く...", Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 0, 0),
        };
        openCalculatorButton.Click += OpenCalculator_Click;
        var addParamRow = new StackPanel { Orientation = Orientation.Horizontal };
        addParamRow.Children.Add(_addParamButton);
        addParamRow.Children.Add(openCalculatorButton);
        outer.Children.Add(addParamRow);
        outer.Children.Add(new TextBlock
        {
            Text = "計算機は①のタブで選択中の難易度タブを対象に動作します(タブを切り替えると自動で再計算されます)。",
            Foreground = Brushes.Gray,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 2, 0, 0),
        });

        outer.Children.Add(SectionLabel("③ 直接入力モード(過去資産からのコピペ用)"));
        outer.Children.Add(new TextBlock
        {
            Text = "dos.txtのゲージ関連ヘッダー行(例: |customGauge=...|、|gaugeOriginal=...|)をそのまま貼り付けてくださいまし。" +
                   "ここに空白以外の内容がある間は①②の設定より常にこちらが優先され、①②は編集不可になります。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        outer.Children.Add(_rawActiveNotice);
        _rawOverrideBox.Text = project.GaugeRawOverrideText ?? "";
        _rawOverrideBox.TextChanged += (_, _) => UpdateRawActiveState();
        outer.Children.Add(_rawOverrideBox);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
        root.Children.Add(scroll);

        Content = root;
        UpdateRawActiveState();

        Closed += (_, _) => _calculatorWindow?.Close();
    }

    /// <summary>「ゲージ計算機を開く...」ボタン(2026-08-01)。モードレスウィンドウとして開き、
    /// 既に開いている場合は前面に出すだけにする(複数出さない)。</summary>
    private void OpenCalculator_Click(object sender, RoutedEventArgs e)
    {
        if (_calculatorWindow is { IsVisible: true })
        {
            _calculatorWindow.Activate();
            return;
        }
        _calculatorWindow = new GaugeCalculatorWindow(this) { Owner = this };
        _calculatorWindow.Closed += (_, _) => _calculatorWindow = null;
        _calculatorWindow.Show();
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 12, 0, 6),
    };

    private void UpdateRawActiveState()
    {
        bool rawActive = !string.IsNullOrWhiteSpace(_rawOverrideBox.Text);
        _rawActiveNotice.Visibility = rawActive ? Visibility.Visible : Visibility.Collapsed;
        _tabGaugeTabs.IsEnabled = !rawActive;
        _paramTablePanel.IsEnabled = !rawActive;
        _addParamButton.IsEnabled = !rawActive;
    }

    // =====================================================================
    // ① 難易度タブ別ゲージ名リスト
    // =====================================================================

    internal sealed class TabGaugeVm
    {
        public string Mode = "none"; // "none" | "inherit" | "list"
        public string InheritKeyword = InheritKeywords[0];
        public List<EntryVm> Entries = [];

        public static TabGaugeVm FromGaugeConfig(GaugeConfig? g)
        {
            if (g is null) return new TabGaugeVm();
            if (!string.IsNullOrEmpty(g.InheritKeyword))
                return new TabGaugeVm { Mode = "inherit", InheritKeyword = g.InheritKeyword };
            return new TabGaugeVm
            {
                Mode = "list",
                Entries = g.Entries.Select(e => new EntryVm { Name = e.Name, IsVariable = e.IsVariable, DisplayName = e.DisplayName ?? "" }).ToList(),
            };
        }
    }

    internal sealed class EntryVm
    {
        public string Name = "";
        public bool IsVariable;
        public string DisplayName = "";
    }

    private void BuildTabGaugeTabs()
    {
        _tabGaugeTabs.Items.Clear();
        for (int i = 0; i < _project.Tabs.Count; i++)
        {
            var tab = _project.Tabs[i];
            var vm = _tabVms[i];

            var panel = new StackPanel { Margin = new Thickness(8) };

            var modeCombo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            modeCombo.Items.Add("指定しない(本体既定を使用)");
            modeCombo.Items.Add("継承キーワードを使用");
            modeCombo.Items.Add("明示リストを指定");
            modeCombo.SelectedIndex = vm.Mode switch { "inherit" => 1, "list" => 2, _ => 0 };
            panel.Children.Add(modeCombo);

            var inheritCombo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), ItemsSource = InheritKeywords, SelectedItem = vm.InheritKeyword };
            inheritCombo.SelectionChanged += (_, _) => vm.InheritKeyword = inheritCombo.SelectedItem as string ?? InheritKeywords[0];
            panel.Children.Add(inheritCombo);

            var entriesPanel = new StackPanel();
            panel.Children.Add(entriesPanel);

            var addEntryButton = new Button { Content = "ゲージを追加", Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(addEntryButton);

            void RefreshVisibility()
            {
                inheritCombo.Visibility = vm.Mode == "inherit" ? Visibility.Visible : Visibility.Collapsed;
                entriesPanel.Visibility = vm.Mode == "list" ? Visibility.Visible : Visibility.Collapsed;
                addEntryButton.Visibility = vm.Mode == "list" ? Visibility.Visible : Visibility.Collapsed;
            }

            void RefreshEntries()
            {
                entriesPanel.Children.Clear();
                foreach (var entry in vm.Entries)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                    var nameBox = new TextBox { Width = 120, Text = entry.Name, ToolTip = "ゲージ名(例: Original, Heavy, 独自名)" };
                    nameBox.TextChanged += (_, _) => entry.Name = nameBox.Text;
                    var varCheck = new CheckBox { Content = "V(可変)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), IsChecked = entry.IsVariable };
                    varCheck.Checked += (_, _) => entry.IsVariable = true;
                    varCheck.Unchecked += (_, _) => entry.IsVariable = false;
                    var dispBox = new TextBox { Width = 140, Text = entry.DisplayName, ToolTip = "表示名(空欄可)" };
                    dispBox.TextChanged += (_, _) => entry.DisplayName = dispBox.Text;
                    var removeButton = new Button { Content = "削除", Width = 50, Margin = new Thickness(8, 0, 0, 0) };
                    removeButton.Click += (_, _) => { vm.Entries.Remove(entry); RefreshEntries(); };

                    row.Children.Add(nameBox);
                    row.Children.Add(varCheck);
                    row.Children.Add(dispBox);
                    row.Children.Add(removeButton);
                    entriesPanel.Children.Add(row);
                }
            }

            modeCombo.SelectionChanged += (_, _) =>
            {
                vm.Mode = modeCombo.SelectedIndex switch { 1 => "inherit", 2 => "list", _ => "none" };
                RefreshVisibility();
            };
            addEntryButton.Click += (_, _) => { vm.Entries.Add(new EntryVm()); RefreshEntries(); };

            RefreshVisibility();
            RefreshEntries();

            _tabGaugeTabs.Items.Add(new TabItem { Header = tab.DisplayLabel, Content = panel });
        }
    }

    // =====================================================================
    // ② ゲージ別パラメータ
    // =====================================================================

    internal sealed class ParamRowVm
    {
        public string GaugeName = "";
        public List<string> PerTabCsv = [];
    }

    private void RefreshParamTable()
    {
        _paramTablePanel.Children.Clear();

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(new TextBlock { Text = "ゲージ名", Width = 110, FontWeight = FontWeights.Bold });
        for (int i = 0; i < _project.Tabs.Count; i++)
            headerRow.Children.Add(new TextBlock { Text = _project.Tabs[i].DisplayLabel, Width = 110, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        headerRow.Children.Add(new TextBlock { Text = "", Width = 50 });
        _paramTablePanel.Children.Add(headerRow);

        foreach (var row in _paramRows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var nameBox = new TextBox { Width = 110, Text = row.GaugeName };
            nameBox.TextChanged += (_, _) => row.GaugeName = nameBox.Text;
            rowPanel.Children.Add(nameBox);

            for (int i = 0; i < _project.Tabs.Count; i++)
            {
                int idx = i;
                var cell = new TextBox
                {
                    Width = 105,
                    Margin = new Thickness(2, 0, 2, 0),
                    Text = idx < row.PerTabCsv.Count ? row.PerTabCsv[idx] : "",
                    ToolTip = "ノルマ(またはx),回復,ダメージ,初期ライフ(空欄=先頭タブと同じ)",
                };
                cell.TextChanged += (_, _) =>
                {
                    while (row.PerTabCsv.Count <= idx) row.PerTabCsv.Add("");
                    row.PerTabCsv[idx] = cell.Text;
                };
                rowPanel.Children.Add(cell);
            }

            var removeButton = new Button { Content = "削除", Width = 50, Margin = new Thickness(2, 0, 0, 0) };
            removeButton.Click += (_, _) => { _paramRows.Remove(row); RefreshParamTable(); };
            rowPanel.Children.Add(removeButton);

            _paramTablePanel.Children.Add(rowPanel);
        }
    }

    // =====================================================================
    // 保存
    // =====================================================================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";

        string? rawText = string.IsNullOrWhiteSpace(_rawOverrideBox.Text) ? null : _rawOverrideBox.Text;

        var gaugeConfigs = new GaugeConfig?[_project.Tabs.Count];
        if (rawText is null)
        {
            for (int i = 0; i < _tabVms.Count; i++)
            {
                var vm = _tabVms[i];
                switch (vm.Mode)
                {
                    case "none":
                        gaugeConfigs[i] = null;
                        break;
                    case "inherit":
                        gaugeConfigs[i] = new GaugeConfig { InheritKeyword = vm.InheritKeyword };
                        break;
                    case "list":
                        foreach (var entry in vm.Entries)
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name))
                            {
                                _error.Text = $"'{_project.Tabs[i].DisplayLabel}': ゲージ名が未入力の項目がありますの。";
                                return;
                            }
                        }
                        gaugeConfigs[i] = new GaugeConfig
                        {
                            Entries = vm.Entries.Select(e => new GaugeListEntry(
                                e.Name.Trim(), e.IsVariable,
                                string.IsNullOrWhiteSpace(e.DisplayName) ? null : e.DisplayName.Trim())).ToList(),
                        };
                        break;
                }
            }

            var names = _paramRows.Where(r => !string.IsNullOrWhiteSpace(r.GaugeName)).Select(r => r.GaugeName.Trim()).ToList();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                _error.Text = "ゲージ別パラメータのゲージ名が重複していますの。";
                return;
            }
        }

        for (int i = 0; i < _project.Tabs.Count; i++)
            _project.Tabs[i].Gauge = rawText is null ? gaugeConfigs[i] : null;

        _project.GaugeParams = rawText is not null
            ? []
            : _paramRows
                .Where(r => !string.IsNullOrWhiteSpace(r.GaugeName))
                .ToDictionary(r => r.GaugeName.Trim(), r => new GaugeParamSet { PerTabCsv = r.PerTabCsv.ToList() });

        _project.GaugeRawOverrideText = rawText;

        Saved = true;
        DialogResult = true;
    }
}
