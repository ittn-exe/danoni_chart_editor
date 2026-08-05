using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>コピーマネージャー(異なるキー種間のコピー&ペースト、2026-07-31)の衝突解決ロジック
/// (PasteLaneState)単体テスト。TestData/EditingTemplate/temp_5.jsonのSixteenth基準で検証する。</summary>
public class PasteLaneStateTests
{
    private static readonly long Sixteenth = PasteLaneState.Sixteenth;
    private static readonly PasteConflictOptions Default = PasteConflictOptions.Default;

    [Fact]
    public void TryPlaceNote_DuplicateTick_ReturnsFalse()
    {
        var state = new PasteLaneState([100], []);
        Assert.False(state.TryPlaceNote(100, Default));
        Assert.Single(state.Notes);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeHead_NotePriority_DiscardsFreeze()
    {
        var state = new PasteLaneState([], [new FreezeNote(100, 500)]);
        var opt = Default with { NoteVsFreezeHeadMode = "note" };
        Assert.True(state.TryPlaceNote(100, opt));
        Assert.Contains(100L, state.Notes);
        Assert.Empty(state.Freezes);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeHead_FreezePriority_DiscardsNote()
    {
        var state = new PasteLaneState([], [new FreezeNote(100, 500)]);
        var opt = Default with { NoteVsFreezeHeadMode = "freeze" };
        Assert.False(state.TryPlaceNote(100, opt));
        Assert.DoesNotContain(100L, state.Notes);
        Assert.Single(state.Freezes);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeBody_Trim_ShortensFreezeAndPlacesNote()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { NoteVsFreezeBodyMode = "trim" };
        Assert.True(state.TryPlaceNote(500, opt));
        Assert.Contains(500L, state.Notes);
        var f = Assert.Single(state.Freezes);
        Assert.Equal(0, f.StartTick);
        Assert.Equal(500 - Sixteenth, f.EndTick);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeBody_IgnoreNote_KeepsFreezeIntact()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { NoteVsFreezeBodyMode = "ignoreNote" };
        Assert.False(state.TryPlaceNote(500, opt));
        Assert.DoesNotContain(500L, state.Notes);
        var f = Assert.Single(state.Freezes);
        Assert.Equal(0, f.StartTick);
        Assert.Equal(1000, f.EndTick);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeTail_Trim_ShortensFreeze()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { NoteVsFreezeTailMode = "trim" };
        Assert.True(state.TryPlaceNote(1000, opt));
        Assert.Contains(1000L, state.Notes);
        var f = Assert.Single(state.Freezes);
        Assert.Equal(1000 - Sixteenth, f.EndTick);
    }

    [Fact]
    public void TryPlaceNote_VsFreezeTail_IgnoreNote_KeepsFreezeIntact()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { NoteVsFreezeTailMode = "ignoreNote" };
        Assert.False(state.TryPlaceNote(1000, opt));
        var f = Assert.Single(state.Freezes);
        Assert.Equal(1000, f.EndTick);
    }

    [Fact]
    public void TryPlaceFreeze_HeadVsHead_LongWins_NewShortOneLoses()
    {
        var state = new PasteLaneState([], [new FreezeNote(100, 900)]); // 長さ800
        var opt = Default with { FreezeHeadVsHeadMode = "long" };
        Assert.False(state.TryPlaceFreeze(100, 300, opt)); // 新規は長さ200(短い)ので負ける
        var f = Assert.Single(state.Freezes);
        Assert.Equal(900, f.EndTick);
    }

    [Fact]
    public void TryPlaceFreeze_HeadVsHead_LongWins_NewLongOneReplacesExisting()
    {
        var state = new PasteLaneState([], [new FreezeNote(100, 300)]); // 長さ200
        var opt = Default with { FreezeHeadVsHeadMode = "long" };
        Assert.True(state.TryPlaceFreeze(100, 900, opt)); // 新規は長さ800(長い)
        var f = Assert.Single(state.Freezes);
        Assert.Equal(900, f.EndTick);
    }

    [Fact]
    public void TryPlaceFreeze_HeadVsBody_Trim_ShortensFrontFreezeAndPlacesNew()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { FreezeHeadVsBodyMode = "trim" };
        Assert.True(state.TryPlaceFreeze(500, 700, opt));
        Assert.Equal(2, state.Freezes.Count);
        Assert.Contains(state.Freezes, f => f.StartTick == 0 && f.EndTick == 500 - Sixteenth);
        Assert.Contains(state.Freezes, f => f.StartTick == 500 && f.EndTick == 700);
    }

