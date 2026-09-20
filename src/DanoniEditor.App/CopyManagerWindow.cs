using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// コピー元タブとペースト先タブのキー種が異なる場合に表示する、レーン対応指定ウィンドウ(2026-07-31)。
/// 上段にコピー元レーン(実際にノート・フリーズを含むものだけ)、下段にペースト先の全レーンを並べ、
/// 中段の「配置レーン」(ペースト先レーン数と同数のセル)へ、上段のレーンをドラッグ&ドロップして
/// 対応を指定する。1つの配置レーンへ複数のコピー元レーンを重ねる、同じコピー元レーンを複数の
/// 配置レーンへ割り当てる、のいずれも許可する。実行(OK)時、割り当てが決まっていないコピー元レーンが
/// 残っていれば確認ダイアログを出す。キャンセル可。
/// </summary>
internal sealed class CopyManagerWindow : Window
{
    private readonly SmartToolController _controller;
    private readonly AppSettings _settings;

    private readonly KeyTemplate _sourceTemplate;
    private readonly KeyTemplate _destTemplate;
    private readonly IReadOnlyList<int> _sourceLanes;

    private WrapPanel _sourceRow = null!;
    private StackPanel _middleRow = null!;
    private TextBlock _statusText = null!;

    /// <summary>OK操作の結果、実際に貼り付け(PasteWithLaneMapping)が行われたか。呼び出し元は
    /// これがtrueの場合のみ譜面ビュー等を再描画すればよい。</summary>
    public bool Pasted { get; private set; }

    public CopyManagerWindow(EditorDocument doc, SmartToolController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;

        var sourceKeyTypeId = controller.ClipboardSourceKeyTypeId
            ?? throw new InvalidOperationException("クリップボードにコピー元のキー種情報がありません");
        _sourceTemplate = doc.Templates.Get(sourceKeyTypeId);
        _destTemplate = doc.CurrentTemplate;
        _sourceLanes = controller.ClipboardSourceLanesWithObjects();

        Title = "コピーマネージャー";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildLayout();
    }

