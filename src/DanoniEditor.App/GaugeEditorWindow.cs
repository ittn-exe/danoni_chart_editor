using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// customGauge/gaugeXXX(仕様dos-h0053/dos-h0022)の編集ウィンドウ(2026-08-01、2026-08-05再設計)。
/// 設定メニューから開く。ChartProjectを直接編集するのではなく編集用コピー(VM)上で作業し、
/// 「保存」時にのみ project.Tabs[].Gauge / project.GaugeParams / project.GaugeRawOverrideText へ反映する
/// (テンプレ編集・マクロ編集ウィンドウと同じ「保存確定まではキャンセル可能」の方針)。
///
/// 2026-08-05再設計(ユーザー確定仕様): danoni_main.js(resetCustomGauge/getGaugeSetting)を確認した結果、
/// 「difDataのborder/recovery/damage/initLife%(本体ゲージ)」と「customGauge/gaugeXXX(切替候補ゲージ)」は
/// 排他ではなく併存する別機能だと判明したため、「対象の譜面(タブ)を選び、その譜面の設定をまとめて行う」
/// UIへ再構成した。TabControl(_tabGaugeTabs)がその「対象譜面選択」を兼ねる(GaugeCalculatorWindowが
/// SelectionChangedを購読して追随する既存の仕組みをそのまま流用)。各TabItem内には
///   - 本体ゲージ(difData直接指定、旧④): ノルマ(x指定/数値+数値欄)・回復量・ダメージ・初期ライフを
///     独立した入力欄で編集する(以前は1本の生CSV欄だった)。上書きしない場合はチェックを外せば
///     DifDataExtraは書き出されず、本体既定値が使われる。
///   - 切替候補ゲージ(customGauge、旧①): 「指定しない」「継承キーワード」「明示リスト」の3択。
/// を配置する。ゲージ名(gaugeXXX)自体はプロジェクト全体で共有される情報のため、TabControlの外側に
/// 「ゲージ別パラメータ」表(旧②、名前の新規作成/削除も含む)として残す。
/// 「直接入力モード」(旧③、過去資産からのコピペ用)は末尾に残置。空でなければ本体ゲージ・切替候補ゲージ
/// いずれも完全に無視してこのテキストをそのままdos.txtへ出力する(DosExporter.AppendGaugeHeaders参照)。
/// 「dos作成後に直接編集する」(2026-08-05)がON中は、上記すべてを無効化しエクスポートも一切行わない。
/// </summary>
internal sealed class GaugeEditorWindow : Window
{
    private static readonly string[] InheritKeywords = ["survival", "border", "customDefault"];

