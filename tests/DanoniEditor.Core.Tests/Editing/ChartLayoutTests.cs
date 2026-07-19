using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

public class ChartLayoutTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private static ChartLayout NewLayout() => new(TestFixtures.Repository().Get("5"));

    [Fact]
    public void Columns_AreOrdered_MarkerMeasureLanesSpeedBoostBpm()
    {
        var layout = NewLayout();
        var kinds = layout.Columns.Select(c => c.Kind).ToArray();
        Assert.Equal(ColumnKind.Marker, kinds[0]);
        Assert.Equal(ColumnKind.Measure, kinds[1]);
        for (int i = 0; i < 5; i++) Assert.Equal(ColumnKind.Note, kinds[2 + i]);
        Assert.Equal(ColumnKind.Speed, kinds[7]);
        Assert.Equal(ColumnKind.Boost, kinds[8]);
        Assert.Equal(ColumnKind.Bpm, kinds[9]);
    }

    [Fact]
    public void TickToY_And_YToTick_AreInverse()
    {
        var layout = NewLayout();
        layout.PxPerTick = ChartLayout.DefaultPxPerTick;
        double y = layout.TickToY(384 * T);
        Assert.Equal(384 * T, layout.YToTick(y), 6);
    }

    [Fact]
    public void TickToY_Tick0_IsAtTopMargin()
    {
        var layout = NewLayout();
        Assert.Equal(ChartLayout.TopMargin, layout.TickToY(0));
    }

    [Fact]
    public void ColumnAt_ReturnsNoteColumn_ForXInsideNoteLane()
    {
        var layout = NewLayout();
        var noteCol = layout.NoteColumn(2); // "up"
        var found = layout.ColumnAt(noteCol.CenterX);
        Assert.NotNull(found);
        Assert.Equal(ColumnKind.Note, found!.Kind);
        Assert.Equal(2, found.NoteLaneIndex);
    }

    [Fact]
    public void HitTest_PrioritizesFreezeEndpoint_OverNote_OverFreezeBody()
    {
        var layout = NewLayout();
        var project = TestFixtures.NewProject();
        var tab = project.Tabs[0];
        // lane0に長いフリーズ(0..400)、その中間(200)に通常ノートが同居する状況(半径17px=34tick分は十分離す)
        tab.Lanes[0].Freezes.Add(new FreezeNote(0, 400 * T));
        tab.Lanes[0].Notes.Add(200 * T);
        var col = layout.NoteColumn(0);

        // 始点(tick0)ヒット
        var hitStart = layout.HitTest(tab, project, col.CenterX, layout.TickToY(0));
        Assert.Equal(ObjectKind.FreezeStart, hitStart!.Value.Kind);

        // 終点(tick400)ヒット
        var hitEnd = layout.HitTest(tab, project, col.CenterX, layout.TickToY(400 * T));
        Assert.Equal(ObjectKind.FreezeEnd, hitEnd!.Value.Kind);

        // 中間(tick200)ノートヒット(ノート優先)
        var hitNote = layout.HitTest(tab, project, col.CenterX, layout.TickToY(200 * T));
        Assert.Equal(ObjectKind.Note, hitNote!.Value.Kind);

        // ノートのない胴体位置(tick100、両端点からも十分離れている)はFreezeBody
        var hitBody = layout.HitTest(tab, project, col.CenterX, layout.TickToY(100 * T));
        Assert.Equal(ObjectKind.FreezeBody, hitBody!.Value.Kind);
    }

    [Fact]
    public void HitTest_Bpm_FindsEventByTick()
    {
        var layout = NewLayout();
        var project = TestFixtures.NewProject();
        project.BpmEvents.Add(new(192 * T, 180));
        var col = layout.Column(ColumnKind.Bpm);
        var hit = layout.HitTest(project.Tabs[0], project, col.CenterX, layout.TickToY(192 * T));
        Assert.Equal(ObjectKind.Bpm, hit!.Value.Kind);
        Assert.Equal(192 * T, hit.Value.Tick);
    }

    [Fact]
    public void ObjectsInRect_ExcludesTick0Bpm()
    {
        var layout = NewLayout();
        var project = TestFixtures.NewProject();
        project.BpmEvents.Add(new(96 * T, 150));
        var col = layout.Column(ColumnKind.Bpm);
        var refs = layout.ObjectsInRect(project.Tabs[0], project,
            col.X, layout.TickToY(-10 * T), col.X + col.Width, layout.TickToY(300 * T)).ToList();
        Assert.DoesNotContain(refs, r => r.Kind == ObjectKind.Bpm && r.Tick == 0);
        Assert.Contains(refs, r => r.Kind == ObjectKind.Bpm && r.Tick == 96 * T);
    }
}
