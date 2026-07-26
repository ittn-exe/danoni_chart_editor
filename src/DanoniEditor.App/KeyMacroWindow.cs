using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// キーマクロ設定ウィンドウ(2026-07-26要望対応)。Ctrl+Shift+1〜9の各スロットへ、
/// 「複数の機能を順番に実行する」手順(KeyMacroStep)を登録する。既存の「レーン入替マクロ」
/// (仕様書11章)とは別機能。設定は即時に渡されたAppSettingsへ反映・保存する(他の小ダイアログと
/// 異なり作業コピー/コミット方式は取らない、変更のたびに即保存でシンプルに保つ設計判断)。
/// </summary>
internal sealed class KeyMacroWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private readonly ComboBox _slotCombo;
    private readonly ListBox _stepsList;
    private readonly ComboBox _kindCombo;
    private readonly TextBox _valueBox;

    private static readonly (KeyMacroStepKind Kind, string Label, bool NeedsValue)[] KindItems =
    [
        (KeyMacroStepKind.SetPlaybackSpeed, "再生速度を設定(倍率)", true),
        (KeyMacroStepKind.SetPlaybackStartSeconds, "再生開始位置を設定(秒)", true),
        (KeyMacroStepKind.StartVisualTest, "目視テストを開始", false),
        (KeyMacroStepKind.StartPlaytest, "プレイテストを開始", false),
    ];

    public KeyMacroWindow(Window owner, AppSettings settings, Action onSaved)
    {
        _settings = settings;
        _onSaved = onSaved;

        Title = "キーマクロ設定";
        Owner = owner;
        Width = 420;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new StackPanel { Margin = new Thickness(12) };

        root.Children.Add(new TextBlock
        {
            Text = "Ctrl+Shift+数字キーへ割り当てる、複数の機能を順番に実行するマクロですわ。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock { Text = "スロット", Margin = new Thickness(0, 0, 0, 4) });
        _slotCombo = new ComboBox
        {
            ItemsSource = Enumerable.Range(1, 9).Select(i => $"Ctrl+Shift+{i}").ToList(),
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _slotCombo.SelectionChanged += (_, _) => RefreshStepsList();
        root.Children.Add(_slotCombo);

        _stepsList = new ListBox { Height = 150, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_stepsList);

        var reorderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var up = new Button { Content = "↑", Width = 32, Margin = new Thickness(0, 0, 4, 0) };
        var down = new Button { Content = "↓", Width = 32, Margin = new Thickness(0, 0, 4, 0) };
        var remove = new Button { Content = "削除", Width = 50 };
        up.Click += (_, _) => MoveSelectedStep(-1);
        down.Click += (_, _) => MoveSelectedStep(1);
        remove.Click += (_, _) => RemoveSelectedStep();
        reorderRow.Children.Add(up);
        reorderRow.Children.Add(down);
        reorderRow.Children.Add(remove);
        root.Children.Add(reorderRow);

        root.Children.Add(new TextBlock { Text = "手順を追加", Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.Bold });
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _kindCombo = new ComboBox
        {
            ItemsSource = KindItems.Select(k => k.Label).ToList(),
            SelectedIndex = 0,
            Width = 220,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _valueBox = new TextBox { Width = 70, Text = "1.0", Margin = new Thickness(0, 0, 4, 0) };
        _kindCombo.SelectionChanged += (_, _) => _valueBox.IsEnabled = KindItems[_kindCombo.SelectedIndex].NeedsValue;
        var add = new Button { Content = "追加", Width = 50 };
        add.Click += (_, _) => AddStep();
        addRow.Children.Add(_kindCombo);
        addRow.Children.Add(_valueBox);
        addRow.Children.Add(add);
        root.Children.Add(addRow);

        var close = new Button { Content = "閉じる", Width = 70, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(close);

        Content = root;
        RefreshStepsList();
    }

    private int CurrentSlot => _slotCombo.SelectedIndex + 1;

    /// <summary>参照のみ(存在しなければnull)。一覧表示・削除・並び替えで使う。</summary>
    private KeyMacroDefinition? FindSlotDefinition() => _settings.KeyMacros.FirstOrDefault(m => m.Slot == CurrentSlot);

    /// <summary>手順追加時のみ、無ければ新規作成して返す(単に画面を開いただけで空定義が
    /// 溜まらないよう、閲覧系のメソッドからは呼ばない)。</summary>
    private KeyMacroDefinition GetOrCreateSlotDefinition()
    {
        var def = FindSlotDefinition();
        if (def is null)
        {
            def = new KeyMacroDefinition { Slot = CurrentSlot };
            _settings.KeyMacros.Add(def);
        }
        return def;
    }

    private static string DescribeStep(KeyMacroStep step) => step.Kind switch
    {
        KeyMacroStepKind.SetPlaybackSpeed => $"再生速度を x{step.Value.ToString("0.00", CultureInfo.InvariantCulture)} に設定",
        KeyMacroStepKind.SetPlaybackStartSeconds => $"再生開始位置を {step.Value.ToString("0.00", CultureInfo.InvariantCulture)}秒 に設定",
        KeyMacroStepKind.StartVisualTest => "目視テストを開始",
        KeyMacroStepKind.StartPlaytest => "プレイテストを開始",
        _ => step.Kind.ToString(),
    };

    private void RefreshStepsList()
    {
        _stepsList.ItemsSource = FindSlotDefinition()?.Steps.Select(DescribeStep).ToList() ?? [];
    }

    private void AddStep()
    {
        var (kind, _, needsValue) = KindItems[_kindCombo.SelectedIndex];
        double value = 0;
        if (needsValue && (!double.TryParse(_valueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0))
        {
            MessageBox.Show(this, "値は0以上の数値で入力してくださいませ。", "キーマクロ設定", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        GetOrCreateSlotDefinition().Steps.Add(new KeyMacroStep { Kind = kind, Value = value });
        Save();
    }

    private void RemoveSelectedStep()
    {
        var def = FindSlotDefinition();
        int idx = _stepsList.SelectedIndex;
        if (def is null || idx < 0 || idx >= def.Steps.Count) return;
        def.Steps.RemoveAt(idx);
        Save();
    }

    private void MoveSelectedStep(int delta)
    {
        var def = FindSlotDefinition();
        int idx = _stepsList.SelectedIndex;
        int newIdx = idx + delta;
        if (def is null || idx < 0 || newIdx < 0 || newIdx >= def.Steps.Count) return;
        (def.Steps[idx], def.Steps[newIdx]) = (def.Steps[newIdx], def.Steps[idx]);
        Save();
        _stepsList.SelectedIndex = newIdx;
    }

    private void Save()
    {
        // 空になったスロット定義はリストへ残さない(次回起動時のごみ防止)
        _settings.KeyMacros.RemoveAll(m => m.Steps.Count == 0);
        RefreshStepsList();
        _onSaved();
    }
}