    // =====================================================================
    // レイアウト構築
    // =====================================================================

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(12), AllowDrop = true };
        root.Drop += OnRootDrop;
        root.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };

        for (int i = 0; i < 10; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions[8].Height = new GridLength(1, GridUnitType.Star); // Expander行だけ残り全部を使う

        var instructions = new TextBlock
        {
            Text = "コピー元タブとペースト先タブのキー種が異なります。下の「コピー元レーン」から「配置レーン」へ" +
                   "ドラッグ&ドロップして、貼り付け先のどのレーンに置くかを指定してください" +
                   "(同じコピー元レーンを複数の配置レーンへ、複数のコピー元レーンを同じ配置レーンへ、どちらも指定可能です)。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(instructions, 0);
        root.Children.Add(instructions);

        var sourceLabel = new TextBlock { Text = "コピー元レーン", FontWeight = FontWeights.Bold };
        Grid.SetRow(sourceLabel, 1);
        root.Children.Add(sourceLabel);

        _sourceRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var idx in _sourceLanes) _sourceRow.Children.Add(MakeSourceCell(idx));
        var sourceScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _sourceRow,
            MaxHeight = 90,
        };
        Grid.SetRow(sourceScroll, 2);
        root.Children.Add(sourceScroll);

        var middleLabel = new TextBlock
        {
            Text = "配置レーン(ここへドラッグ&ドロップ、右クリックまたは×ボタンで解除)",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(middleLabel, 3);
        root.Children.Add(middleLabel);

        _middleRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < _destTemplate.KeyCount; i++) _middleRow.Children.Add(MakeAssignmentCell());
        var middleScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _middleRow,
            MaxHeight = 220,
        };
        Grid.SetRow(middleScroll, 4);
        root.Children.Add(middleScroll);

        var destLabel = new TextBlock { Text = "コピー先レーン", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(destLabel, 5);
        root.Children.Add(destLabel);

        var destRow = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < _destTemplate.KeyCount; i++) destRow.Children.Add(MakeDestCell(i));
        var destScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = destRow,
            MaxHeight = 90,
        };
        Grid.SetRow(destScroll, 6);
        root.Children.Add(destScroll);

        _statusText = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_statusText, 7);
        root.Children.Add(_statusText);

        var expander = new Expander
        {
            Header = "コピー・ペースト設定",
            IsExpanded = false,
            Margin = new Thickness(0, 8, 0, 0),
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildConflictSettingsPanel() },
        };
        Grid.SetRow(expander, 8);
        root.Children.Add(expander);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var okButton = new Button { Content = "実行", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        okButton.Click += OkButton_Click;
        buttonsPanel.Children.Add(okButton);
        buttonsPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonsPanel, 9);
        root.Children.Add(buttonsPanel);

        UpdateStatus();
        return root;
    }

    /// <summary>「コピー・ペースト設定」折り畳み内、6種の衝突解決+1種のプロパティ維持設定。
    /// 値は変更のたびAppSettingsへ即時反映・保存する(この場限りの小さな設定のため、
    /// PreferencesWindowのようなClone→Result方式は取らない)。</summary>
    private StackPanel BuildConflictSettingsPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };

        panel.Children.Add(MakeConflictRow(
            "通常ノート + フリーズ先頭",
            "通常ノートを優先", "note",
            "フリーズを優先", "freeze",
            () => _settings.CopyManagerNoteVsFreezeHeadMode,
            v => _settings.CopyManagerNoteVsFreezeHeadMode = v));

        panel.Children.Add(MakeConflictRow(
            "通常ノート + フリーズ帯",
            "通常ノートの16分手前でフリーズアローを切る", "trim",
            "通常ノートを無視する", "ignoreNote",
            () => _settings.CopyManagerNoteVsFreezeBodyMode,
            v => _settings.CopyManagerNoteVsFreezeBodyMode = v));

        panel.Children.Add(MakeConflictRow(
            "通常ノート + フリーズ終端",
            "通常ノートの16分手前でフリーズアローを切る", "trim",
            "通常ノートを無視する", "ignoreNote",
            () => _settings.CopyManagerNoteVsFreezeTailMode,
            v => _settings.CopyManagerNoteVsFreezeTailMode = v));

        panel.Children.Add(MakeConflictRow(
            "フリーズ先頭 + フリーズ先頭",
            "長い方を残す", "long",
            "短い方を残す", "short",
            () => _settings.CopyManagerFreezeHeadVsHeadMode,
            v => _settings.CopyManagerFreezeHeadVsHeadMode = v));

        panel.Children.Add(MakeConflictRow(
            "フリーズ先頭 + フリーズ帯",
            "後ろのフリーズアローの16分手前で切る", "trim",
            "後ろのフリーズアローを無視", "ignoreFreeze",
            () => _settings.CopyManagerFreezeHeadVsBodyMode,
            v => _settings.CopyManagerFreezeHeadVsBodyMode = v));

        panel.Children.Add(MakeConflictRow(
            "フリーズ先頭 + フリーズ終端",
            "後ろのフリーズアローの16分手前で切る", "trim",
            "後ろのフリーズアローを無視", "ignoreFreeze",
            () => _settings.CopyManagerFreezeHeadVsTailMode,
            v => _settings.CopyManagerFreezeHeadVsTailMode = v));

        panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });

        panel.Children.Add(MakeConflictRow(
            "プロパティの維持(色編集モードの色・コメント/警告注釈)",
            "持ち越す", "carryOver",
            "持ち越さない", "discard",
            () => _settings.CopyManagerPreservePropertiesMode,
            v => _settings.CopyManagerPreservePropertiesMode = v));

        return panel;
    }

    private StackPanel MakeConflictRow(string title, string labelA, string valueA, string labelB, string valueB,
        Func<string> getter, Action<string> setter)
    {
        var groupName = "cg_" + Guid.NewGuid().ToString("N");
        var rbA = new RadioButton { Content = labelA, GroupName = groupName, Margin = new Thickness(0, 0, 16, 0) };
        var rbB = new RadioButton { Content = labelB, GroupName = groupName };
        rbA.IsChecked = getter() == valueA;
        rbB.IsChecked = getter() == valueB;
        rbA.Checked += (_, _) => { setter(valueA); SaveSettings(); };
        rbB.Checked += (_, _) => { setter(valueB); SaveSettings(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(rbA);
        row.Children.Add(rbB);

        var container = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        container.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        container.Children.Add(row);
        return container;
    }

    private void SaveSettings() => _settings.Save(AppPaths.SettingsFilePath);

    // =====================================================================
    // レーン表示セル(コピー元/配置/コピー先で共通の表示、2026-08-01: サイズ統一対応)
    // =====================================================================

    /// <summary>コピー元レーン・配置レーン(バッジ)・コピー先レーンの3箇所で共通して使う
    /// アイコンサイズ・セル幅。ここを揃えることで3段の表示サイズが一致する(2026-08-01要望対応)。</summary>
    private const double LaneIconSize = 40;
    private const double LaneCellWidth = 56;

    /// <summary>レーンアイコン+ラベルの表示部分(コピー元/コピー先/配置バッジで共通のビジュアル)。
    /// 2026-08-01: 配置レーンのバッジは外枠(配置レーンセル本体)の幅に合わせて伸縮させる都合上、
    /// 中身は常に中央寄せにしておく(そうしないとLaneIconElementの描画基準点が左に寄って見える)。</summary>
    private static StackPanel BuildLaneVisual(LaneDef lane)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new LaneIconElement(lane, LaneIconSize));
        stack.Children.Add(new TextBlock
        {
            Text = lane.LaneId,
            FontSize = 10,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return stack;
    }

    private static Border MakeLaneCell(LaneDef lane, bool draggable) => new()
    {
        Width = LaneCellWidth,
        Margin = new Thickness(4, 0, 4, 0),
        Padding = new Thickness(2),
        BorderBrush = draggable ? Brushes.Gray : Brushes.Transparent,
        BorderThickness = new Thickness(1),
        Background = Brushes.Black,
        Child = BuildLaneVisual(lane),
    };

    private Border MakeSourceCell(int sourceLaneIndex)
    {
        var lane = _sourceTemplate.Lanes[sourceLaneIndex];
        var cell = MakeLaneCell(lane, draggable: true);
        cell.Cursor = Cursors.Hand;

        Point dragStart = default;
        bool dragArmed = false;
        cell.PreviewMouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(null); dragArmed = true; };
        cell.PreviewMouseMove += (_, e) =>
        {
            if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragArmed = false;
            DragDrop.DoDragDrop(cell, new SourceDragToken(sourceLaneIndex), DragDropEffects.Move);
        };
        return cell;
    }

    private Border MakeDestCell(int destLaneIndex) => MakeLaneCell(_destTemplate.Lanes[destLaneIndex], draggable: false);

    // =====================================================================
    // 配置レーン(中段、D&Dの受け皿)
    // =====================================================================

    /// <summary>配置レーンの1セル分。コピー元レーンからのドロップで新規バッジを追加、
    /// 既存バッジのドロップで付け替えを受け付ける。バッジがコピー元レーンと同じ表示サイズになった分
    /// (2026-08-01要望対応)、複数のバッジが積まれた場合はStackPanelの自然な高さ増加に任せて
    /// セルの縦幅がそのまま伸びる(明示的な計算は不要)。
    /// 2026-08-01: セル本体の幅はコピー元/コピー先レーンと完全に同じLaneCellWidthに揃え、
    /// バッジ側は固定幅を持たせず(MakeBadge参照)このセルの幅にそのまま追従させることで、
    /// 「配置レーンだけ枠が二重に太る」ような幅のズレが出ないようにしている。</summary>
    private Border MakeAssignmentCell()
    {
        var badgesPanel = new StackPanel { Orientation = Orientation.Vertical };
        var cell = new Border
        {
            Width = LaneCellWidth,
            MinHeight = LaneIconSize + 24, // バッジ1個分(アイコン+ラベル+枠)の最小高さ
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(2),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            AllowDrop = true,
            Child = badgesPanel,
        };
        cell.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(SourceDragToken)) || e.Data.GetDataPresent(typeof(BadgeDragToken))
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        cell.Drop += (_, e) => OnCellDrop(badgesPanel, e);
        return cell;
    }

    private void OnCellDrop(StackPanel badgesPanel, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SourceDragToken)) is SourceDragToken src)
        {
            badgesPanel.Children.Add(MakeBadge(src.SourceLaneIndex));
            e.Handled = true;
            UpdateStatus();
            return;
        }

        if (e.Data.GetData(typeof(BadgeDragToken)) is BadgeDragToken bd)
        {
            if (bd.Badge.Parent is Panel oldParent) oldParent.Children.Remove(bd.Badge);
            badgesPanel.Children.Add(bd.Badge);
            e.Handled = true;
            UpdateStatus();
        }
    }

    /// <summary>配置レーン上のバッジ(=1件のコピー元→貼り付け先レーン対応)。
    /// ドラッグして別の配置レーンへ移す、右クリックの「削除」、×ボタン、配置レーン外へドラッグ、
    /// の4通りで扱える(うち3通りは削除、1通りは付け替え、2026-07-31要望対応)。
    /// 2026-08-01要望対応: 表示はコピー元レーン(MakeLaneCell)と同じアイコンサイズにし、
    /// ×ボタンはアイコン+ラベルの上に重ねて右上に配置する(横並びにすると幅が揃わなくなるため)。
    /// バッジ自体には固定幅を持たせず、親のMakeAssignmentCell(幅=LaneCellWidthでコピー元/
    /// コピー先と統一済み)にHorizontalAlignment=Stretchで追従させる。バッジ側にも固定幅を
    /// 持たせると「配置レーンの外枠+バッジ自身の枠」が二重に加算され、コピー元/コピー先より
    /// 明らかに幅広く見えてしまっていた(不具合修正)。</summary>
    private Border MakeBadge(int sourceLaneIndex)
    {
        var lane = _sourceTemplate.Lanes[sourceLaneIndex];

        var closeButton = new Button
        {
            Content = "×",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
        };

        var overlay = new Grid();
        overlay.Children.Add(BuildLaneVisual(lane));
        overlay.Children.Add(closeButton);

        var badge = new Border
        {
            Tag = sourceLaneIndex,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(1),
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Cursor = Cursors.SizeAll,
            Child = overlay,
        };

        closeButton.Click += (_, _) => RemoveBadge(badge);

        // 2026-08-01要望対応: 譜面ビューの右クリック削除と同じ操作感にするため、メニューを介さず
        // 右クリック即座に削除する(コンテキストメニューは出さない)。
        badge.MouseRightButtonDown += (_, e) =>
        {
            RemoveBadge(badge);
            e.Handled = true;
        };

        Point dragStart = default;
        bool dragArmed = false;
        badge.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Button) return; // ×ボタン自体のクリックはドラッグ開始にしない
            dragStart = e.GetPosition(null);
            dragArmed = true;
        };
        badge.PreviewMouseMove += (_, e) =>
        {
            if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragArmed = false;
            DragDrop.DoDragDrop(badge, new BadgeDragToken(sourceLaneIndex, badge), DragDropEffects.Move);
        };

        return badge;
    }

    private void RemoveBadge(Border badge)
    {
        if (badge.Parent is Panel parent) parent.Children.Remove(badge);
        UpdateStatus();
    }

    /// <summary>配置レーンのどのセルにも収まらなかったドロップ(=ウィンドウの背景等)を、
    /// バッジの削除として扱う(「つまんでレーン領域外へドラッグ」による解除、2026-07-31要望対応)。
    /// AssignmentCell側のDropハンドラがe.Handled=trueを設定した場合はここまでバブリングしてこない。</summary>
    private void OnRootDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(typeof(BadgeDragToken)) is BadgeDragToken bd)
        {
            RemoveBadge(bd.Badge);
            e.Handled = true;
        }
    }

    // =====================================================================
    // 状態表示・実行
    // =====================================================================

    private List<int> GetAssignedSourceLaneIndices()
    {
        var result = new List<int>();
        foreach (var cellObj in _middleRow.Children)
        {
            if (cellObj is not Border { Child: StackPanel panel }) continue;
            foreach (var badgeObj in panel.Children)
                if (badgeObj is Border { Tag: int srcIdx }) result.Add(srcIdx);
        }
        return [.. result.Distinct()];
    }

    private List<(int SourceLane, int DestLane)> BuildMapping()
    {
        var result = new List<(int, int)>();
        for (int destIndex = 0; destIndex < _middleRow.Children.Count; destIndex++)
        {
            if (_middleRow.Children[destIndex] is not Border { Child: StackPanel panel }) continue;
            foreach (var badgeObj in panel.Children)
                if (badgeObj is Border { Tag: int srcIdx }) result.Add((srcIdx, destIndex));
        }
        return result;
    }

    private void UpdateStatus()
    {
        var assigned = GetAssignedSourceLaneIndices();
        var missing = _sourceLanes.Except(assigned).ToList();
        _statusText.Text = missing.Count == 0
            ? ""
            : "未配置のコピー元レーン: " + string.Join(", ", missing.Select(i => _sourceTemplate.Lanes[i].LaneId));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var mapping = BuildMapping();
        if (mapping.Count == 0)
        {
            MessageBox.Show(this,
                "配置レーンに何も割り当てられていません。コピー元レーンをドラッグ&ドロップして対応を指定してください。",
                "コピーマネージャー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var assigned = mapping.Select(m => m.SourceLane).Distinct().ToList();
        var missing = _sourceLanes.Except(assigned).ToList();
        if (missing.Count > 0)
        {
            var result = MessageBox.Show(this,
                "コピー元の全てのレーンが配置されていません。コピーを実行しますか?",
                "コピーマネージャー", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return; // キャンセル: マネージャーに戻る
        }

        var conflictOptions = new PasteConflictOptions(
            _settings.CopyManagerNoteVsFreezeHeadMode,
            _settings.CopyManagerNoteVsFreezeBodyMode,
            _settings.CopyManagerNoteVsFreezeTailMode,
            _settings.CopyManagerFreezeHeadVsHeadMode,
            _settings.CopyManagerFreezeHeadVsBodyMode,
            _settings.CopyManagerFreezeHeadVsTailMode);
        bool preserveProperties = _settings.CopyManagerPreservePropertiesMode == "carryOver";

        Pasted = _controller.PasteWithLaneMapping(mapping, conflictOptions, preserveProperties);
        DialogResult = true;
        Close();
    }

    // =====================================================================
    // ドラッグ用ペイロード・アイコン描画
    // =====================================================================

    private sealed record SourceDragToken(int SourceLaneIndex);

    private sealed record BadgeDragToken(int SourceLaneIndex, Border Badge);

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
