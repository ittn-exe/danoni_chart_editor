using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Sync;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>共同編集: セル差分の生成・適用のテスト(共同編集 設計メモ 2026-09-20、2.2節)</summary>
public class CellDiffApplierTests
{
    private static ChartProject CreateProjectWithLanes(int laneCount)
    {
        var tab = new DifficultyTab();
        for (int i = 0; i < laneCount; i++) tab.Lanes.Add(new LaneNotes());
        return new ChartProject { Tabs = { tab } };
    }

    [Fact]
    public void ApplyNoteCell_Note_AddsTick()
    {
        var project = CreateProjectWithLanes(2);
        var message = CellDiffApplier.CreateNoteCellMessage(tabIndex: 0, laneIndex: 1, tick: 192, present: true);

        CellDiffApplier.ApplyNoteCell(project, message);

        Assert.Equal([192L], project.Tabs[0].Lanes[1].Notes);
        Assert.Empty(project.Tabs[0].Lanes[0].Notes); // 他のレーンには影響しない
    }

    [Fact]
    public void ApplyNoteCell_Empty_RemovesTick()
    {
        var project = CreateProjectWithLanes(1);
        project.Tabs[0].Lanes[0].Notes.Add(192);

        CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: false));

        Assert.Empty(project.Tabs[0].Lanes[0].Notes);
    }

    [Fact]
    public void ApplyNoteCell_SameTickTwice_DoesNotDuplicate()
    {
        var project = CreateProjectWithLanes(1);

        CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true));
        CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true));

        Assert.Equal([192L], project.Tabs[0].Lanes[0].Notes);
    }

    [Fact]
    public void ApplyNoteCell_Move_IsExpressedAsEmptyThenNote()
    {
        // 設計メモ2.2節: 移動は「旧位置をEmpty、新位置をNote」の2件のメッセージとして表現する
        var project = CreateProjectWithLanes(1);
        project.Tabs[0].Lanes[0].Notes.Add(192);

        CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: false));
        CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(0, 0, 384, present: true));

        Assert.Equal([384L], project.Tabs[0].Lanes[0].Notes);
    }

    [Fact]
    public void ApplyFreeze_Set_AddsFreezeNote()
    {
        var project = CreateProjectWithLanes(1);

        CellDiffApplier.ApplyFreeze(project, CellDiffApplier.CreateFreezeSetMessage(0, 0, startTick: 192, endTick: 576));

        var freeze = Assert.Single(project.Tabs[0].Lanes[0].Freezes);
        Assert.Equal(192, freeze.StartTick);
        Assert.Equal(576, freeze.EndTick);
    }

    [Fact]
    public void ApplyFreeze_SetOnExistingStartTick_ReplacesIt()
    {
        var project = CreateProjectWithLanes(1);
        project.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(192, 384));

        // 終点だけを384→576へ伸ばす更新(内部的には同一StartTickの置き換え)
        CellDiffApplier.ApplyFreeze(project, CellDiffApplier.CreateFreezeSetMessage(0, 0, startTick: 192, endTick: 576));

        var freeze = Assert.Single(project.Tabs[0].Lanes[0].Freezes);
        Assert.Equal(576, freeze.EndTick);
    }

    [Fact]
    public void ApplyFreeze_Remove_DeletesMatchingStartTick()
    {
        var project = CreateProjectWithLanes(1);
        project.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(192, 576));

        CellDiffApplier.ApplyFreeze(project, CellDiffApplier.CreateFreezeRemoveMessage(0, 0, startTick: 192));

        Assert.Empty(project.Tabs[0].Lanes[0].Freezes);
    }

    [Fact]
    public void ApplyNoteCell_OutOfRangeTabIndex_Throws()
    {
        var project = CreateProjectWithLanes(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(tabIndex: 5, laneIndex: 0, tick: 0, present: true)));
    }

    [Fact]
    public void ApplyNoteCell_OutOfRangeLaneIndex_Throws()
    {
        var project = CreateProjectWithLanes(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CellDiffApplier.ApplyNoteCell(project, CellDiffApplier.CreateNoteCellMessage(tabIndex: 0, laneIndex: 9, tick: 0, present: true)));
    }

    [Fact]
    public void SnapshotSync_RoundTrip_PreservesNotes()
    {
        var project = CreateProjectWithLanes(1);
        project.Tabs[0].Lanes[0].Notes.Add(192);
        project.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(384, 768));

        var message = SnapshotSync.CreateSnapshot(project);
        var restored = SnapshotSync.ApplySnapshot(message);

        Assert.Equal([192L], restored.Tabs[0].Lanes[0].Notes);
        Assert.Equal(new FreezeNote(384, 768), Assert.Single(restored.Tabs[0].Lanes[0].Freezes));
    }
}