    private readonly ChartProject _project;
    private readonly List<TabGaugeVm> _tabVms;
    private readonly List<ParamRowVm> _paramRows;
    /// <summary>本体ゲージ(difData直接指定)のタブごとの入力値(2026-08-05再設計、旧・生CSV欄を
    /// フィールドごとの入力欄+チェックボックスへ分解したもの)</summary>
    private readonly List<DifDataExtraVm> _difDataVms;

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
        Text = "直接入力が優先されています(上の譜面ごとの設定・ゲージ別パラメータは無視されます)。",
        Foreground = Brushes.OrangeRed,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 4, 0, 4),
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>「dos作成後に直接編集する」フラグ(2026-08-05)。ON中は①②③④すべて無効化し、
    /// エクスポート時もゲージ関連ヘッダーを一切出力しない(ChartProject.GaugeManualEditAfterExport参照)。</summary>
    private readonly CheckBox _manualEditAfterExport = new()
    {
        Content = "dos作成後に直接編集する(このエディタでは触らず、書き出し後のdos.txtへ自分で追記する)",
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 8),
    };

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
        // 2026-07-24: ゲージ別パラメータの実値はDifficultyTab.GaugeParams(タブごと)に持たせる方式へ変更。
        // GaugeNamesは行の並び順(名前一覧)のみを保持するプロジェクト全体の情報。
        _paramRows = project.GaugeNames.Select(name => new ParamRowVm
        {
            GaugeName = name,
            PerTabCsv = project.Tabs.Select(t => t.GaugeParams is { } gp && gp.TryGetValue(name, out var csv) ? csv : "").ToList(),
        }).ToList();
        _difDataVms = project.Tabs.Select(t => DifDataExtraVm.Parse(t.DifDataExtra)).ToList();

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

        _manualEditAfterExport.IsChecked = project.GaugeManualEditAfterExport;
        _manualEditAfterExport.Checked += (_, _) => UpdateRawActiveState();
        _manualEditAfterExport.Unchecked += (_, _) => UpdateRawActiveState();
        outer.Children.Add(_manualEditAfterExport);

        outer.Children.Add(SectionLabel("譜面(難易度)ごとのゲージ設定"));
        outer.Children.Add(new TextBlock
        {
            Text = "対象の譜面をタブで選び、その譜面の本体ゲージ・切替候補ゲージを設定してくださいまし。",
            Margin = new Thickness(0, 0, 0, 6),
        });
        BuildTabGaugeTabs();
        outer.Children.Add(_tabGaugeTabs);

        outer.Children.Add(SectionLabel("ゲージ別パラメータ (gaugeXXX、プロジェクト全体で共有)"));
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

        outer.Children.Add(SectionLabel("直接入力モード(過去資産からのコピペ用)"));
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
        bool manualEdit = _manualEditAfterExport.IsChecked == true;
        // 2026-08-05再設計: _tabGaugeTabsが「対象譜面選択+本体ゲージ+切替候補ゲージ」をすべて
        // 内包するため、これを無効化するだけで両方まとめて無効化される。
        bool rawActive = !manualEdit && !string.IsNullOrWhiteSpace(_rawOverrideBox.Text);
        _rawActiveNotice.Visibility = rawActive ? Visibility.Visible : Visibility.Collapsed;
        _tabGaugeTabs.IsEnabled = !manualEdit && !rawActive;
        _paramTablePanel.IsEnabled = !manualEdit && !rawActive;
        _addParamButton.IsEnabled = !manualEdit && !rawActive;
        // 2026-08-05: 「dos作成後に直接編集する」がON中は直接入力モードも無効化する
        // (エディタでは一切触らせない、というユーザー確定仕様のため)。
        _rawOverrideBox.IsEnabled = !manualEdit;
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

    // =====================================================================
    // 本体ゲージ(difData直接指定、旧④、2026-08-05再設計でタブパネル内へ統合)
    // =====================================================================

    /// <summary>本体ゲージ(difDataのborder/recovery/damage/initLife%)のタブごとの入力値。
    /// Enabled=falseの間はDifDataExtraを出力しない(本体既定値が使われる)。ノルマはdanoniplus側で
    /// "x"という特殊キーワードを受け付ける(danoni_main.js getGaugeSetting確認済み)ため、
    /// 数値入力とx指定をラジオボタンで切り替えられるようにしている。</summary>
    internal sealed class DifDataExtraVm
    {
        public bool Enabled;
        public bool BorderIsX;
        public string BorderValue = "70";
        public string Recovery = "6";
        public string Damage = "40";
        public string InitLife = "";

        public static DifDataExtraVm Parse(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new DifDataExtraVm { Enabled = false };
            var parts = csv.Split(',');
            string border = parts.Length > 0 ? parts[0].Trim() : "";
            bool isX = border == "x";
            return new DifDataExtraVm
            {
                Enabled = true,
                BorderIsX = isX,
                BorderValue = isX || border.Length == 0 ? "70" : border,
                Recovery = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "6",
                Damage = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "40",
                InitLife = parts.Length > 3 ? parts[3] : "",
            };
        }

        /// <summary>Enabled=falseなら空文字(=出力なし)、trueならCSVを組み立てる。初期ライフのみ
        /// 空欄可(danoniplus側で末尾フィールド省略時は本体既定のinitLifeが使われるため)。</summary>
        public string ToCsv()
        {
            if (!Enabled) return "";
            string border = BorderIsX ? "x" : BorderValue;
            string csv = $"{border},{Recovery},{Damage}";
            return string.IsNullOrWhiteSpace(InitLife) ? csv : $"{csv},{InitLife}";
        }
    }

    /// <summary>本体ゲージ(difData)の入力欄一式を組み立てる(2026-08-05)。</summary>
    private FrameworkElement BuildDifDataExtraFields(int tabIndex)
    {
        var vm = _difDataVms[tabIndex];
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var enabledCheck = new CheckBox
        {
            Content = "本体ゲージ(border/recovery/damage/initLife%)を上書きする(OFF=本体既定値を使用)",
            IsChecked = vm.Enabled,
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(enabledCheck);

        var fieldsPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        panel.Children.Add(fieldsPanel);

        var borderXRadio = new RadioButton { Content = "x指定", GroupName = $"border_{tabIndex}", Margin = new Thickness(0, 0, 12, 0), IsChecked = vm.BorderIsX };
        var borderNumRadio = new RadioButton { Content = "数値", GroupName = $"border_{tabIndex}", IsChecked = !vm.BorderIsX };
        var borderValueBox = new TextBox { Width = 80, Text = vm.BorderValue, Margin = new Thickness(8, 0, 0, 0) };
        var borderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        borderRow.Children.Add(new TextBlock { Text = "ノルマ(0-100):", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        borderRow.Children.Add(borderXRadio);
        borderRow.Children.Add(borderNumRadio);
        borderRow.Children.Add(borderValueBox);
        fieldsPanel.Children.Add(borderRow);

        var recoveryBox = new TextBox { Width = 80, Text = vm.Recovery };
        fieldsPanel.Children.Add(LabeledFieldRow("回復量:", recoveryBox));

        var damageBox = new TextBox { Width = 80, Text = vm.Damage };
        fieldsPanel.Children.Add(LabeledFieldRow("ダメージ:", damageBox));

        var initLifeBox = new TextBox { Width = 80, Text = vm.InitLife };
        fieldsPanel.Children.Add(LabeledFieldRow("初期ライフ(空欄可):", initLifeBox));

        void RefreshEnabledState()
        {
            fieldsPanel.IsEnabled = vm.Enabled;
            borderValueBox.IsEnabled = !vm.BorderIsX;
        }

        enabledCheck.Checked += (_, _) => { vm.Enabled = true; RefreshEnabledState(); };
        enabledCheck.Unchecked += (_, _) => { vm.Enabled = false; RefreshEnabledState(); };
        borderXRadio.Checked += (_, _) => { vm.BorderIsX = true; RefreshEnabledState(); };
        borderNumRadio.Checked += (_, _) => { vm.BorderIsX = false; RefreshEnabledState(); };
        borderValueBox.TextChanged += (_, _) => vm.BorderValue = borderValueBox.Text;
        recoveryBox.TextChanged += (_, _) => vm.Recovery = recoveryBox.Text;
        damageBox.TextChanged += (_, _) => vm.Damage = damageBox.Text;
        initLifeBox.TextChanged += (_, _) => vm.InitLife = initLifeBox.Text;

        RefreshEnabledState();
        return panel;
    }

    private static StackPanel LabeledFieldRow(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    // =====================================================================
    // ① 難易度タブ別ゲージ名リスト(customGauge、切替候補ゲージ)
    // =====================================================================

    private void BuildTabGaugeTabs()
    {
        _tabGaugeTabs.Items.Clear();
        for (int i = 0; i < _project.Tabs.Count; i++)
        {
            var tab = _project.Tabs[i];
            var vm = _tabVms[i];

            var panel = new StackPanel { Margin = new Thickness(8) };

            panel.Children.Add(SectionLabel("本体ゲージ(difDataへの直接指定)"));
            panel.Children.Add(BuildDifDataExtraFields(i));

            panel.Children.Add(SectionLabel("切替候補ゲージ(customGauge)"));
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

        // 2026-07-24: ②の表内容(行=ゲージ名, 列=タブ)を、ChartProject.GaugeNames(並び順)と
        // 各DifficultyTab.GaugeParams(タブごとの実値)へ分解して書き戻す。
        List<ParamRowVm> validRows = rawText is not null
            ? []
            : _paramRows.Where(r => !string.IsNullOrWhiteSpace(r.GaugeName)).ToList();

        _project.GaugeNames = validRows.Select(r => r.GaugeName.Trim()).ToList();
        for (int i = 0; i < _project.Tabs.Count; i++)
        {
            Dictionary<string, string>? gp = null;
            foreach (var row in validRows)
            {
                var csv = i < row.PerTabCsv.Count ? row.PerTabCsv[i] : "";
                if (string.IsNullOrEmpty(csv)) continue;
                (gp ??= []).Add(row.GaugeName.Trim(), csv);
            }
            _project.Tabs[i].GaugeParams = gp;
        }

        _project.GaugeRawOverrideText = rawText;

        // 2026-08-05: 本体ゲージ(difData直接指定、名前を介さないborder/recovery/damage/initLife%生値)の書き戻し。
        // 直接入力モード(customGauge/gaugeXXX)とは無関係な別ヘッダーのため、rawTextの有無を問わず常に反映する。
        for (int i = 0; i < _project.Tabs.Count; i++)
        {
            var csv = _difDataVms[i].ToCsv();
            _project.Tabs[i].DifDataExtra = string.IsNullOrEmpty(csv) ? null : csv;
        }

        // 2026-08-05: 「dos作成後に直接編集する」フラグの書き戻し(プロジェクト全体で1つ)。
        _project.GaugeManualEditAfterExport = _manualEditAfterExport.IsChecked == true;

        Saved = true;
        DialogResult = true;
    }
}
