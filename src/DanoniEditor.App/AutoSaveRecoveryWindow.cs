using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Persistence;

namespace DanoniEditor.App;

/// <summary>
/// 「ファイル>自動保存データの復旧」用のスロット選択ダイアログ(2026-10-04要望対応)。
/// 従来の起動時クラッシュ検知(App.OnStartup→MainWindow.OfferCrashRecovery)は、異常終了が
/// 疑われた場合に自動で1件ずつ確認ダイアログを出す一方通行の導線のみだった。
/// 「いいえ」を選んだ・判定をすり抜けた等の理由で後から見返せなくなるケースに備え、
/// manifest.json上の自動保存スロットを一覧から選んで開ける手動導線をここに追加する。
///
/// GridMismatchPolicyDialogと同じ方針(XAML無しの最小モーダルダイアログ)。一覧表示は
/// ListView+GridViewの素朴な実装で、列はプロジェクト名・元のファイル・自動保存日時・状態。
/// 「状態」列は、そのスロットを書き込んだプロセス(ウィンドウ)が今も生きているかを示す
/// (生存中の別ウィンドウのスロットを選んでも開けるが、紛らわしいため注記のみ行い、
/// 削除や選択自体は禁止しない=最終手段の手動導線として柔軟性を優先する)。
/// </summary>
internal static class AutoSaveRecoveryWindow
{
    private sealed class Row(AutoSaveSlotInfo slot, bool alive)
    {
        public AutoSaveSlotInfo Slot { get; } = slot;
        public string ProjectName => Slot.ProjectName;
        public string LastKnownPath => Slot.LastKnownPath ?? "(未保存の新規プロジェクト)";
        public string SavedAt => Slot.SavedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        public string Status => alive ? "生存中の別ウィンドウ(開いても元のウィンドウには影響しません)" : "復旧可能";
    }

    /// <summary>選択されたスロット情報。キャンセル/未選択時はnull。</summary>
    public static AutoSaveSlotInfo? Ask(Window owner, IReadOnlyList<AutoSaveSlotInfo> slots, ISet<string> aliveInstanceIds)
    {
        var win = new Window
        {
            Title = "自動保存データの復旧",
            Owner = owner,
            Width = 720,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var info = new TextBlock
        {
            Text = "復旧したい自動保存データを選んで「開く」を押してください。開いた内容は未保存の新規タブとして" +
                   "追加されます(元のファイルへは上書きされません)。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(info, 0);
        root.Children.Add(info);

        var rows = slots
            .OrderByDescending(s => s.SavedAtUtc)
            .Select(s => new Row(s, aliveInstanceIds.Contains(s.InstanceId)))
            .ToList();

        var list = new ListView { ItemsSource = rows, SelectionMode = SelectionMode.Single };
        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn { Header = "プロジェクト名", Width = 160, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.ProjectName)) });
        gridView.Columns.Add(new GridViewColumn { Header = "元のファイル", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.LastKnownPath)) });
        gridView.Columns.Add(new GridViewColumn { Header = "自動保存日時", Width = 130, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.SavedAt)) });
        gridView.Columns.Add(new GridViewColumn { Header = "状態", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Status)) });
        list.View = gridView;
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var open = new Button { Content = "開く", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        win.Content = root;

        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is not null;
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Row) win.DialogResult = true;
        };

        bool openClicked = false;
        open.Click += (_, _) => { openClicked = true; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        var result = win.ShowDialog();
        if (result != true) return null;
        if (!openClicked && list.SelectedItem is null) return null; // ダブルクリックでも選択が必須
        return (list.SelectedItem as Row)?.Slot;
    }
}
