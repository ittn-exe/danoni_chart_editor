using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// ノート/フリーズのコメント・警告(Annotations、2026-07-26)のテスト。
/// - SetAnnotationActionの設定/クリア/Undo
/// - 削除・移動・リサイズ時のサイドカー追随(ColorOverridesと同規約)
/// - 空エントリ(Comment=""かつWarning=false)を残さない規約
/// </summary>
public class AnnotationTests
{
    private static NoteAnnotation? AnnotationAt(EditorDocument doc, int lane, long tick) =>
        doc.CurrentTab.Lanes[lane].Annotations.FirstOrDefault(a => a.Tick == tick);

    [Fact]
    public void SetAnnotation_AddsAndUndoRestores()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 100));

        doc.Execute(new SetAnnotationAction(0, 100, "テストコメント", warning: true));
        var a = AnnotationAt(doc, 0, 100);
        Assert.NotNull(a);
        Assert.Equal("テストコメント", a!.Comment);
        Assert.True(a.Warning);

        doc.Undo();
        Assert.Null(AnnotationAt(doc, 0, 100));
    }

    [Fact]
    public void SetAnnotation_EmptyCommentAndNoWarning_RemovesEntry()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 100));
        doc.Execute(new SetAnnotationAction(0, 100, "コメント", warning: true));

        // 警告OFF+コメント空 → エントリ自体が消える(空エントリを残さない規約)
        doc.Execute(new SetAnnotationAction(0, 100, "", warning: false));
        Assert.Empty(doc.CurrentTab.Lanes[0].Annotations);

        doc.Undo();
        Assert.NotNull(AnnotationAt(doc, 0, 100));
    }

    [Fact]
    public void DeleteNote_RemovesAnnotation_AndUndoRestores()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 100));
        doc.Execute(new SetAnnotationAction(0, 100, "警告付き", warning: true));

        doc.Execute(new DeleteNoteAction(0, 100));
        Assert.Empty(doc.CurrentTab.Lanes[0].Annotations);

        doc.Undo();
        var a = AnnotationAt(doc, 0, 100);
        Assert.NotNull(a);
        Assert.True(a!.Warning);
    }

    [Fact]
    public void DeleteFreeze_RemovesAnnotation_AndUndoRestores()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(1, 100, 200));
        doc.Execute(new SetAnnotationAction(1, 100, "フリーズ警告", warning: true));

        doc.Execute(new DeleteFreezeAction(1, 100));
        Assert.Empty(doc.CurrentTab.Lanes[1].Annotations);

        doc.Undo();
        Assert.NotNull(AnnotationAt(doc, 1, 100));
    }

    [Fact]
    public void MoveObjects_AnnotationFollowsNote()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 100));
        doc.Execute(new SetAnnotationAction(0, 100, "追随テスト", warning: true));

        doc.Execute(new MoveObjectsAction([new ObjectRef(ObjectKind.Note, 0, 100)], laneDelta: 1, tickDelta: 50));
        Assert.Null(AnnotationAt(doc, 0, 100));
        var moved = AnnotationAt(doc, 1, 150);
        Assert.NotNull(moved);
        Assert.Equal("追随テスト", moved!.Comment);

        doc.Undo();
        Assert.NotNull(AnnotationAt(doc, 0, 100));
        Assert.Null(AnnotationAt(doc, 1, 150));
    }

    [Fact]
    public void ResizeFreeze_StartTickChange_AnnotationFollows()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 100, 200));
        doc.Execute(new SetAnnotationAction(0, 100, "リサイズ追随", warning: true));

        var f = doc.CurrentTab.Lanes[0].Freezes.First(x => x.StartTick == 100);
        doc.Execute(new ResizeFreezeAction(0, f, 80, 200));
        Assert.Null(AnnotationAt(doc, 0, 100));
        Assert.NotNull(AnnotationAt(doc, 0, 80));

        doc.Undo();
        Assert.NotNull(AnnotationAt(doc, 0, 100));
        Assert.Null(AnnotationAt(doc, 0, 80));
    }
}
