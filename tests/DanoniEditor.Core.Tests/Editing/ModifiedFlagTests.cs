using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>未保存フラグ(EditorDocument.IsModified、2026-07-19b、未解決事項§2-6)のテスト</summary>
public class ModifiedFlagTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    [Fact]
    public void Execute_SetsModified_AndMarkSavedClears()
    {
        var doc = TestFixtures.NewDocument();
        Assert.False(doc.IsModified);
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        Assert.True(doc.IsModified);
        doc.MarkSaved();
        Assert.False(doc.IsModified);
        doc.Undo(); // 保存後のUndoも「未保存の変更」になる
        Assert.True(doc.IsModified);
    }

    [Fact]
    public void SelectionChange_DoesNotSetModified()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.MarkSaved();
        var ctrl = new SmartToolController(doc);
        var layout = doc.CurrentLayout;
        var col = layout.NoteColumn(0);
        ctrl.BeginLeft(new PointerPos(col.CenterX, layout.TickToY(48 * T)), PointerModifiers.None); // 既存ノート上=選択
        ctrl.End(new PointerPos(col.CenterX, layout.TickToY(48 * T)));
        Assert.Single(doc.Selection);
        Assert.False(doc.IsModified); // 選択しただけでは未保存扱いにならない
    }

    [Fact]
    public void FrameEditModeToggle_DoesNotSetModified()
    {
        var doc = TestFixtures.NewDocument();
        doc.EnterFrameEditMode();
        doc.ExitFrameEditMode(mergeDuplicates: false);
        Assert.False(doc.IsModified);
    }
}
