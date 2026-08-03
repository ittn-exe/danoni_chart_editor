using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// customGauge/gaugeXXX(仕様dos-h0053/dos-h0022)の編集ウィンドウ(2026-07-26新設、2026-07-26/
/// 2026-07-30の2度再設計)。ChartProjectを直接編集するのではなく編集用コピー(VM)上で作業し、
/// 「保存」時にのみ project.Tabs[].Gauge / project.GaugeNames / project.GaugeRawOverrideText へ反映する
/// (テンプレ編集・マクロ編集ウィンドウと同じ「保存確定まではキャンセル可能」の方針)。
///
/// 2026-07-30再設計(ユーザー確定仕様、以前のUIが分かりづらいとの指摘を受けて全面刷新):
/// - 「本体ゲージ(difData直接指定)」と「ゲージセット設定(customGauge)」は排他ではなく併用可能な
///   別機能(danoni_main.jsのresetCustomGauge/getGaugeSetting確認済み)なので、無理に排他UIにはせず、
///   タブ内で視覚的に分離しつつ両方常に編集できるようにする。
/// - gaugeXXX(①宣言リスト)は「内部名+既定表示名」をまず宣言し、値は「全譜面で共有」/「譜面毎に
///   変更」を選べる。共有時は宣言と同時にその場で値を入力でき、個別時は各タブ側で値を入力する。
/// - 各タブの「ゲージセット設定」は「設定しない」/「カスタムゲージを使用」/「本体の既存セットを使用」
///   の3択。カスタムゲージ選択時は、①で宣言済みの名前を「採用中」⇄「追加できる」の2群で相互にやり取り
///   できるリスト形式にする(継承キーワードsurvival/border/customDefaultは、danoni_main.js上
///   customGauge{N}の値全体が完全一致した場合のみ有効になる別軸の設定のため、リストへは混在させず
///   独立した選択肢として扱う)。
/// - 上級者向けの「直接入力モード」は、「記述からゲージを取得」(貼り付けたテキストを解析して上の
///   構造化UIへ一括反映するインポート専用、以後は構造化UIがそのままexportに使われる)と
///   「エクスポート時にdosへ直接反映する」(従来のGaugeRawOverrideTextと同じ、構造化UIを完全に無視して
///   このテキストをそのまま出力)の2モードに分け、常時「非空なら優先」という分かりにくい暗黙優先を廃止した。
/// </summary>
internal sealed class GaugeEditorWindow : Window
{
    /// <summary>本体内蔵ゲージセット(danoni_main.js resetCustomGauge確認済み、customGauge{N}の値
    /// 全体がこのいずれかと完全一致した場合のみキーワードとして解釈される)。Value=dos.txtへ書き出す
    /// 実際の値、Label=UI表示用の分かりやすい表記。</summary>
    private static readonly (string Value, string Label)[] BuiltinGaugeSets =
    [
        ("survival", "サバイバル(survival)"),
        ("border", "ボーダー(border)"),
        ("customDefault", "難易度別既定(customDefault)"),
    ];
    private static readonly string[] InheritKeywords = [.. BuiltinGaugeSets.Select(b => b.Value)];

