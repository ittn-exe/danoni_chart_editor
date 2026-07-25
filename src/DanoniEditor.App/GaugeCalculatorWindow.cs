using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DanoniEditor.Core.Analysis;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// ゲージ計算機(2026-08-01、ユーザー要望)。GaugeEditorWindowの②ゲージ別パラメータ表から
/// モードレスで開く。①のタブで選択中の難易度タブを「カレントタブ」として常に参照し、
/// タブが切り替われば警告無しに新しいタブの値で再計算する(ユーザー確定仕様)。
/// 計算式はdanoni_main.js(getAccuracy/calcLifeVal/gaugeFormat)をナレッジの本体ソースで検証したもの
/// (GaugeCalculator.cs参照)。ユーザー提供の参考ツール(calc.html)にあったFixモードの「×100」誤りは
/// 含めていない。
/// </summary>
internal sealed class GaugeCalculatorWindow : Window
{
    private readonly GaugeEditorWindow _owner;
    private bool _suppressEvents;

    // --- 対象タブ表示 ---
    private readonly TextBlock _tabLabel = new() { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };

    // --- Base Settings ---
    private readonly TextBox _maxLifeValBox = new() { Width = 90 };
    private readonly TextBox _borderBox = new() { Width = 90 };
    private readonly TextBox _normalArrowBox = new() { Width = 90 };
    private readonly TextBox _freezeArrowBox = new() { Width = 90 };
    private readonly TextBox _initLifeBox = new() { Width = 90 };
    private readonly CheckBox _frzStartJdgCheck = new() { Content = "フリーズ始点判定を含める" };

    // --- 対象ゲージ名 / モード ---
    private readonly ComboBox _gaugeNameCombo = new() { Width = 200 };
    private readonly RadioButton _modeFixRadio = new() { Content = "Fix(可変フラグ=F、生の値をそのまま使用)", GroupName = "calcMode" };
    private readonly RadioButton _modeVaryRadio = new() { Content = "Vary(可変フラグ=V、ノーツ数で按分)", GroupName = "calcMode", IsChecked = true };
    private readonly TextBlock _rcvLabel = new();
    private readonly TextBox _rcvBox = new() { Width = 90 };
    private readonly TextBlock _dmgLabel = new();
    private readonly TextBox _dmgBox = new() { Width = 90 };

    // --- シミュレーション結果 ---
    private readonly TextBlock _resRealRcv = new() { FontWeight = FontWeights.Bold };
    private readonly TextBlock _resRealDmg = new() { FontWeight = FontWeights.Bold };
    private readonly TextBlock _resRate = new() { FontWeight = FontWeights.Bold };
    private readonly TextBlock _resMiss = new() { FontWeight = FontWeights.Bold };

    // --- Design Mode ---
    private readonly RadioButton _targetRateRadio = new() { Content = "達成率(%)", GroupName = "designTarget", IsChecked = true };
    private readonly RadioButton _targetMissRadio = new() { Content = "ミス数", GroupName = "designTarget" };
    private readonly TextBox _targetValueBox = new() { Width = 90, Text = "95" };
    private readonly RadioButton _fixDmgRadio = new() { Content = "ダメージを固定 → 回復を逆算", GroupName = "fixParam", IsChecked = true };
    private readonly RadioButton _fixRcvRadio = new() { Content = "回復を固定 → ダメージを逆算", GroupName = "fixParam" };
    private readonly TextBlock _designResult = new() { FontWeight = FontWeights.Bold, Foreground = Brushes.LightGreen };

    private readonly TextBlock _error = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _applyButton = new() { Content = "選択中のゲージ名へ適用", Width = 160 };