    [Fact]
    public void TryPlaceFreeze_HeadVsBody_IgnoreFreeze_DiscardsNewOne()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { FreezeHeadVsBodyMode = "ignoreFreeze" };
        Assert.False(state.TryPlaceFreeze(500, 700, opt));
        var f = Assert.Single(state.Freezes);
        Assert.Equal(1000, f.EndTick);
    }

    [Fact]
    public void TryPlaceFreeze_NewRangeContainsExistingNote_Trim_ShortensNewFreeze()
    {
        var state = new PasteLaneState([500], []);
        var opt = Default with { NoteVsFreezeBodyMode = "trim" };
        Assert.True(state.TryPlaceFreeze(0, 1000, opt));
        var f = Assert.Single(state.Freezes);
        Assert.Equal(0, f.StartTick);
        Assert.Equal(500 - Sixteenth, f.EndTick);
        Assert.Contains(500L, state.Notes); // 既存ノートはそのまま残る
    }

    [Fact]
    public void TryPlaceFreeze_NewRangeContainsExistingNote_IgnoreNote_RemovesExistingNote()
    {
        var state = new PasteLaneState([500], []);
        var opt = Default with { NoteVsFreezeBodyMode = "ignoreNote" };
        Assert.True(state.TryPlaceFreeze(0, 1000, opt));
        var f = Assert.Single(state.Freezes);
        Assert.Equal(0, f.StartTick);
        Assert.Equal(1000, f.EndTick);
        Assert.DoesNotContain(500L, state.Notes);
    }

    [Fact]
    public void Operations_RecordsRemovalBeforeAddition_ForHeadVsHeadReplace()
    {
        var state = new PasteLaneState([], [new FreezeNote(100, 300)]);
        var opt = Default with { FreezeHeadVsHeadMode = "long" };
        Assert.True(state.TryPlaceFreeze(100, 900, opt));
        Assert.Equal(2, state.Operations.Count);
        Assert.IsType<RemoveExistingFreezeOp>(state.Operations[0]);
        Assert.IsType<AddFreezeOp>(state.Operations[1]);
    }

    [Fact]
    public void Operations_RecordsTrim_ForBodyOverlap()
    {
        var state = new PasteLaneState([], [new FreezeNote(0, 1000)]);
        var opt = Default with { NoteVsFreezeBodyMode = "trim" };
        Assert.True(state.TryPlaceNote(500, opt));
        Assert.Equal(2, state.Operations.Count);
        Assert.IsType<TrimExistingFreezeOp>(state.Operations[0]);
        Assert.IsType<AddNoteOp>(state.Operations[1]);
    }
}

/// <summary>SmartToolControllerのコピーマネージャー関連API(ClipboardNeedsLaneMapping・
/// ClipboardSourceLanesWithObjects・PasteWithLaneMapping)の統合テスト(2026-07-31)。
/// 5key(タブ0)と23key(タブ1)を同一プロジェクトに持たせ、キー種の異なるタブ間コピペを再現する。
/// 2026-08-06: staticなEditorClipboardを共有するClipboardTestsとの並列実行による競合を避けるため
/// "EditorClipboard"コレクションに所属させる(EditorClipboardCollection.cs参照)。</summary>
[Collection("EditorClipboard")]
public class PasteWithLaneMappingTests
{
    public PasteWithLaneMappingTests() => EditorClipboard.Clear();

