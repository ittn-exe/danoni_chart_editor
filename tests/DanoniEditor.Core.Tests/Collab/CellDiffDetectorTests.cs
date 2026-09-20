using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Sync;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>共同編集: 編集前後のタブ状態からセル差分を検出するテスト(共同編集 設計メモ 2026-09-20、2.2節 セル差分方式への移行)</summary>
public class CellDiffDetectorTests
{
    private const string Author = "p1";

    private static DifficultyTab CreateTabWithLanes(int laneCount)
    {
        var tab = new DifficultyTab();
        for (int i = 0; i < laneCount; i++) tab.Lanes.Add(new LaneNotes());
        return tab;
    }

    [Fact]
    public void Diff_NoteAdded_YieldsSingleNoteCellMessage()
    {
        var tab = CreateTabWithLanes(2);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[1].Notes.Add(192);

        var messages = CellDiffDetector.Diff(tabIndex: 0, before, tab, Author).ToList();

        var message = Assert.Single(messages);
        var noteCell = Assert.IsType<NoteCellChangedMessage>(message);
        Assert.Equal(0, noteCell.TabIndex);
        Assert.Equal(1, noteCell.LaneIndex);
        Assert.Equal(192, noteCell.Tick);
        Assert.Equal(NoteCellState.Note, noteCell.State);
        Assert.Equal(Author, noteCell.AuthorParticipantId);
    }

    [Fact]
    public void Diff_NoteRemoved_YieldsEmptyStateMessage()
    {
        var tab = CreateTabWithLanes(1);
        tab.Lanes[0].Notes.Add(192);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Notes.Remove(192);

        var message = Assert.Single(CellDiffDetector.Diff(0, before, tab, Author));
        var noteCell = Assert.IsType<NoteCellChangedMessage>(message);
        Assert.Equal(192, noteCell.Tick);
        Assert.Equal(NoteCellState.Empty, noteCell.State);
    }

    [Fact]
    public void Diff_NoteMoved_YieldsRemoveThenAdd()
    {
        // 移動は「旧位置をEmpty、新位置をNote」の2件として検出される(CellDiffApplierTests側の前提と一致)
        var tab = CreateTabWithLanes(1);
        tab.Lanes[0].Notes.Add(192);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Notes.Remove(192);
        tab.Lanes[0].Notes.Add(384);

        var messages = CellDiffDetector.Diff(0, before, tab, Author)
            .Cast<NoteCellChangedMessage>()
            .OrderBy(m => m.Tick)
            .ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal(192, messages[0].Tick);
        Assert.Equal(NoteCellState.Empty, messages[0].State);
        Assert.Equal(384, messages[1].Tick);
        Assert.Equal(NoteCellState.Note, messages[1].State);
    }

    [Fact]
    public void Diff_NoChange_YieldsNoMessages()
    {
        var tab = CreateTabWithLanes(1);
        tab.Lanes[0].Notes.Add(192);
        tab.Lanes[0].Freezes.Add(new FreezeNote(384, 768));
        var before = CellDiffDetector.Capture(tab);

        Assert.Empty(CellDiffDetector.Diff(0, before, tab, Author));
    }

    [Fact]
    public void Diff_FreezeAdded_YieldsFreezeSetMessage()
    {
        var tab = CreateTabWithLanes(1);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Freezes.Add(new FreezeNote(192, 576));

        var message = Assert.Single(CellDiffDetector.Diff(0, before, tab, Author));
        var freeze = Assert.IsType<FreezeChangedMessage>(message);
        Assert.Equal(192, freeze.StartTick);
        Assert.Equal(576, freeze.EndTick);
        Assert.Equal(Author, freeze.AuthorParticipantId);
    }

    [Fact]
    public void Diff_FreezeEndTickChanged_YieldsFreezeSetMessage()
    {
        var tab = CreateTabWithLanes(1);
        tab.Lanes[0].Freezes.Add(new FreezeNote(192, 384));
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Freezes[0] = new FreezeNote(192, 576);

        var message = Assert.Single(CellDiffDetector.Diff(0, before, tab, Author));
        var freeze = Assert.IsType<FreezeChangedMessage>(message);
        Assert.Equal(192, freeze.StartTick);
        Assert.Equal(576, freeze.EndTick);
    }

    [Fact]
    public void Diff_FreezeRemoved_YieldsFreezeRemoveMessage()
    {
        var tab = CreateTabWithLanes(1);
        tab.Lanes[0].Freezes.Add(new FreezeNote(192, 576));
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Freezes.RemoveAt(0);

        var message = Assert.Single(CellDiffDetector.Diff(0, before, tab, Author));
        var freeze = Assert.IsType<FreezeChangedMessage>(message);
        Assert.Equal(192, freeze.StartTick);
        Assert.Null(freeze.EndTick);
    }

    [Fact]
    public void Diff_MultipleLanesChanged_YieldsMessagesPerLane()
    {
        var tab = CreateTabWithLanes(3);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes[0].Notes.Add(96);
        tab.Lanes[2].Notes.Add(288);

        var messages = CellDiffDetector.Diff(0, before, tab, Author)
            .Cast<NoteCellChangedMessage>()
            .OrderBy(m => m.LaneIndex)
            .ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal(0, messages[0].LaneIndex);
        Assert.Equal(2, messages[1].LaneIndex);
    }

    [Fact]
    public void Diff_LaneCountShrunkSinceCapture_OnlyComparesCommonRange()
    {
        // テンプレート変更等でレーン数が変わった稀なケース: 超過分は無視して例外を投げない
        var tab = CreateTabWithLanes(3);
        var before = CellDiffDetector.Capture(tab);

        tab.Lanes.RemoveAt(2);
        tab.Lanes[0].Notes.Add(192);

        var messages = CellDiffDetector.Diff(0, before, tab, Author).ToList();

        var message = Assert.Single(messages);
        var noteCell = Assert.IsType<NoteCellChangedMessage>(message);
        Assert.Equal(0, noteCell.LaneIndex);
    }
}