    public GaugeCalculatorWindow(GaugeEditorWindow owner)
    {
        _owner = owner;

        Title = "ゲージ計算機";
        Width = 480;
        Height = 720;
        MinWidth = 420;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var outer = new StackPanel { Margin = new Thickness(12) };

        outer.Children.Add(_tabLabel);

        outer.Children.Add(SectionLabel("Base Settings"));
        outer.Children.Add(LabeledRow("MAX Life(maxLifeVal)", _maxLifeValBox));
        outer.Children.Add(LabeledRow("Border(ノルマ%)", _borderBox));
        outer.Children.Add(LabeledRow("通常ノート数", _normalArrowBox));
        outer.Children.Add(LabeledRow("フリーズ数", _freezeArrowBox));
        outer.Children.Add(LabeledRow("初期ライフ(InitLife%)", _initLifeBox));
        outer.Children.Add(_frzStartJdgCheck);

        outer.Children.Add(SectionLabel("対象ゲージ名 / モード"));
        outer.Children.Add(LabeledRow("対象ゲージ名(②の表から選択)", _gaugeNameCombo));
        outer.Children.Add(_modeFixRadio);
        outer.Children.Add(_modeVaryRadio);
        outer.Children.Add(LabeledRow("", _rcvLabel));
        outer.Children.Add(_rcvBox);
        outer.Children.Add(LabeledRow("", _dmgLabel));
        outer.Children.Add(_dmgBox);

        var resultBox = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        var resultPanel = new StackPanel();
        resultPanel.Children.Add(ResultRow("realRcv(1ノーツあたりの回復量):", _resRealRcv));
        resultPanel.Children.Add(ResultRow("realDmg(1ノーツあたりの減少量):", _resRealDmg));
        resultPanel.Children.Add(ResultRow("必要達成率:", _resRate));
        resultPanel.Children.Add(ResultRow("許容ミス数:", _resMiss));
        resultBox.Child = resultPanel;
        outer.Children.Add(resultBox);

        outer.Children.Add(SectionLabel("Design Mode(目標値からの逆算)"));
        var targetModeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        targetModeRow.Children.Add(_targetRateRadio);
        targetModeRow.Children.Add(_targetMissRadio);
        outer.Children.Add(targetModeRow);
        outer.Children.Add(LabeledRow("目標値", _targetValueBox));
        outer.Children.Add(_fixDmgRadio);
        outer.Children.Add(_fixRcvRadio);
        var designResultBox = new Border { BorderBrush = Brushes.DarkGreen, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        designResultBox.Child = _designResult;
        outer.Children.Add(designResultBox);

        outer.Children.Add(_applyButton);
        outer.Children.Add(_error);

        root.Content = outer;
        Content = root;

        // --- イベント配線 ---
        foreach (var box in new[] { _maxLifeValBox, _borderBox, _normalArrowBox, _freezeArrowBox, _initLifeBox, _rcvBox, _dmgBox, _targetValueBox })
            box.TextChanged += (_, _) => Recalculate();
        _frzStartJdgCheck.Checked += (_, _) => Recalculate();
        _frzStartJdgCheck.Unchecked += (_, _) => Recalculate();
        _modeFixRadio.Checked += (_, _) => { UpdateModeLabels(); Recalculate(); };
        _modeVaryRadio.Checked += (_, _) => { UpdateModeLabels(); Recalculate(); };
        _targetRateRadio.Checked += (_, _) => Recalculate();
        _targetMissRadio.Checked += (_, _) => Recalculate();
        _fixDmgRadio.Checked += (_, _) => Recalculate();
        _fixRcvRadio.Checked += (_, _) => Recalculate();
        _gaugeNameCombo.SelectionChanged += (_, _) => LoadSelectedGaugeName();
        _applyButton.Click += ApplyButton_Click;

        _owner.TabGaugeTabsControl.SelectionChanged += TabGaugeTabsControl_SelectionChanged;
        Closed += (_, _) => _owner.TabGaugeTabsControl.SelectionChanged -= TabGaugeTabsControl_SelectionChanged;

        LoadFromCurrentTab(resetGaugeSelection: true);
    }

    private static TextBlock SectionLabel(string text) => new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6) };

    private static StackPanel LabeledRow(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        if (!string.IsNullOrEmpty(label))
            row.Children.Add(new TextBlock { Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private static StackPanel ResultRow(string label, TextBlock valueBlock)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 220 });
        row.Children.Add(valueBlock);
        return row;
    }

    private void TabGaugeTabsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 2026-08-01ユーザー確定仕様: タブ切替時は警告無しに新タブの値で再計算する
        LoadFromCurrentTab(resetGaugeSelection: false);
    }

    private int CurrentTabIndex => Math.Max(0, _owner.TabGaugeTabsControl.SelectedIndex);

    /// <summary>タブ切替・初回表示時に、Base Settingsを現在のタブの実データ/ExtraHeadersから再取得する。</summary>
    private void LoadFromCurrentTab(bool resetGaugeSelection)
    {
        _suppressEvents = true;
        var project = _owner.ProjectRef;
        int tabIndex = CurrentTabIndex;
        var tab = project.Tabs[tabIndex];

        _tabLabel.Text = $"対象タブ: {tab.DisplayLabel}(①のタブ切替に自動追随します)";

        double maxLifeVal = project.ExtraHeaders.TryGetValue("maxLifeVal", out var mlv) && double.TryParse(mlv, NumberStyles.Float, CultureInfo.InvariantCulture, out var mlvVal)
            ? mlvVal : 1000; // 本体既定値(C_VAL_MAXLIFE=1000)
        _maxLifeValBox.Text = maxLifeVal.ToString(CultureInfo.InvariantCulture);

        int normalCount = tab.Lanes.Sum(l => l.Notes.Count);
        int freezeCount = tab.Lanes.Sum(l => l.Freezes.Count);
        _normalArrowBox.Text = normalCount.ToString(CultureInfo.InvariantCulture);
        _freezeArrowBox.Text = freezeCount.ToString(CultureInfo.InvariantCulture);

        _frzStartJdgCheck.IsChecked = project.ExtraHeaders.TryGetValue("frzStartjdgUse", out var fsj) && fsj == "true";

        if (resetGaugeSelection)
        {
            RefreshGaugeNameCombo();
        }

        _suppressEvents = false;
        LoadSelectedGaugeName();
    }

    private void RefreshGaugeNameCombo()
    {
        var selected = _gaugeNameCombo.SelectedItem as string;
        _gaugeNameCombo.Items.Clear();
        _gaugeNameCombo.Items.Add("(手動入力)");
        foreach (var row in _owner.ParamRowsRef.Where(r => !string.IsNullOrWhiteSpace(r.GaugeName)))
            _gaugeNameCombo.Items.Add(row.GaugeName);
        _gaugeNameCombo.SelectedItem = selected is not null && _gaugeNameCombo.Items.Contains(selected) ? selected : "(手動入力)";
    }

    /// <summary>対象ゲージ名を選択した際、②の表の現在タブ列の値(border,rcv,dmg,init)と、
    /// ①のいずれかのタブのEntriesから見つかる可変フラグ(F/V)を読み込んでフォームへ反映する。</summary>
    private void LoadSelectedGaugeName()
    {
        if (_suppressEvents) return;
        if (_gaugeNameCombo.SelectedItem is not string name || name == "(手動入力)")
        {
            Recalculate();
            return;
        }

        _suppressEvents = true;
        int tabIndex = CurrentTabIndex;
        var row = _owner.ParamRowsRef.FirstOrDefault(r => r.GaugeName == name);
        if (row is not null && tabIndex < row.PerTabCsv.Count && !string.IsNullOrWhiteSpace(row.PerTabCsv[tabIndex]))
        {
            var parts = row.PerTabCsv[tabIndex].Split(',');
            if (parts.Length >= 4)
            {
                if (parts[0] != "x") _borderBox.Text = parts[0];
                _rcvBox.Text = parts[1];
                _dmgBox.Text = parts[2];
                _initLifeBox.Text = parts[3];
            }
        }

        // 全タブのEntriesからこのゲージ名の可変フラグ(F/V)を探す(最初に見つかったものを採用)
        bool? isVariable = _owner.TabVmsRef
            .SelectMany(t => t.Entries)
            .Where(en => en.Name == name)
            .Select(en => (bool?)en.IsVariable)
            .FirstOrDefault();
        if (isVariable is true) _modeVaryRadio.IsChecked = true;
        else if (isVariable is false) _modeFixRadio.IsChecked = true;

        _suppressEvents = false;
        UpdateModeLabels();
        Recalculate();
    }

    private void UpdateModeLabels()
    {
        bool vary = _modeVaryRadio.IsChecked == true;
        _rcvLabel.Text = vary ? "Rcv(回復倍率、ノーツ数按分で自動換算)" : "Rcv(回復値、生の値をそのまま使用)";
        _dmgLabel.Text = vary ? "Dmg(減少倍率、ノーツ数按分で自動換算)" : "Dmg(減少値、生の値をそのまま使用)";
    }

    private void Recalculate()
    {
        if (_suppressEvents) return;
        _error.Text = "";
        _error.Foreground = Brushes.Red;

        if (!TryReadInputs(out double maxLifeVal, out double borderPercent, out int normalCount, out int freezeCount,
                out double initPercent, out double rcvRaw, out double dmgRaw))
        {
            _resRealRcv.Text = _resRealDmg.Text = _resRate.Text = _resMiss.Text = _designResult.Text = "----";
            return;
        }

        var mode = _modeVaryRadio.IsChecked == true ? GaugeCalculator.CalcMode.Vary : GaugeCalculator.CalcMode.Fix;
        int allCnt = GaugeCalculator.GetAllCount(normalCount, freezeCount, _frzStartJdgCheck.IsChecked == true);
        double realBorder = GaugeCalculator.PercentToReal(borderPercent, maxLifeVal);
        double realInit = GaugeCalculator.PercentToReal(initPercent, maxLifeVal);
        double realRcv = GaugeCalculator.ToRealValue(rcvRaw, mode, maxLifeVal, allCnt);
        double realDmg = GaugeCalculator.ToRealValue(dmgRaw, mode, maxLifeVal, allCnt);
        if (mode == GaugeCalculator.CalcMode.Vary)
        {
            // 2026-08-01: 本体のgaugeFormatはVary時の表示値をmaxLifeValで頭打ちにする(Math.min)。
            realRcv = Math.Min(realRcv, maxLifeVal);
            realDmg = Math.Min(realDmg, maxLifeVal);
        }

        _resRealRcv.Text = realRcv.ToString("F2", CultureInfo.InvariantCulture);
        _resRealDmg.Text = realDmg.ToString("F2", CultureInfo.InvariantCulture);

        var acc = GaugeCalculator.GetAccuracy(realBorder, realRcv, realDmg, realInit, allCnt);
        if (!acc.IsValid)
        {
            _resRate.Text = "----";
            _resMiss.Text = "----";
        }
        else
        {
            _resRate.Text = acc.RatePercent <= 100 ? $"{acc.RatePercent:F2}%" : $"{acc.RatePercent:F2}%(クリア不可能)";
            _resMiss.Text = acc.AllowableMiss >= 0 ? $"{acc.AllowableMiss}miss↓" : $"({acc.AllowableMiss}miss、理論上クリア不可)";
        }

        // --- Design Mode ---
        if (!double.TryParse(_targetValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double targetValue))
        {
            _designResult.Text = "----";
            return;
        }
        var target = _targetRateRadio.IsChecked == true ? GaugeCalculator.DesignTarget.Rate : GaugeCalculator.DesignTarget.MissCount;
        var fixParam = _fixDmgRadio.IsChecked == true ? GaugeCalculator.FixParam.Damage : GaugeCalculator.FixParam.Recovery;
        double fixedRaw = fixParam == GaugeCalculator.FixParam.Damage ? dmgRaw : rcvRaw;

        var design = GaugeCalculator.SolveForTarget(target, targetValue, fixParam, fixedRaw, mode, maxLifeVal, realBorder, realInit, allCnt);
        if (!design.IsSolvable)
        {
            _designResult.Text = "計算不可(成功数が0以下になる目標値です)";
        }
        else if (design.IsInfinite)
        {
            _designResult.Text = "∞(ミス0回前提のためダメージ側は無限大になります)";
        }
        else
        {
            string paramName = fixParam == GaugeCalculator.FixParam.Damage ? "推奨Rcv" : "推奨Dmg";
            _designResult.Text = $"{paramName}: {design.RawValue:F4}";
        }
    }

    private bool TryReadInputs(out double maxLifeVal, out double borderPercent, out int normalCount, out int freezeCount,
        out double initPercent, out double rcvRaw, out double dmgRaw)
    {
        maxLifeVal = borderPercent = initPercent = rcvRaw = dmgRaw = 0;
        normalCount = freezeCount = 0;

        bool ok = double.TryParse(_maxLifeValBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxLifeVal)
            && double.TryParse(_borderBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out borderPercent)
            && int.TryParse(_normalArrowBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out normalCount)
            && int.TryParse(_freezeArrowBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out freezeCount)
            && double.TryParse(_initLifeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out initPercent)
            && double.TryParse(_rcvBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out rcvRaw)
            && double.TryParse(_dmgBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out dmgRaw);
        return ok;
    }

    /// <summary>「選択中のゲージ名へ適用」: Design Modeの推奨値を該当パラメータへ反映したうえで、
    /// border,rcv,dmg,initのCSVを②の表の現在タブ列へ書き戻す。</summary>
    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gaugeNameCombo.SelectedItem is not string name || name == "(手動入力)")
        {
            _error.Text = "適用先のゲージ名を選択してくださいまし(②の表に無いゲージ名は先に②で追加が必要です)。";
            return;
        }
        if (!TryReadInputs(out _, out double borderPercent, out _, out _, out double initPercent, out double rcvRaw, out double dmgRaw))
        {
            _error.Text = "入力値が不正ですの。";
            return;
        }

        // Design Modeの推奨値がある場合はそちらを優先して固定していない側へ反映する
        var fixParam = _fixDmgRadio.IsChecked == true ? GaugeCalculator.FixParam.Damage : GaugeCalculator.FixParam.Recovery;
        if (_designResult.Text.StartsWith("推奨", StringComparison.Ordinal))
        {
            var numPart = _designResult.Text.Split(':')[1].Trim();
            if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double recommended))
            {
                if (fixParam == GaugeCalculator.FixParam.Damage) rcvRaw = recommended;
                else dmgRaw = recommended;
            }
        }

        var row = _owner.ParamRowsRef.FirstOrDefault(r => r.GaugeName == name);
        if (row is null)
        {
            _error.Text = $"ゲージ名'{name}'が②の表に見つかりませんでした。";
            return;
        }

        int tabIndex = CurrentTabIndex;
        while (row.PerTabCsv.Count <= tabIndex) row.PerTabCsv.Add("");
        string borderField = borderPercent == 0 && _borderBox.Text.Trim() == "x" ? "x" : borderPercent.ToString(CultureInfo.InvariantCulture);
        row.PerTabCsv[tabIndex] = $"{borderField},{rcvRaw.ToString(CultureInfo.InvariantCulture)},{dmgRaw.ToString(CultureInfo.InvariantCulture)},{initPercent.ToString(CultureInfo.InvariantCulture)}";

        _rcvBox.Text = rcvRaw.ToString(CultureInfo.InvariantCulture);
        _dmgBox.Text = dmgRaw.ToString(CultureInfo.InvariantCulture);

        _owner.RefreshParamTableExternal();
        _error.Text = $"'{name}'(タブ列{tabIndex + 1})へ適用しましたの。";
        _error.Foreground = Brushes.LightGreen;
    }
}