    private static (EditorDocument Doc, SmartToolController Ctrl) NewTwoKeyTypeScene()
    {
        var doc = TestFixtures.NewDocument(keyTypeId: "5"); // タブ0: 5key
        var repo = TestFixtures.Repository();
        doc.Project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("23"), "Extra")); // タブ1: 23key
        return (doc, new SmartToolController(doc));
    }

    [Fact]
    public void ClipboardNeedsLaneMapping_SameKeyType_ReturnsFalse()
    {
        var doc = TestFixtures.NewDocument(keyTypeId: "5");
        var ctrl = new SmartToolController(doc);
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 480));
        Assert.True(ctrl.CopySelection());

        Assert.False(ctrl.ClipboardNeedsLaneMapping());
    }

    [Fact]
    public void ClipboardNeedsLaneMapping_DifferentKeyType_WithNote_ReturnsTrue()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        Assert.True(ctrl.ClipboardNeedsLaneMapping());
        Assert.Equal(new[] { 0 }, ctrl.ClipboardSourceLanesWithObjects());
    }

    [Fact]
    public void ClipboardNeedsLaneMapping_DifferentKeyType_OnlyNonLaneEntries_ReturnsFalse()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceMarkerAction(480, "test"));
        doc.Selection.Add(new ObjectRef(ObjectKind.Marker, -1, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        // キー種は違うが、コピー内容にノート・フリーズが無いためコピーマネージャーは不要
        Assert.False(ctrl.ClipboardNeedsLaneMapping());
    }

    [Fact]
    public void PasteWithLaneMapping_PlacesNoteOnlyOnMappedDestLane()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        var mapping = new List<(int SourceLane, int DestLane)> { (0, 5) };
        Assert.True(ctrl.PasteWithLaneMapping(mapping, PasteConflictOptions.Default, preserveProperties: true));

        // PlaybackStartFrame未設定のためtick0基準で貼り付けられる(既存Pasteの挙動と同じ)
        Assert.Contains(0L, doc.CurrentTab.Lanes[5].Notes);
        Assert.Empty(doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void PasteWithLaneMapping_OneSourceToMultipleDest_DuplicatesNote()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(2, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 2, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        var mapping = new List<(int SourceLane, int DestLane)> { (2, 3), (2, 7) };
        Assert.True(ctrl.PasteWithLaneMapping(mapping, PasteConflictOptions.Default, preserveProperties: true));

        Assert.Contains(0L, doc.CurrentTab.Lanes[3].Notes);
        Assert.Contains(0L, doc.CurrentTab.Lanes[7].Notes);
    }

    [Fact]
    public void PasteWithLaneMapping_MultipleSourcesToSameDest_SameTickIsSkippedAfterFirst()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Execute(new PlaceNoteAction(1, 480)); // 同じtick、別レーン
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        var mapping = new List<(int SourceLane, int DestLane)> { (0, 5), (1, 5) }; // 両方とも貼り付け先レーン5へ
        Assert.True(ctrl.PasteWithLaneMapping(mapping, PasteConflictOptions.Default, preserveProperties: true));

        Assert.Single(doc.CurrentTab.Lanes[5].Notes); // 2件目以降はパスされる(同一レーン同一frame不可)
    }

    [Fact]
    public void PasteWithLaneMapping_UnmappedSourceLane_IsNotPasted()
    {
        var (doc, ctrl) = NewTwoKeyTypeScene();

        doc.CurrentTabIndex = 0;
        doc.Execute(new PlaceNoteAction(0, 480));
        doc.Execute(new PlaceNoteAction(1, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 480));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 480));
        Assert.True(ctrl.CopySelection());

        doc.CurrentTabIndex = 1;
        var mapping = new List<(int SourceLane, int DestLane)> { (0, 5) }; // レーン1は対応表に含めない
        Assert.True(ctrl.PasteWithLaneMapping(mapping, PasteConflictOptions.Default, preserveProperties: true));

        Assert.Contains(0L, doc.CurrentTab.Lanes[5].Notes);
        for (int i = 0; i < doc.CurrentTemplate.KeyCount; i++)
            if (i != 5) Assert.Empty(doc.CurrentTab.Lanes[i].Notes);
    }
}
