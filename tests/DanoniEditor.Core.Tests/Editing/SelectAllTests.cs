using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>全選択(仕様書13章TBD: Ctrl+A/Shift+Ctrl+A、2026-07-21)のテスト</summary>
public class SelectAllTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35)

    private static (EditorDocument Doc, SmartToolController Ctrl) NewScene(string keyTypeId = "5")
    {
        var doc = TestFixtures.NewDocument(keyTypeId: keyTypeId);
        return (doc, new SmartToolController(doc));
    }

    // --- Ctrl+A(SelectAllNotes): 対象固定(ノート・フリーズのみ) ---

    [Fact]
    public void SelectAllNotes_EmptyChart_ReturnsFalse()
    {
        var (_, ctrl) = NewScene();
        Assert.False(ctrl.SelectAllNotes());
    }

    [Fact]
    public void SelectAllNotes_SelectsNotesAndFreezes_AcrossLanes_ButNotOtherKinds()
    {
        var (doc, ctrl) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        doc.Execute(new PlaceFreezeAction(2, 100 * T, 200 * T));
        doc.CurrentTab.SpeedEvents.Add(new ValueEvent(48 * T, 1.5)); // 対象外(Ctrl+Aは固定でノート/フリーズのみ)
        doc.CurrentTab.Lanes[3].Notes.Add(300 * T); // Undo管理外の直接追加でも拾えることを確認

        Assert.True(ctrl.SelectAllNotes());
        Assert.Equal(4, doc.Selection.Count);
        Assert.Contains(new ObjectRef(ObjectKind.Note, 0, 48 * T), doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.Note, 1, 96 * T), doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.FreezeStart, 2, 100 * T), doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.Note, 3, 300 * T), doc.Selection);
        Assert.DoesNotContain(doc.Selection, r => r.Kind == ObjectKind.Speed);
    }

    [Fact]
    public void SelectAllNotes_ReplacesPreviousSelection()
    {
        var (doc, ctrl) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.CurrentTab.SpeedEvents.Add(new ValueEvent(10 * T, 2.0));
        doc.Selection.Add(new ObjectRef(ObjectKind.Speed, -1, 10 * T)); // 事前の無関係な選択

        Assert.True(ctrl.SelectAllNotes());
        Assert.Single(doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.Note, 0, 48 * T), doc.Selection);
    }

    // --- Shift+Ctrl+A(SelectAllTargets): 環境設定で選べる対象 ---

    [Fact]
    public void SelectAllTargets_AllOff_ReturnsFalse()
    {
        var (doc, ctrl) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        var options = new SelectAllOptions(false, false, false, false, false, false, false);
        Assert.False(ctrl.SelectAllTargets(options));
    }

    [Fact]
    public void SelectAllTargets_OnlySpeed_SelectsOnlySpeedEvents()
    {
        var (doc, ctrl) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.CurrentTab.SpeedEvents.Add(new ValueEvent(10 * T, 2.0));
        doc.CurrentTab.BoostEvents.Add(new ValueEvent(20 * T, 1.5));
        var options = new SelectAllOptions(Note: false, Freeze: false, Speed: true, Boost: false, Bpm: false, TimeSignature: false, Marker: false);

        Assert.True(ctrl.SelectAllTargets(options));
        Assert.Single(doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.Speed, -1, 10 * T), doc.Selection);
    }

    [Fact]
    public void SelectAllTargets_Bpm_ExcludesTick0()
    {
        var (doc, ctrl) = NewScene();
        // doc.Project.BpmEvents already has the mandatory tick-0 entry from TestFixtures/model default
        doc.Project.BpmEvents.Add(new BpmEvent(240 * T, 180));
        var options = new SelectAllOptions(false, false, false, false, Bpm: true, false, false);

        Assert.True(ctrl.SelectAllTargets(options));
        Assert.Single(doc.Selection);
        Assert.Contains(new ObjectRef(ObjectKind.Bpm, -1, 240 * T), doc.Selection);
        Assert.DoesNotContain(new ObjectRef(ObjectKind.Bpm, -1, 0), doc.Selection);
    }

    [Fact]
    public void SelectAllTargets_TimeSignature_UsesMeasureIndexAsTick()
    {
        var (doc, ctrl) = NewScene();
        doc.Project.TimeSignatures.Add(new TimeSignatureEvent(4, 3, 4));
        var options = new SelectAllOptions(false, false, false, false, false, TimeSignature: true, Marker: false);

        Assert.True(ctrl.SelectAllTargets(options));
        Assert.Contains(new ObjectRef(ObjectKind.TimeSignature, -1, 4), doc.Selection);
    }

    [Fact]
    public void SelectAllTargets_Marker_Selected()
    {
        var (doc, ctrl) = NewScene();
        doc.Project.Markers.Add(new Marker(50 * T, "テスト"));
        var options = new SelectAllOptions(false, false, false, false, false, false, Marker: true);

        Assert.True(ctrl.SelectAllTargets(options));
        Assert.Contains(new ObjectRef(ObjectKind.Marker, -1, 50 * T), doc.Selection);
    }

    [Fact]
    public void SelectAllTargets_AllOn_CombinesEverything()
    {
        var (doc, ctrl) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceFreezeAction(1, 100 * T, 200 * T));
        doc.CurrentTab.SpeedEvents.Add(new ValueEvent(10 * T, 2.0));
        doc.CurrentTab.BoostEvents.Add(new ValueEvent(20 * T, 1.5));
        doc.Project.BpmEvents.Add(new BpmEvent(240 * T, 180));
        doc.Project.TimeSignatures.Add(new TimeSignatureEvent(4, 3, 4));
        doc.Project.Markers.Add(new Marker(50 * T, "テスト"));
        var options = new SelectAllOptions(true, true, true, true, true, true, true);

        Assert.True(ctrl.SelectAllTargets(options));
        // note+freeze+speed+boost+bpm(tick0除外)+timesig(既定の0小節目4/4+追加した4小節目3/4の2件)+marker
        Assert.Equal(8, doc.Selection.Count);
    }
}
