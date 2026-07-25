using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// 歌詞レーン(WordLanes、仕様dos-e0003-wordData、2026-07-23、TBD 4)の管理ウィンドウ。
/// カレント難易度タブに対して、ユーザーが任意に歌詞レーンを追加/名前変更/Reverse切替/削除できる。
/// 他のウィンドウ(テンプレ編集・マクロ編集等)と異なり「保存確定」方式ではなく、各操作を
/// 即座にDifficultyTab.WordLanesへ反映する。2026-07-26: 追加/改名/Reverse切替/削除いずれも
/// EditorDocument.Execute経由でUndoStackに積む(=通常の編集操作と同様にCtrl+Zで取り消せる)。
/// レーン削除のUndoは、削除時点のレーン内容(歌詞エントリを含む)を丸ごと復元する
/// (削除操作そのものを取り消す。DeleteWordLaneAction参照)。
/// モードレス(開いたまま譜面ビューを操作できる)。
/// </summary>
internal sealed class WordLaneManagerWindow : Window
{
    private readonly EditorDocument _doc;
    private readonly Action _onChanged;
    private readonly StackPanel _rowsPanel = new() { Margin = new Thickness(0, 0, 0, 8) };

    public WordLaneManagerWindow(EditorDocument doc, Action onChanged)
    {
        _doc = doc;
        _onChanged = onChanged;

        Title = "歌詞レーンの管理";
        Width = 420;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new DockPanel { Margin = new Thickness(10) };

        var addButton = new Button { Content = "+ 歌詞レーンを追加", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        addButton.Click += (_, _) =>
        {
            _doc.Execute(new AddWordLaneAction($"歌詞{_doc.CurrentTab.WordLanes.Count + 1}"));
            _onChanged();
            RefreshRows();
        };
        DockPanel.SetDock(addButton, Dock.Top);
        root.Children.Add(addButton);

        var closeButton = new Button { Content = "閉じる", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(closeButton, Dock.Bottom);
        root.Children.Add(closeButton);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = _rowsPanel;
        root.Children.Add(scroll);

        Content = root;
        RefreshRows();
    }

    private void RefreshRows()
    {
        _rowsPanel.Children.Clear();
        var lanes = _doc.CurrentTab.WordLanes;
        for (int i = 0; i < lanes.Count; i++)
        {
            int idx = i; // クロージャ用にローカルへ固定
            var lane = lanes[idx];

            var row = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var panel = new StackPanel();

            var nameBox = new TextBox { Text = lane.Name, Margin = new Thickness(0, 0, 0, 4) };
            nameBox.LostFocus += (_, _) =>
            {
                if (lane.Name == nameBox.Text) return;
                _doc.Execute(new RenameWordLaneAction(idx, nameBox.Text));
                _onChanged();
            };
            panel.Children.Add(nameBox);

            var bottomRow = new DockPanel();
            var reverseCheck = new CheckBox { Content = "Reverse専用", VerticalAlignment = VerticalAlignment.Center, IsChecked = lane.IsReverse };
            reverseCheck.Checked += (_, _) => { _doc.Execute(new SetWordLaneReverseAction(idx, true)); _onChanged(); };
            reverseCheck.Unchecked += (_, _) => { _doc.Execute(new SetWordLaneReverseAction(idx, false)); _onChanged(); };
            DockPanel.SetDock(reverseCheck, Dock.Left);
            bottomRow.Children.Add(reverseCheck);

            var entryCount = new TextBlock
            {
                Text = $"({lane.Entries.Count}件)",
                Foreground = System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(entryCount, Dock.Left);
            bottomRow.Children.Add(entryCount);

            var deleteButton = new Button { Content = "削除", Width = 60, HorizontalAlignment = HorizontalAlignment.Right };
            deleteButton.Click += (_, _) =>
            {
                if (lane.Entries.Count > 0)
                {
                    var confirm = MessageBox.Show(this,
                        $"「{lane.Name}」には{lane.Entries.Count}件の歌詞データがあります。削除しますか?(Ctrl+Zで元に戻せます)",
                        "歌詞レーンの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;
                }
                // 2026-07-26: DeleteWordLaneActionへ委譲(削除時点の内容を丸ごと保持し、Undoで復元する)。
                // 後続レーンのindexが詰まる関係上、Word系の選択状態はアクション内でクリアする。
                _doc.Execute(new DeleteWordLaneAction(idx));
                _onChanged();
                RefreshRows();
            };
            bottomRow.Children.Add(deleteButton);

            panel.Children.Add(bottomRow);
            row.Child = panel;
            _rowsPanel.Children.Add(row);
        }

        if (lanes.Count == 0)
        {
            _rowsPanel.Children.Add(new TextBlock
            {
                Text = "歌詞レーンはまだありません。上のボタンから追加してください。",
                FontStyle = FontStyles.Italic,
                Foreground = System.Windows.Media.Brushes.Gray,
            });
        }
    }
}
