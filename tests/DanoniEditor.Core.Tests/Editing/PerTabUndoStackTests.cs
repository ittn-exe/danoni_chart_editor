using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>2026-08-06不具合修正の回帰テスト: Undo/Redo履歴を難易度タブごとに分離した件。
/// 従来はUndoStackがドキュメント全体で1本しか無く、IEditActionが実行「時点」のCurrentTabを
/// 対象にする設計と噛み合わず、「タブAで削除 → タブBへ切替 → Ctrl+Z」でタブBに存在しなかった
/// ノートが追加されるなど、エラーを出さずに別タブの譜面が壊れる不具合があった。</summary>
public class PerTabUndoStackTests
{
    private static EditorDocument TwoTabDocument()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject
        {
            ProjectName = "t",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));
        return new EditorDocument(project, repo);
    }

    [Fact]
    public void UndoAfterTabSwitch_DoesNotTouchTheOtherTab()
    {
        var doc = TwoTabDocument();

        // タブAでノートを配置
        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        Assert.Contains(480L, doc.Project.Tabs[0].Lanes[0].Notes);

        // タブBへ切替。タブB側にはまだ何の履歴も無いのでUndoできない
        doc.CurrentTabIndex = 1;
        Assert.False(doc.UndoStack.CanUndo);
        Assert.False(doc.Undo());

        // タブAのノートも、タブBのレーンも一切変化していない
        Assert.Contains(480L, doc.Project.Tabs[0].Lanes[0].Notes);
        Assert.Empty(doc.Project.Tabs[1].Lanes[0].Notes);
    }

    [Fact]
    public void DeleteThenSwitchThenUndo_DoesNotInjectPhantomNoteIntoOtherTab()
    {
        var doc = TwoTabDocument();

        // タブAにノートを用意し、削除する(履歴に残す)
        doc.CurrentTabIndex = 0;
        doc.Project.Tabs[0].Lanes[0].Notes.Add(480);
        doc.Execute(new DeleteNoteAction(0, 480));
        Assert.Empty(doc.Project.Tabs[0].Lanes[0].Notes);

        // タブBへ切替してUndo。従来はここでタブBへ「元々無かったノート」が追加されてしまっていた
        doc.CurrentTabIndex = 1;
        doc.Undo();

        Assert.Empty(doc.Project.Tabs[1].Lanes[0].Notes);
    }

    [Fact]
    public void EachTabKeepsItsOwnHistory_AndUndoAppliesToTheCorrectTab()
    {
        var doc = TwoTabDocument();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.CurrentTabIndex = 1;
        doc.Execute(new PlaceNoteAction(1, 960));

        // タブBでUndo → タブBのノートだけが消える
        doc.Undo();
        Assert.Empty(doc.Project.Tabs[1].Lanes[1].Notes);
        Assert.Contains(480L, doc.Project.Tabs[0].Lanes[0].Notes);

        // タブAへ戻ればタブA自身の履歴が生きている
        doc.CurrentTabIndex = 0;
        Assert.True(doc.UndoStack.CanUndo);
        doc.Undo();
        Assert.Empty(doc.Project.Tabs[0].Lanes[0].Notes);
    }

    [Fact]
    public void RedoIsAlsoPerTab()
    {
        var doc = TwoTabDocument();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Undo();
        Assert.True(doc.UndoStack.CanRedo);

        // タブBにはRedo対象が無い
        doc.CurrentTabIndex = 1;
        Assert.False(doc.UndoStack.CanRedo);
        Assert.False(doc.Redo());
        Assert.Empty(doc.Project.Tabs[1].Lanes[0].Notes);

        // タブAへ戻ればRedoできる
        doc.CurrentTabIndex = 0;
        Assert.True(doc.Redo());
        Assert.Contains(480L, doc.Project.Tabs[0].Lanes[0].Notes);
    }

    [Fact]
    public void UndoCapacity_AppliesToAllTabsIncludingOnesCreatedLater()
    {
        var doc = TwoTabDocument();
        doc.UndoCapacity = 3;

        doc.CurrentTabIndex = 0;
        for (int i = 1; i <= 5; i++) doc.Execute(new PlaceNoteAction(0, i * 480));
        Assert.Equal(3, doc.UndoStack.UndoDepth);

        // 容量設定後に初めて履歴が作られるタブにも同じ容量が適用される
        doc.CurrentTabIndex = 1;
        for (int i = 1; i <= 5; i++) doc.Execute(new PlaceNoteAction(0, i * 480));
        Assert.Equal(3, doc.UndoStack.UndoDepth);
    }

    [Fact]
    public void DuplicatedTab_StartsWithEmptyHistory()
    {
        var doc = TwoTabDocument();
        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));

        // 複製タブは新しいTabIdを採番するため、履歴は引き継がれない
        var clone = doc.Project.Tabs[0].Clone();
        doc.Project.Tabs.Add(clone);
        doc.NotifyTabsChanged(doc.Project.Tabs.Count - 1);

        Assert.False(doc.UndoStack.CanUndo);
    }

    [Fact]
    public void ClosingTab_DiscardsItsHistory_WithoutAffectingOthers()
    {
        var doc = TwoTabDocument();

        doc.CurrentTabIndex = 1;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 960));

        // タブBを閉じる(タブを閉じる操作自体はUndo対象外)
        ProjectOperations.RemoveTab(doc.Project, 1);
        doc.NotifyTabsChanged(0);

        // 残ったタブAの履歴は健在
        Assert.True(doc.UndoStack.CanUndo);
        doc.Undo();
        Assert.Empty(doc.Project.Tabs[0].Lanes[0].Notes);
    }
}