    private readonly ChartProject _project;
    private readonly List<TabGaugeVm> _tabVms;
    private readonly List<ParamRowVm> _paramRows;
    /// <summary>本体ゲージ(difData直接指定)のタブごとの入力値</summary>
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

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>「dos作成後に直接編集する」フラグ。ON中は①②③④すべて無効化し、エクスポート時も
    /// ゲージ関連ヘッダーを一切出力しない(ChartProject.GaugeManualEditAfterExport参照)。</summary>
    private readonly CheckBox _manualEditAfterExport = new()
    {
        Content = "dos作成後に直接編集する(このエディタでは触らず、書き出し後のdos.txtへ自分で追記する)",
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private readonly TabControl _tabGaugeTabs = new();
    /// <summary>①gaugeX宣言リストの描画先(2026-07-30再設計、単一列のリスト形式)。</summary>
    private readonly StackPanel _gaugeDeclPanel = new();
    private readonly Button _addParamButton = new() { Content = "ゲージ名を追加", Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>④上級者向け「直接入力モード」の2択("import"=記述から取得/"direct"=直接反映)</summary>
    private readonly RadioButton _rawModeImport = new() { Content = "記述からゲージを取得する(貼り付けて「インポート」を押すと、上の各項目へ反映されます)", GroupName = "rawMode" };
    private readonly RadioButton _rawModeDirect = new() { Content = "エクスポート時にdosへ直接反映する(上の各項目は無視され、このテキストがそのまま出力されます)", GroupName = "rawMode" };
    private readonly Button _importRawButton = new() { Content = "インポート", Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Expander _advancedExpander = new() { Header = "上級者向け: 直接入力モード(過去資産からのコピペ用)", IsExpanded = false, Margin = new Thickness(0, 14, 0, 0) };

    /// <summary>ゲージ計算機(モードレス、開いている間は①のタブ切替に追随する)。</summary>
    private GaugeCalculatorWindow? _calculatorWindow;

    /// <summary>保存に成功したかどうか(呼び出し元がNotifyChanged等を行う目安)</summary>
    public bool Saved { get; private set; }

    // --- GaugeCalculatorWindowから参照するための内部アクセサ(2026-07-30再設計後も維持) ---
    internal ChartProject ProjectRef => _project;
    internal TabControl TabGaugeTabsControl => _tabGaugeTabs;
    internal List<ParamRowVm> ParamRowsRef => _paramRows;
    internal List<TabGaugeVm> TabVmsRef => _tabVms;
    internal void RefreshParamTableExternal() => RefreshAll();

    public GaugeEditorWindow(ChartProject project)
    {
        _project = project;

        Title = "ゲージ設定編集(customGauge / gaugeXXX / difData)";
        Width = 960;
        Height = 760;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        _tabVms = project.Tabs.Select(t => TabGaugeVm.FromGaugeConfig(t.Gauge)).ToList();
        _paramRows = project.GaugeNames.Select(def => new ParamRowVm
        {
            GaugeName = def.Name,
            DisplayName = def.DisplayName ?? "",
            PerTabCsv = project.Tabs.Select(t => t.GaugeParams is { } gp && gp.TryGetValue(def.Name, out var csv) ? csv : "").ToList(),
        }).ToList();
        foreach (var row in _paramRows) row.Shared = InferShared(row, project.Tabs.Count);
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
        _manualEditAfterExport.Checked += (_, _) => UpdateEnabledState();
        _manualEditAfterExport.Unchecked += (_, _) => UpdateEnabledState();
        outer.Children.Add(_manualEditAfterExport);

        outer.Children.Add(SectionLabel("① ゲージ名の宣言(gaugeXXX、プロジェクト全体で共有)"));
        outer.Children.Add(new TextBlock
        {
            Text = "内部名(dos.txt出力用の識別子)と、任意の既定表示名をここで宣言してくださいませ。値は" +
                   "「全譜面で共有」ならここで直接入力、「譜面毎に変更」なら各難易度タブ側で入力しますの。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        outer.Children.Add(_gaugeDeclPanel);
        _addParamButton.Click += (_, _) =>
        {
            _paramRows.Add(new ParamRowVm { GaugeName = "", PerTabCsv = Enumerable.Repeat("", _project.Tabs.Count).ToList(), Shared = true });
            RefreshAll();
        };
        var openCalculatorButton = new Button { Content = "ゲージ計算機を開く...", Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 8, 0, 0) };
        openCalculatorButton.Click += OpenCalculator_Click;
        var declButtonsRow = new StackPanel { Orientation = Orientation.Horizontal };
        declButtonsRow.Children.Add(_addParamButton);
        declButtonsRow.Children.Add(openCalculatorButton);
        outer.Children.Add(declButtonsRow);

        outer.Children.Add(SectionLabel("② 難易度タブごとの設定"));
        outer.Children.Add(new TextBlock
        {
            Text = "対象の譜面をタブで選び、本体ゲージ・ゲージセット設定を行ってくださいまし(この2つは独立した" +
                   "機能で、同時に使えますの)。",
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        });
        outer.Children.Add(_tabGaugeTabs);

        _advancedExpander.Content = BuildAdvancedSection();
        outer.Children.Add(_advancedExpander);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = outer };
        root.Children.Add(scroll);

        Content = root;
        RefreshAll();
        UpdateEnabledState();

        Closed += (_, _) => _calculatorWindow?.Close();
    }

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

    /// <summary>「dos作成後に直接編集する」フラグ・直接入力モードの状態に応じて①②を有効/無効化する。</summary>
    private void UpdateEnabledState()
    {
        bool manualEdit = _manualEditAfterExport.IsChecked == true;
        bool directActive = !manualEdit && _rawModeDirect.IsChecked == true && !string.IsNullOrWhiteSpace(_rawOverrideBox.Text);
        _gaugeDeclPanel.IsEnabled = !manualEdit && !directActive;
        _addParamButton.IsEnabled = !manualEdit && !directActive;
        _tabGaugeTabs.IsEnabled = !manualEdit && !directActive;
        _importRawButton.IsEnabled = !manualEdit && _rawModeImport.IsChecked == true;
        _rawOverrideBox.IsEnabled = !manualEdit;
    }

    // =====================================================================
    // ① gaugeXXX宣言リスト(2026-07-30再設計)
    // =====================================================================

    internal sealed class ParamRowVm
    {
        public string GaugeName = "";
        /// <summary>宣言時の既定表示名(空欄可)。各タブのEntryVm.DisplayNameが空の場合のフォールバック。</summary>
        public string DisplayName = "";
        /// <summary>true=全譜面で同じ値を共有(宣言行でまとめて編集)、false=譜面毎に個別入力
        /// (エディタのUI状態のみ、プロジェクトファイルへは保存しない。読み込み時は全タブの値が
        /// 一致していれば共有とみなす)。</summary>
        public bool Shared = true;
        public List<string> PerTabCsv = [];
    }

    /// <summary>全タブの値が(空を除いて)一致していれば共有とみなす既定推測。</summary>
    private static bool InferShared(ParamRowVm row, int tabCount)
    {
        var distinctNonEmpty = Enumerable.Range(0, tabCount)
            .Select(i => i < row.PerTabCsv.Count ? row.PerTabCsv[i] : "")
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .ToList();
        return distinctNonEmpty.Count <= 1;
    }

    private void RefreshAll()
    {
        int selectedTab = _tabGaugeTabs.SelectedIndex;
        RefreshDeclarationPanel();
        BuildTabGaugeTabs();
        if (selectedTab >= 0 && selectedTab < _tabGaugeTabs.Items.Count) _tabGaugeTabs.SelectedIndex = selectedTab;
    }

    private void RefreshDeclarationPanel()
    {
        _gaugeDeclPanel.Children.Clear();

        foreach (var row in _paramRows)
        {
            var card = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8),
            };
            var cardPanel = new StackPanel();

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            headerRow.Children.Add(new TextBlock { Text = "内部名:", Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var nameBox = new TextBox { Width = 140, Text = row.GaugeName };
            nameBox.TextChanged += (_, _) => row.GaugeName = nameBox.Text;
            headerRow.Children.Add(nameBox);

            var removeButton = new Button { Content = "このゲージ名を削除", Width = 130, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            removeButton.Click += (_, _) => { _paramRows.Remove(row); RefreshAll(); };
            headerRow.Children.Add(removeButton);
            cardPanel.Children.Add(headerRow);

            var dispRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var dispCheck = new CheckBox { Content = "表示名を変える", IsChecked = !string.IsNullOrEmpty(row.DisplayName), VerticalAlignment = VerticalAlignment.Center };
            var dispBox = new TextBox { Width = 140, Text = row.DisplayName, Margin = new Thickness(8, 0, 0, 0), IsEnabled = dispCheck.IsChecked == true };
            dispCheck.Checked += (_, _) => dispBox.IsEnabled = true;
            dispCheck.Unchecked += (_, _) => { dispBox.IsEnabled = false; dispBox.Text = ""; row.DisplayName = ""; };
            dispBox.TextChanged += (_, _) => { if (dispCheck.IsChecked == true) row.DisplayName = dispBox.Text; };
            dispRow.Children.Add(dispCheck);
            dispRow.Children.Add(dispBox);
            cardPanel.Children.Add(dispRow);

            var sharedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var groupName = $"shared_{Guid.NewGuid():N}";
            var sharedRadio = new RadioButton { Content = "全譜面で共有する", GroupName = groupName, IsChecked = row.Shared, Margin = new Thickness(0, 0, 12, 0) };
            var perTabRadio = new RadioButton { Content = "譜面毎に変更する", GroupName = groupName, IsChecked = !row.Shared };
            sharedRow.Children.Add(sharedRadio);
            sharedRow.Children.Add(perTabRadio);
            cardPanel.Children.Add(sharedRow);

            var sharedValuePanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0), Visibility = row.Shared ? Visibility.Visible : Visibility.Collapsed };
            sharedValuePanel.Children.Add(BuildGaugeValueFields(
                () => row.PerTabCsv.Count > 0 ? row.PerTabCsv[0] : "",
                csv =>
                {
                    while (row.PerTabCsv.Count < _project.Tabs.Count) row.PerTabCsv.Add("");
                    for (int i = 0; i < _project.Tabs.Count; i++) row.PerTabCsv[i] = csv;
                }));
            cardPanel.Children.Add(sharedValuePanel);

            sharedRadio.Checked += (_, _) =>
            {
                row.Shared = true;
                sharedValuePanel.Visibility = Visibility.Visible;
                // 共有へ切り替えた際、既存の値がバラバラなら先頭の非空値で揃える
                var first = row.PerTabCsv.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
                for (int i = 0; i < row.PerTabCsv.Count; i++) row.PerTabCsv[i] = first;
                RefreshAll();
            };
            perTabRadio.Checked += (_, _) =>
            {
                row.Shared = false;
                sharedValuePanel.Visibility = Visibility.Collapsed;
                RefreshAll();
            };

            card.Child = cardPanel;
            _gaugeDeclPanel.Children.Add(card);
        }

        if (_paramRows.Count == 0)
            _gaugeDeclPanel.Children.Add(new TextBlock { Text = "(まだ宣言されていません)", Foreground = Brushes.Gray });
    }

    /// <summary>ノルマ(またはx)/回復/ダメージ/初期ライフの4欄入力UI(2026-07-30新設、difData入力欄と
    /// gaugeXXX値入力欄の両方から共通で使う汎用ヘルパー)。</summary>
    private static FrameworkElement BuildGaugeValueFields(Func<string> getCsv, Action<string> setCsv)
    {
        var parts = (getCsv() ?? "").Split(',');
        string border = parts.Length > 0 ? parts[0].Trim() : "";
        bool borderIsX = border == "x";
        string borderVal = borderIsX || border.Length == 0 ? "70" : border;
        string recovery = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "6";
        string damage = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "40";
        string initLife = parts.Length > 3 ? parts[3] : "";

        var groupName = $"border_{Guid.NewGuid():N}";
        var borderXRadio = new RadioButton { Content = "x指定", GroupName = groupName, Margin = new Thickness(0, 0, 12, 0), IsChecked = borderIsX };
        var borderNumRadio = new RadioButton { Content = "数値", GroupName = groupName, IsChecked = !borderIsX };
        var borderValueBox = new TextBox { Width = 70, Text = borderVal, Margin = new Thickness(8, 0, 0, 0), IsEnabled = !borderIsX };
        var recoveryBox = new TextBox { Width = 70, Text = recovery };
        var damageBox = new TextBox { Width = 70, Text = damage };
        var initLifeBox = new TextBox { Width = 70, Text = initLife };

        void Commit()
        {
            string b = borderXRadio.IsChecked == true ? "x" : borderValueBox.Text;
            string csv = $"{b},{recoveryBox.Text},{damageBox.Text}";
            setCsv(string.IsNullOrWhiteSpace(initLifeBox.Text) ? csv : $"{csv},{initLifeBox.Text}");
        }

        borderXRadio.Checked += (_, _) => { borderValueBox.IsEnabled = false; Commit(); };
        borderNumRadio.Checked += (_, _) => { borderValueBox.IsEnabled = true; Commit(); };
        borderValueBox.TextChanged += (_, _) => Commit();
        recoveryBox.TextChanged += (_, _) => Commit();
        damageBox.TextChanged += (_, _) => Commit();
        initLifeBox.TextChanged += (_, _) => Commit();

        var panel = new StackPanel();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row1.Children.Add(new TextBlock { Text = "ノルマ(0-100):", Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row1.Children.Add(borderXRadio);
        row1.Children.Add(borderNumRadio);
        row1.Children.Add(borderValueBox);
        panel.Children.Add(row1);
        panel.Children.Add(LabeledFieldRow("回復量:", recoveryBox));
        panel.Children.Add(LabeledFieldRow("ダメージ:", damageBox));
        panel.Children.Add(LabeledFieldRow("初期ライフ(空欄可):", initLifeBox));
        return panel;
    }

    private static StackPanel LabeledFieldRow(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    // =====================================================================
    // 本体ゲージ(difData直接指定)
    // =====================================================================

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

        public string ToCsv()
        {
            if (!Enabled) return "";
            string border = BorderIsX ? "x" : BorderValue;
            string csv = $"{border},{Recovery},{Damage}";
            return string.IsNullOrWhiteSpace(InitLife) ? csv : $"{csv},{InitLife}";
        }
    }

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

        var groupName = $"border_{tabIndex}_{Guid.NewGuid():N}";
        var borderXRadio = new RadioButton { Content = "x指定", GroupName = groupName, Margin = new Thickness(0, 0, 12, 0), IsChecked = vm.BorderIsX };
        var borderNumRadio = new RadioButton { Content = "数値", GroupName = groupName, IsChecked = !vm.BorderIsX };
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

    // =====================================================================
    // ② 難易度タブ別「ゲージセット設定」(customGauge、2026-07-30再設計)
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
            int tabIndex = i;
            var tab = _project.Tabs[i];
            var vm = _tabVms[i];

            var panel = new StackPanel { Margin = new Thickness(8) };

            var difDataBorder = new Border { BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12) };
            var difDataPanel = new StackPanel();
            difDataPanel.Children.Add(SectionLabel("本体ゲージ(difDataへの直接指定)"));
            difDataPanel.Children.Add(BuildDifDataExtraFields(i));
            difDataBorder.Child = difDataPanel;
            panel.Children.Add(difDataBorder);

            var gaugeSetBorder = new Border { BorderBrush = Brushes.DarkOliveGreen, BorderThickness = new Thickness(1), Padding = new Thickness(8) };
            var gaugeSetPanel = new StackPanel();
            gaugeSetPanel.Children.Add(SectionLabel("ゲージセット設定(customGauge)"));
            gaugeSetPanel.Children.Add(new TextBlock
            {
                Text = "上の本体ゲージとは独立した機能で、同時に使えますの(本体ゲージ=既定表示、ゲージセット=" +
                       "プレイヤーが選べる代替候補)。",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var setGroupName = $"gaugeSetMode_{tabIndex}_{Guid.NewGuid():N}";
            var setModeNone = new RadioButton { Content = "設定しない(本体既定を使用)", GroupName = setGroupName, Margin = new Thickness(0, 0, 0, 4) };
            var setModeCustom = new RadioButton { Content = "カスタムゲージを使用", GroupName = setGroupName, Margin = new Thickness(0, 0, 0, 4) };
            var setModeBuiltin = new RadioButton { Content = "本体の既存セットを使用", GroupName = setGroupName, Margin = new Thickness(0, 0, 0, 4) };
            switch (vm.Mode)
            {
                case "list": setModeCustom.IsChecked = true; break;
                case "inherit": setModeBuiltin.IsChecked = true; break;
                default: setModeNone.IsChecked = true; break;
            }
            gaugeSetPanel.Children.Add(setModeNone);
            gaugeSetPanel.Children.Add(setModeCustom);
            gaugeSetPanel.Children.Add(setModeBuiltin);

            var builtinCombo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 8) };
            foreach (var (_, label) in BuiltinGaugeSets) builtinCombo.Items.Add(label);
            int builtinIdx = Array.IndexOf(InheritKeywords, vm.InheritKeyword);
            builtinCombo.SelectedIndex = builtinIdx >= 0 ? builtinIdx : 0;
            gaugeSetPanel.Children.Add(builtinCombo);

            var customPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            gaugeSetPanel.Children.Add(customPanel);

            void RefreshVisibility()
            {
                builtinCombo.Visibility = setModeBuiltin.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                customPanel.Visibility = setModeCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }

            void RefreshCustomPanel()
            {
                customPanel.Children.Clear();
                customPanel.Children.Add(new TextBlock { Text = "採用中のゲージ", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) });
                if (vm.Entries.Count == 0)
                    customPanel.Children.Add(new TextBlock { Text = "(まだありません)", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });

                for (int k = 0; k < vm.Entries.Count; k++)
                {
                    int idx = k;
                    var entry = vm.Entries[idx];
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

                    var upBtn = new Button { Content = "↑", Width = 24, IsEnabled = idx > 0 };
                    var downBtn = new Button { Content = "↓", Width = 24, IsEnabled = idx < vm.Entries.Count - 1, Margin = new Thickness(2, 0, 6, 0) };
                    upBtn.Click += (_, _) => { (vm.Entries[idx - 1], vm.Entries[idx]) = (vm.Entries[idx], vm.Entries[idx - 1]); RefreshCustomPanel(); };
                    downBtn.Click += (_, _) => { (vm.Entries[idx + 1], vm.Entries[idx]) = (vm.Entries[idx], vm.Entries[idx + 1]); RefreshCustomPanel(); };
                    row.Children.Add(upBtn);
                    row.Children.Add(downBtn);

                    row.Children.Add(new TextBlock { Text = entry.Name, Width = 110, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });

                    var varCheck = new CheckBox { Content = "V(可変)", IsChecked = entry.IsVariable, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    varCheck.Checked += (_, _) => entry.IsVariable = true;
                    varCheck.Unchecked += (_, _) => entry.IsVariable = false;
                    row.Children.Add(varCheck);

                    string declaredDefault = _paramRows.FirstOrDefault(r => r.GaugeName == entry.Name)?.DisplayName ?? "";
                    var dispCheck = new CheckBox { Content = "表示名を変える", IsChecked = !string.IsNullOrEmpty(entry.DisplayName), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                    var dispBox = new TextBox
                    {
                        Width = 120,
                        Text = entry.DisplayName,
                        IsEnabled = dispCheck.IsChecked == true,
                        ToolTip = string.IsNullOrEmpty(declaredDefault) ? "" : $"未指定時は①の既定表示名「{declaredDefault}」が使われます",
                    };
                    dispCheck.Checked += (_, _) => dispBox.IsEnabled = true;
                    dispCheck.Unchecked += (_, _) => { dispBox.IsEnabled = false; dispBox.Text = ""; entry.DisplayName = ""; };
                    dispBox.TextChanged += (_, _) => { if (dispCheck.IsChecked == true) entry.DisplayName = dispBox.Text; };
                    row.Children.Add(dispCheck);
                    row.Children.Add(dispBox);

                    var removeBtn = new Button { Content = "未採用に戻す", Width = 100, Margin = new Thickness(8, 0, 0, 0) };
                    removeBtn.Click += (_, _) => { vm.Entries.RemoveAt(idx); RefreshCustomPanel(); };
                    row.Children.Add(removeBtn);

                    customPanel.Children.Add(row);

                    var paramRow = _paramRows.FirstOrDefault(r => r.GaugeName == entry.Name);
                    if (paramRow is not null && !paramRow.Shared)
                    {
                        var valuePanel = BuildGaugeValueFields(
                            () => tabIndex < paramRow.PerTabCsv.Count ? paramRow.PerTabCsv[tabIndex] : "",
                            csv =>
                            {
                                while (paramRow.PerTabCsv.Count <= tabIndex) paramRow.PerTabCsv.Add("");
                                paramRow.PerTabCsv[tabIndex] = csv;
                            });
                        valuePanel.Margin = new Thickness(58, 2, 0, 8);
                        customPanel.Children.Add(valuePanel);
                    }
                }

                customPanel.Children.Add(new TextBlock { Text = "追加できるゲージ(①で宣言済み)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2) });
                var available = _paramRows.Where(r => !string.IsNullOrWhiteSpace(r.GaugeName) && !vm.Entries.Any(e => e.Name == r.GaugeName)).ToList();
                if (available.Count == 0)
                    customPanel.Children.Add(new TextBlock { Text = "(すべて採用済み、または①で未宣言)", Foreground = Brushes.Gray });
                foreach (var r in available)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                    row.Children.Add(new TextBlock { Text = r.GaugeName, Width = 140, VerticalAlignment = VerticalAlignment.Center });
                    var addBtn = new Button { Content = "→ 追加", Width = 80 };
                    string name = r.GaugeName;
                    addBtn.Click += (_, _) => { vm.Entries.Add(new EntryVm { Name = name }); RefreshCustomPanel(); };
                    row.Children.Add(addBtn);
                    customPanel.Children.Add(row);
                }
            }

            setModeNone.Checked += (_, _) => { vm.Mode = "none"; RefreshVisibility(); };
            setModeCustom.Checked += (_, _) => { vm.Mode = "list"; RefreshVisibility(); };
            setModeBuiltin.Checked += (_, _) => { vm.Mode = "inherit"; vm.InheritKeyword = InheritKeywords[builtinCombo.SelectedIndex]; RefreshVisibility(); };
            builtinCombo.SelectionChanged += (_, _) => { if (setModeBuiltin.IsChecked == true) vm.InheritKeyword = InheritKeywords[builtinCombo.SelectedIndex]; };

            RefreshVisibility();
            RefreshCustomPanel();

            gaugeSetBorder.Child = gaugeSetPanel;
            panel.Children.Add(gaugeSetBorder);

            _tabGaugeTabs.Items.Add(new TabItem { Header = tab.DisplayLabel, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }
    }

    // =====================================================================
    // ④ 上級者向け: 直接入力モード(2026-07-30再設計)
    // =====================================================================

    private FrameworkElement BuildAdvancedSection()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 8, 8, 0) };

        bool hasRawText = !string.IsNullOrWhiteSpace(_project.GaugeRawOverrideText);
        _rawModeImport.IsChecked = !hasRawText;
        _rawModeDirect.IsChecked = hasRawText;
        if (hasRawText) _rawOverrideBox.Text = _project.GaugeRawOverrideText ?? "";

        panel.Children.Add(_rawModeImport);
        panel.Children.Add(_rawModeDirect);
        panel.Children.Add(_rawOverrideBox);

        _importRawButton.Click += ImportRawOverride_Click;
        panel.Children.Add(_importRawButton);

        void RefreshRawModeVisibility()
        {
            _importRawButton.Visibility = _rawModeImport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateEnabledState();
        }
        _rawModeImport.Checked += (_, _) => RefreshRawModeVisibility();
        _rawModeDirect.Checked += (_, _) => RefreshRawModeVisibility();
        _rawOverrideBox.TextChanged += (_, _) => UpdateEnabledState();
        RefreshRawModeVisibility();

        return panel;
    }

    /// <summary>「インポート」ボタン(2026-07-30再設計、旧LoadFromRawOverride_Clickの後継)。
    /// _rawOverrideBoxに貼り付けられたcustomGaugeN/gaugeXXXのヘッダー行(|key=value|形式)を解析し、
    /// ①②の構造化UIへ一括反映する。成功時はテキスト欄をクリアし(以後は構造化UIがexportに使われる)、
    /// 失敗時は内容を保持したままエラーを表示する。</summary>
    private void ImportRawOverride_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";
        string text = _rawOverrideBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _error.Text = "テキスト欄が空のため読み取れませんの。";
            return;
        }

        int tabCount = _project.Tabs.Count;
        var parsedTabVms = Enumerable.Range(0, tabCount).Select(_ => new TabGaugeVm()).ToList();
        var parsedRows = new List<ParamRowVm>();
        bool anyMatched = false;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[0] != '|' || line[^1] != '|') continue;
            var body = line[1..^1];
            int eq = body.IndexOf('=');
            if (eq < 0) continue;
            var key = body[..eq];
            var value = body[(eq + 1)..];

