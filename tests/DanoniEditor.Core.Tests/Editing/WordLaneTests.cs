using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>歌詞レーン(WordLanes、2026-07-23、TBD 4)のレイアウト・編集操作のテスト。
/// レーンはタブごとに可変本数のため、ChartLayoutのSyncWordLaneCountとEditorDocument.CurrentLayoutの
/// 同期挙動、HitTest/ObjectsInRect/配置/削除/移動の一連の流れを確認する。</summary>
public class WordLaneTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48;

    private static (EditorDocument Doc, SmartToolController Ctrl) NewSceneWithWordLane()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });
        return (doc, new SmartToolController(doc));
    }

    private static PointerPos At(ColumnInfo col, ChartLayout layout, long tick) => new(col.CenterX, layout.TickToY(tick));

    private static void Click(SmartToolController ctrl, PointerPos pos, PointerModifiers mods = PointerModifiers.None)
    {
        ctrl.BeginLeft(pos, mods);
        ctrl.End(pos);
    }

    [Fact]
    public void CurrentLayout_NoWordLanes_HasNoWordColumn()
    {
        var doc = TestFixtures.NewDocument();
        Assert.DoesNotContain(doc.CurrentLayout.Columns, c => c.Kind == ColumnKind.Word);
    }

    [Fact]
    public void CurrentLayout_AfterAddingWordLane_GainsWordColumn()
    {
        var doc = TestFixtures.NewDocument();
        Assert.DoesNotContain(doc.CurrentLayout.Columns, c => c.Kind == ColumnKind.Word);

        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });

        Assert.Single(doc.CurrentLayout.Columns.Where(c => c.Kind == ColumnKind.Word));
    }

    [Fact]
    public void CurrentLayout_TwoWordLanes_ProducesTwoOrderedColumns()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞(Rev)", IsReverse = true });

        var wordCols = doc.CurrentLayout.Columns.Where(c => c.Kind == ColumnKind.Word).ToList();
        Assert.Equal(2, wordCols.Count);
        Assert.Equal(0, wordCols[0].NoteLaneIndex);
        Assert.Equal(1, wordCols[1].NoteLaneIndex);
    }

    [Fact]
    public void ClickEmptyWordLane_PlacesEntry_WithDefaultValues()
    {
        var (doc, ctrl) = NewSceneWithWordLane();
        var layout = doc.CurrentLayout;
        var col = layout.WordColumn(0);

        Click(ctrl, At(col, layout, 96 * T));

        var entry = Assert.Single(doc.CurrentTab.WordLanes[0].Entries);
        Assert.Equal(96 * T, entry.Tick);
        Assert.Equal(0, entry.Position);
        Assert.Equal(WordEntryKind.Lyrics, entry.Kind);
        Assert.Equal("", entry.Text);
    }

    [Fact]
    public void ClickPlacedWordEntry_SelectsIt()
    {
        var (doc, ctrl) = NewSceneWithWordLane();
        var layout = doc.CurrentLayout;
        var col = layout.WordColumn(0);
        Click(ctrl, At(col, layout, 96 * T));

        Click(ctrl, At(col, layout, 96 * T)); // 既存オブジェクト上のクリック=選択

        var selected = Assert.Single(doc.Selection);
        Assert.Equal(ObjectKind.Word, selected.Kind);
        Assert.Equal(0, selected.Lane);
        Assert.Equal(96 * T, selected.Tick);
    }

    [Fact]
    public void DeleteSelection_RemovesWordEntry()
    {
        var (doc, ctrl) = NewSceneWithWordLane();
        var layout = doc.CurrentLayout;
        var col = layout.WordColumn(0);
        Click(ctrl, At(col, layout, 96 * T));
        Click(ctrl, At(col, layout, 96 * T)); // 選択

        Assert.True(ctrl.DeleteSelection());

        Assert.Empty(doc.CurrentTab.WordLanes[0].Entries);
    }

    [Fact]
    public void EditWordEntryAction_UpdatesFieldsAndIsUndoable()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });
        doc.Execute(new PlaceWordEntryAction(0, 48 * T));

        doc.Execute(new EditWordEntryAction(0, 48 * T, new WordEntry(48 * T, 1, WordEntryKind.Lyrics, "こんにちは")));

        var entry = Assert.Single(doc.CurrentTab.WordLanes[0].Entries);
        Assert.Equal(1, entry.Position);
        Assert.Equal("こんにちは", entry.Text);

        Assert.True(doc.Undo());
        var reverted = Assert.Single(doc.CurrentTab.WordLanes[0].Entries);
        Assert.Equal(0, reverted.Position);
        Assert.Equal("", reverted.Text);
    }

    [Fact]
    public void PlaceWordEntryAction_IsUndoable()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });

        doc.Execute(new PlaceWordEntryAction(0, 48 * T));
        Assert.Single(doc.CurrentTab.WordLanes[0].Entries);

        Assert.True(doc.Undo());
        Assert.Empty(doc.CurrentTab.WordLanes[0].Entries);
    }

    [Fact]
    public void DragMoveWordEntry_ChangesTick_KeepsSameLane()
    {
        var (doc, ctrl) = NewSceneWithWordLane();
        var layout = doc.CurrentLayout;
        var col = layout.WordColumn(0);
        Click(ctrl, At(col, layout, 96 * T));
        Click(ctrl, At(col, layout, 96 * T)); // 選択

        // 同一レーン内でtickだけ動かすドラッグ(X座標は変えない)
        ctrl.BeginLeft(At(col, layout, 96 * T), PointerModifiers.None);
        var to = At(col, layout, 192 * T);
        for (int i = 1; i <= 10; i++)
        {
            double t = i / 10.0;
            ctrl.Move(new PointerPos(col.CenterX, layout.TickToY(96 * T) + (layout.TickToY(192 * T) - layout.TickToY(96 * T)) * t));
        }
        ctrl.End(to);

        var entry = Assert.Single(doc.CurrentTab.WordLanes[0].Entries);
        Assert.Equal(192 * T, entry.Tick);
    }

    // =====================================================================
    // 歌詞レーン自体の追加/改名/Reverse切替/削除のUndo対応(2026-07-31)
    // =====================================================================

    [Fact]
    public void AddWordLaneAction_IsUndoable()
    {
        var doc = TestFixtures.NewDocument();

        doc.Execute(new AddWordLaneAction("歌詞"));
        Assert.Single(doc.CurrentTab.WordLanes);

        Assert.True(doc.Undo());
        Assert.Empty(doc.CurrentTab.WordLanes);
    }

    [Fact]
    public void RenameWordLaneAction_IsUndoable()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "旧名" });

        doc.Execute(new RenameWordLaneAction(0, "新名"));
        Assert.Equal("新名", doc.CurrentTab.WordLanes[0].Name);

        Assert.True(doc.Undo());
        Assert.Equal("旧名", doc.CurrentTab.WordLanes[0].Name);
    }

    [Fact]
    public void SetWordLaneReverseAction_IsUndoable()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });

        doc.Execute(new SetWordLaneReverseAction(0, true));
        Assert.True(doc.CurrentTab.WordLanes[0].IsReverse);

        Assert.True(doc.Undo());
        Assert.False(doc.CurrentTab.WordLanes[0].IsReverse);
    }

    [Fact]
    public void DeleteWordLaneAction_UndoRestoresLaneWithItsEntries()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });
        doc.Execute(new PlaceWordEntryAction(0, 48 * T));
        doc.Execute(new EditWordEntryAction(0, 48 * T, new WordEntry(48 * T, 2, WordEntryKind.Lyrics, "あいうえお")));

        doc.Execute(new DeleteWordLaneAction(0));
        Assert.Empty(doc.CurrentTab.WordLanes);

        Assert.True(doc.Undo());
        var lane = Assert.Single(doc.CurrentTab.WordLanes);
        var entry = Assert.Single(lane.Entries);
        Assert.Equal("あいうえお", entry.Text);
        Assert.Equal(2, entry.Position);
    }

    [Fact]
    public void DeleteWordLaneAction_ClearsWordSelectionOnDoAndUndo()
    {
        var (doc, ctrl) = NewSceneWithWordLane();
        var layout = doc.CurrentLayout;
        var col = layout.WordColumn(0);
        Click(ctrl, At(col, layout, 96 * T));
        Click(ctrl, At(col, layout, 96 * T)); // 選択
        Assert.Single(doc.Selection);

        doc.Execute(new DeleteWordLaneAction(0));
        Assert.Empty(doc.Selection);

        doc.Undo();
        Assert.Empty(doc.Selection); // 復元後も選択の連続性は保証しない
    }

    [Fact]
    public void DeleteWordLaneAction_Redo_RemovesAgain()
    {
        var doc = TestFixtures.NewDocument();
        doc.CurrentTab.WordLanes.Add(new WordLane { Name = "歌詞" });

        doc.Execute(new DeleteWordLaneAction(0));
        doc.Undo();
        Assert.True(doc.Redo());

        Assert.Empty(doc.CurrentTab.WordLanes);
    }
}
