using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

public class EditActionsTests
{
    [Fact]
    public void PlaceAndDeleteFreeze_RoundTrips()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        Assert.Contains(doc.CurrentTab.Lanes[0].Freezes, f => f.StartTick == 48 && f.EndTick == 96);

        doc.Execute(new DeleteFreezeAction(0, 48));
        Assert.DoesNotContain(doc.CurrentTab.Lanes[0].Freezes, f => f.StartTick == 48);

        doc.Undo(); // delete取り消し→復活
        Assert.Contains(doc.CurrentTab.Lanes[0].Freezes, f => f.StartTick == 48 && f.EndTick == 96);
    }

    [Fact]
    public void ResizeFreeze_Clamps_StartLessThanEnd()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        var old = doc.CurrentTab.Lanes[0].Freezes.First(f => f.StartTick == 48);

        // 終点を始点より前にドラッグしようとした場合、start<endにクランプされる
        doc.Execute(new ResizeFreezeAction(0, old, 48, 40));
        var resized = doc.CurrentTab.Lanes[0].Freezes.Single();
        Assert.True(resized.EndTick > resized.StartTick);
        Assert.Equal(48, resized.StartTick);
        Assert.Equal(49, resized.EndTick); // end<=start → start+1にクランプ
    }

    [Fact]
    public void ResizeFreeze_Clamps_NonNegativeStart()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        var old = doc.CurrentTab.Lanes[0].Freezes.First();

        doc.Execute(new ResizeFreezeAction(0, old, -20, 96));
        var resized = doc.CurrentTab.Lanes[0].Freezes.Single();
        Assert.Equal(0, resized.StartTick);
    }

    [Fact]
    public void ResizeFreeze_Undo_RestoresOriginalRange()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        var old = doc.CurrentTab.Lanes[0].Freezes.First();
        doc.Execute(new ResizeFreezeAction(0, old, 48, 200));
        doc.Undo();
        var f = doc.CurrentTab.Lanes[0].Freezes.Single();
        Assert.Equal(48, f.StartTick);
        Assert.Equal(96, f.EndTick);
    }

    [Fact]
    public void PlaceValueEvent_Bpm_AtTick0_Throws()
    {
        var doc = TestFixtures.NewDocument();
        Assert.Throws<InvalidOperationException>(() =>
            doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 0, 140)));
    }

    [Fact]
    public void DeleteValueEvent_Bpm_AtTick0_Throws()
    {
        var doc = TestFixtures.NewDocument();
        Assert.Throws<InvalidOperationException>(() =>
            doc.Execute(new DeleteValueEventAction(ValueEventKind.Bpm, 0)));
    }

    [Fact]
    public void MoveValueEvent_Bpm_InvolvingTick0_Throws()
    {
        var doc = TestFixtures.NewDocument();
        Assert.Throws<InvalidOperationException>(() =>
            doc.Execute(new MoveValueEventAction(ValueEventKind.Bpm, 0, 96)));
    }

    [Fact]
    public void MoveValueEvent_Bpm_NonZeroTick_Works()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96, 150));
        doc.Execute(new MoveValueEventAction(ValueEventKind.Bpm, 96, 192));
        Assert.Contains(doc.Project.BpmEvents, e => e.Tick == 192 && e.Bpm == 150);
        Assert.DoesNotContain(doc.Project.BpmEvents, e => e.Tick == 96);

        doc.Undo();
        Assert.Contains(doc.Project.BpmEvents, e => e.Tick == 96 && e.Bpm == 150);
    }

    [Fact]
    public void MoveObjects_GroupMove_UpdatesModelAndSelection()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(1, 48));
        var targets = new[]
        {
            new ObjectRef(ObjectKind.Note, 0, 48),
            new ObjectRef(ObjectKind.Note, 1, 48),
        };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 1, tickDelta: 48));

        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96L, doc.CurrentTab.Lanes[1].Notes);
        Assert.Contains(96L, doc.CurrentTab.Lanes[2].Notes);

        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 1 && r.Tick == 96);
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 2 && r.Tick == 96);
    }

    [Fact]
    public void MoveObjects_Undo_RestoresExactOriginalPositions_EvenAfterLaneClamp()
    {
        var doc = TestFixtures.NewDocument();
        // レーン4(最終レーン)のノートをlaneDelta=+3でクランプさせる
        doc.Execute(new PlaceNoteAction(4, 48));
        var targets = new[] { new ObjectRef(ObjectKind.Note, 4, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 3, tickDelta: 0));
        // 5keyはレーン0..4なのでクランプされてlane4のまま
        Assert.Contains(48L, doc.CurrentTab.Lanes[4].Notes);

        doc.Undo();
        Assert.Contains(48L, doc.CurrentTab.Lanes[4].Notes);
        Assert.All(doc.CurrentTab.Lanes, l => Assert.DoesNotContain(l.Notes, t => t == 48 && doc.CurrentTab.Lanes.IndexOf(l) != 4));
    }

    [Fact]
    public void MoveObjects_ExcludesTick0Bpm_FromGroupMove()
    {
        var doc = TestFixtures.NewDocument();
        var targets = new[] { new ObjectRef(ObjectKind.Bpm, -1, 0) };
        doc.Execute(new MoveObjectsAction(targets, 0, 48));
        Assert.Contains(doc.Project.BpmEvents, e => e.Tick == 0); // 不変のまま
        Assert.Empty(doc.Selection); // 移動不能=選択にも入らない
    }

    [Fact]
    public void PlaceTimeSignature_ReplacesExistingAtSameMeasure_AndUndoRestores()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceTimeSignatureAction(4, 5, 4));
        Assert.Contains(doc.Project.TimeSignatures, s => s.MeasureIndex == 4 && s.Numerator == 5);

        doc.Execute(new PlaceTimeSignatureAction(4, 3, 4));
        Assert.Single(doc.Project.TimeSignatures.Where(s => s.MeasureIndex == 4));
        Assert.Contains(doc.Project.TimeSignatures, s => s.MeasureIndex == 4 && s.Numerator == 3);

        doc.Undo();
        Assert.Contains(doc.Project.TimeSignatures, s => s.MeasureIndex == 4 && s.Numerator == 5);
    }
}