            if (key.StartsWith("customGauge", StringComparison.Ordinal))
            {
                var suffix = key["customGauge".Length..];
                int tabIndex;
                if (suffix.Length == 0) tabIndex = 0;
                else if (int.TryParse(suffix, out var n)) tabIndex = n - 1;
                else continue;
                if (tabIndex < 0 || tabIndex >= tabCount) continue;

                anyMatched = true;
                if (InheritKeywords.Contains(value))
                {
                    parsedTabVms[tabIndex] = new TabGaugeVm { Mode = "inherit", InheritKeyword = value };
                }
                else
                {
                    var entries = value.Split(',').Where(s => s.Length > 0).Select(s =>
                    {
                        var parts = s.Split("::");
                        return new EntryVm
                        {
                            Name = parts.Length > 0 ? parts[0] : "",
                            IsVariable = parts.Length > 1 && parts[1] == "V",
                            DisplayName = parts.Length > 2 ? parts[2] : "",
                        };
                    }).ToList();
                    parsedTabVms[tabIndex] = new TabGaugeVm { Mode = "list", Entries = entries };
                }
            }
            else if (key.StartsWith("gauge", StringComparison.Ordinal) && key.Length > "gauge".Length)
            {
                var name = key["gauge".Length..];
                anyMatched = true;
                var perTab = value.Split('$').ToList();
                while (perTab.Count < tabCount) perTab.Add("");
                if (perTab.Count > tabCount) perTab = perTab.Take(tabCount).ToList();
                var row = new ParamRowVm { GaugeName = name, PerTabCsv = perTab };
                row.Shared = InferShared(row, tabCount);
                parsedRows.Add(row);
            }
        }

        if (!anyMatched)
        {
            _error.Text = "customGauge/gaugeXXXの行が見つかりませんでしたの(|key=value|形式の行のみ読み取れます)。";
            return;
        }

        for (int i = 0; i < tabCount; i++) _tabVms[i] = parsedTabVms[i];
        _paramRows.Clear();
        _paramRows.AddRange(parsedRows);

        _rawOverrideBox.Text = "";
        RefreshAll();
        _error.Text = "";
        _error.Foreground = Brushes.LightGreen;
        _error.Text = "①②へ反映しましたの。";
    }

    // =====================================================================
    // 保存
    // =====================================================================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _error.Text = "";
        _error.Foreground = Brushes.Red;

        string? rawText = _rawModeDirect.IsChecked == true && !string.IsNullOrWhiteSpace(_rawOverrideBox.Text)
            ? _rawOverrideBox.Text
            : null;

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
                _error.Text = "①のゲージ名が重複していますの。";
                return;
            }
        }

        for (int i = 0; i < _project.Tabs.Count; i++)
            _project.Tabs[i].Gauge = rawText is null ? gaugeConfigs[i] : null;

        List<ParamRowVm> validRows = rawText is not null
            ? []
            : _paramRows.Where(r => !string.IsNullOrWhiteSpace(r.GaugeName)).ToList();

        _project.GaugeNames = validRows
            .Select(r => new GaugeNameDef(r.GaugeName.Trim(), string.IsNullOrWhiteSpace(r.DisplayName) ? null : r.DisplayName.Trim()))
            .ToList();
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

        for (int i = 0; i < _project.Tabs.Count; i++)
        {
            var csv = _difDataVms[i].ToCsv();
            _project.Tabs[i].DifDataExtra = string.IsNullOrEmpty(csv) ? null : csv;
        }

        _project.GaugeManualEditAfterExport = _manualEditAfterExport.IsChecked == true;

        Saved = true;
        // 2026-08-02: モードレス化(MainWindow.OpenGaugeEditor_Click参照)に伴い、DialogResult経由の
        // 自動クローズはShowDialog()前提のためInvalidOperationExceptionになる。Close()で明示的に閉じ、
        // 呼び出し元はClosedイベント側でSavedを見てNotifyChangedするよう変更した。
        Close();
    }
}
