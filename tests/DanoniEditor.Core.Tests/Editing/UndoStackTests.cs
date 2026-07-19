using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

public class UndoStackTests
{
    [Fact]
    public void Push_Then_Undo_RevertsNote()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);

        var undone = doc.Undo();
        Assert.True(undone);
        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void Redo_ReappliesAction()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Undo();
        var redone = doc.Redo();
        Assert.True(redone);
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void Push_AfterUndo_ClearsRedoHistory()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Undo();
        Assert.Equal(1, doc.UndoStack.RedoDepth);

        doc.Execute(new PlaceNoteAction(0, 96));
        Assert.Equal(0, doc.UndoStack.RedoDepth);
        Assert.False(doc.Redo());
    }

    [Fact]
    public void UndoRedo_Ordering_IsLastInFirstOut()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 0));
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96));

        doc.Undo(); // removes 96
        Assert.DoesNotContain(96L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);

        doc.Undo(); // removes 48
        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);

        doc.Redo(); // restores 48
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.DoesNotContain(96L, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void Capacity_DropsOldestUndoHistory_WhenExceeded()
    {
        var doc = TestFixtures.NewDocument();
        doc.UndoStack.Capacity = 3;
        for (int i = 0; i < 5; i++) doc.Execute(new PlaceNoteAction(0, i * 48));

        Assert.Equal(3, doc.UndoStack.UndoDepth);
        // 3回までしか戻せない = 先頭2件(i=0,1で置いたノート)はUndoで消せない
        doc.Undo(); doc.Undo(); doc.Undo();
        Assert.False(doc.Undo());
        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void DefaultCapacity_Is30()
    {
        Assert.Equal(30, UndoStack.DefaultCapacity);
        Assert.Equal(30, new UndoStack().Capacity);
    }
}
