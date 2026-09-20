using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Sync;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>共同編集: ノート所有者アイコン(設計メモ6.4節)用の、セッション中のみのephemeralな
/// 所有者追跡(NoteOwnershipTracker)のテスト。2026-09-20実装。</summary>
public class NoteOwnershipTrackerTests
{
    [Fact]
    public void GetNoteOwner_Untracked_ReturnsNull()
    {
        var tracker = new NoteOwnershipTracker();
        Assert.Null(tracker.GetNoteOwner(0, 0, 192));
    }

    [Fact]
    public void Apply_NoteAdded_RecordsAuthor()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 1, 192, present: true, "参加者A"));

        Assert.Equal("参加者A", tracker.GetNoteOwner(0, 1, 192));
    }

    [Fact]
    public void Apply_NoteRemoved_ClearsAuthor()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true, "参加者A"));
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: false, "参加者A"));

        Assert.Null(tracker.GetNoteOwner(0, 0, 192));
    }

    [Fact]
    public void Apply_NoteReplacedByDifferentAuthor_LatestAuthorWins()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true, "参加者A"));
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true, "参加者B"));

        Assert.Equal("参加者B", tracker.GetNoteOwner(0, 0, 192));
    }

    [Fact]
    public void Apply_FreezeSet_RecordsAuthor()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateFreezeSetMessage(0, 0, startTick: 192, endTick: 576, "参加者A"));

        Assert.Equal("参加者A", tracker.GetFreezeOwner(0, 0, 192));
    }

    [Fact]
    public void Apply_FreezeRemoved_ClearsAuthor()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateFreezeSetMessage(0, 0, 192, 576, "参加者A"));
        tracker.Apply(CellDiffApplier.CreateFreezeRemoveMessage(0, 0, 192, "参加者A"));

        Assert.Null(tracker.GetFreezeOwner(0, 0, 192));
    }

    [Fact]
    public void GetNoteOwner_DifferentCellSameLane_IsIndependent()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true, "参加者A"));

        Assert.Equal("参加者A", tracker.GetNoteOwner(0, 0, 192));
        Assert.Null(tracker.GetNoteOwner(0, 0, 384)); // 別tickには影響しない
        Assert.Null(tracker.GetNoteOwner(0, 1, 192)); // 別レーンには影響しない
    }

    [Fact]
    public void Clear_RemovesAllTrackedOwners()
    {
        var tracker = new NoteOwnershipTracker();
        tracker.Apply(CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true, "参加者A"));
        tracker.Apply(CellDiffApplier.CreateFreezeSetMessage(0, 0, 384, 768, "参加者A"));

        tracker.Clear();

        Assert.Null(tracker.GetNoteOwner(0, 0, 192));
        Assert.Null(tracker.GetFreezeOwner(0, 0, 384));
    }
}
