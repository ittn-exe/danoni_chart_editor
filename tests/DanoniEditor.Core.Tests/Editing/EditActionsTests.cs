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

    // =====================================================================
    // speed/boost 始点終点オートスムージング出力(2026-07-30要望対応)
    // =====================================================================

    [Fact]
    public void SetValueEventLink_SetsAndUndoes()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 0, 1.0));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 480, 2.0));

        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Speed, 0, 8));
        Assert.Equal(8, doc.CurrentTab.SpeedEvents.First(e => e.Tick == 0).LinkGridDivision);

        doc.Undo();
        Assert.Null(doc.CurrentTab.SpeedEvents.First(e => e.Tick == 0).LinkGridDivision);
    }

    [Fact]
    public void MoveValueEvent_PreservesLinkGridDivision()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 0, 1.0));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 480, 2.0));
        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Speed, 0, 16));

        doc.Execute(new MoveValueEventAction(ValueEventKind.Speed, 0, 24));

        var moved = doc.CurrentTab.SpeedEvents.First(e => e.Tick == 24);
        Assert.Equal(16, moved.LinkGridDivision);

        doc.Undo();
        var restored = doc.CurrentTab.SpeedEvents.First(e => e.Tick == 0);
        Assert.Equal(16, restored.LinkGridDivision);
    }

    [Fact]
    public void DeleteValueEvent_ClearsPredecessorLinkPointingAtIt()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 0, 1.0));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 480, 2.0));
        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Speed, 0, 8));

        doc.Execute(new DeleteValueEventAction(ValueEventKind.Speed, 480));
        Assert.Null(doc.CurrentTab.SpeedEvents.First(e => e.Tick == 0).LinkGridDivision);

        doc.Undo(); // 削除取り消し→リンクも復元されるはず
        Assert.Contains(doc.CurrentTab.SpeedEvents, e => e.Tick == 480);
        Assert.Equal(8, doc.CurrentTab.SpeedEvents.First(e => e.Tick == 0).LinkGridDivision);
    }

    // =====================================================================
    // BPM 始点終点リンク(直線ランプ、2026-08-23要望対応)
    // =====================================================================

    [Fact]
    public void SetValueEventLink_Bpm_SetsAndUndoes()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 480, 200));

        // tick0はTick/Bpm自体は不変条件で変更不可だが、LinkGridDivisionはTickを変更しないため対象にできる。
        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Bpm, 0, 8));
        Assert.Equal(8, doc.Project.BpmEvents.First(e => e.Tick == 0).LinkGridDivision);

        doc.Undo();
        Assert.Null(doc.Project.BpmEvents.First(e => e.Tick == 0).LinkGridDivision);
    }

    [Fact]
    public void MoveValueEvent_Bpm_PreservesLinkGridDivision()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96, 150));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 480, 200));
        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Bpm, 96, 16));

        doc.Execute(new MoveValueEventAction(ValueEventKind.Bpm, 96, 192));

        var moved = doc.Project.BpmEvents.First(e => e.Tick == 192);
        Assert.Equal(16, moved.LinkGridDivision);

        doc.Undo();
        var restored = doc.Project.BpmEvents.First(e => e.Tick == 96);
        Assert.Equal(16, restored.LinkGridDivision);
    }

    [Fact]
    public void DeleteValueEvent_Bpm_ClearsPredecessorLinkPointingAtIt()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 480, 200));
        doc.Execute(new SetValueEventLinkAction(ValueEventKind.Bpm, 0, 8));

        doc.Execute(new DeleteValueEventAction(ValueEventKind.Bpm, 480));
        Assert.Null(doc.Project.BpmEvents.First(e => e.Tick == 0).LinkGridDivision);

        doc.Undo(); // 削除取り消し→リンクも復元されるはず
        Assert.Contains(doc.Project.BpmEvents, e => e.Tick == 480);
        Assert.Equal(8, doc.Project.BpmEvents.First(e => e.Tick == 0).LinkGridDivision);
    }

    [Fact]
    public void PlaceValueEvent_Bpm_PreservesLinkGridDivisionArgument()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96, 150, linkGridDivision: 4));
        Assert.Equal(4, doc.Project.BpmEvents.First(e => e.Tick == 96).LinkGridDivision);
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

    // =====================================================================
    // 2026-08-08不具合修正(第三者報告): 移動先/複製先/貼り付け先に既存ノートがある場合、
    // 重ねて置かず「その部分だけパス」する(複製・貼り付けは見送り、移動はパスしたノートを
    // 削除する)。
    // 2026-08-08追加対応(ユーザー確定仕様): 移動がパスされたノートは元の位置へ戻さず削除し、
    // 「移動した」という結果を直感的に受け取れるようにする。移動後の選択対象は
    // 「移動が成功したノート」+「パスする原因になった既存ノート」とする。
    // =====================================================================

    [Fact]
    public void MoveObjects_Note_SkipsWhenDestinationOccupied_RemovesBlockedNote_NoDuplicate()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96)); // 移動先に既存ノート
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        // 重ねて置かれず、パスされたノート(元は48)は削除される。既存ノート(96)は重複せず1件のまま。
        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Single(doc.CurrentTab.Lanes[0].Notes); // 96のみ(48は消え、重複もしていない)
        Assert.Equal(96L, doc.CurrentTab.Lanes[0].Notes.Single());
    }

    [Fact]
    public void MoveObjects_Note_SkippedMove_SelectsBlockingExistingNote_NotTheRemovedOne()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96)); // 移動先に既存ノート(=パスする原因)
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        // パスされて削除された側(48)は選択に残らない。パスする原因になった既存ノート(96)が選択される。
        Assert.DoesNotContain(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 48);
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 96);
    }

    [Fact]
    public void MoveObjects_Note_SkippedMove_UndoRestoresOriginalState_AndSelection()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96));
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 48));
        doc.Undo();

        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Equal(2, doc.CurrentTab.Lanes[0].Notes.Count);
        // Undo後の選択はDo()前の元の対象(48)に戻る
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 48);
    }

    [Fact]
    public void MoveObjects_Note_SkippedMove_RestoresColorAndAnnotation_OnUndo()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.CurrentTab.Lanes[0].ColorOverrides.Add(new NColorEntry(48, "#ff0000", null));
        doc.CurrentTab.Lanes[0].Annotations.Add(new NoteAnnotation(48, "メモ", false));
        doc.Execute(new PlaceNoteAction(0, 96)); // 移動先に既存ノート
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        // パスされたノートの削除と一緒に、付随データ(色・コメント)も消える(幽霊化を防ぐ)
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Empty(doc.CurrentTab.Lanes[0].Annotations);

        doc.Undo();
        var color = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal(48, color.Tick);
        Assert.Equal("#ff0000", color.Color);
        var annotation = Assert.Single(doc.CurrentTab.Lanes[0].Annotations);
        Assert.Equal(48, annotation.Tick);
        Assert.Equal("メモ", annotation.Comment);
    }

    [Fact]
    public void MoveObjects_Note_ChainShiftWithinSameSelection_DoesNotFalselyCollide()
    {
        // 連続する3音(48,96,144)をまとめて選択し、+48ずらす(96,144,192)。
        // 行き先が「同じ選択内の他ノートの元位置」であるだけなので衝突扱いにしてはいけない。
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96));
        doc.Execute(new PlaceNoteAction(0, 144));
        var targets = new[]
        {
            new ObjectRef(ObjectKind.Note, 0, 48),
            new ObjectRef(ObjectKind.Note, 0, 96),
            new ObjectRef(ObjectKind.Note, 0, 144),
        };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(144L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(192L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Equal(3, doc.CurrentTab.Lanes[0].Notes.Count);
    }

    [Fact]
    public void CopyObjects_Note_SkipsWhenDestinationOccupied_OriginalUntouched()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96)); // 複製先に既存ノート
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new CopyObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes); // 複製元はそのまま残る(Copyの性質)
        Assert.Single(doc.CurrentTab.Lanes[0].Notes, t => t == 96); // 重ねて複製されていない
        Assert.Equal(2, doc.CurrentTab.Lanes[0].Notes.Count);
    }

    [Fact]
    public void CopyObjects_Note_SkipsWhenDestinationOccupied_SelectsBlockingExistingNote()
    {
        // 2026-08-08追加対応: 複製がパスされた場合、その原因になった既存ノート(96)が選択される
        // (Move/Pasteと同じ考え方、なぜ複製されなかったかが一目で分かるようにする)。
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(0, 96)); // 複製先に既存ノート
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new CopyObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 96);
    }

    [Fact]
    public void CopyObjects_Note_DestinationFree_CreatesDuplicate()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new CopyObjectsAction(targets, laneDelta: 0, tickDelta: 48));

        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Equal(2, doc.CurrentTab.Lanes[0].Notes.Count);
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 96); // 複製先が選択される
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

    // =====================================================================
    // 色編集モード(ncolor_data、2026-07-23)
    // =====================================================================

    [Fact]
    public void SetNoteColorAction_Note_SetsColorOnly_UndoRemoves()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new SetNoteColorAction(0, 48, "#ff0000", setColor: true, setBand: false));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
        Assert.Null(entry.BandColor);

        doc.Undo();
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
    }

    [Fact]
    public void SetNoteColorAction_Freeze_ColorAndBandAreIndependent()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#111111", setColor: true, setBand: false));
        doc.Execute(new SetNoteColorAction(0, 48, "#222222", setColor: false, setBand: true));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#111111", entry.Color);
        Assert.Equal("#222222", entry.BandColor);

        doc.Undo(); // 帯の設定だけ取り消し
        var afterUndo = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#111111", afterUndo.Color);
        Assert.Null(afterUndo.BandColor);
    }

    [Fact]
    public void ResetNoteColorAction_ClearsOnlySpecifiedPart_KeepsOther()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#111111", setColor: true, setBand: true));

        doc.Execute(new ResetNoteColorAction(0, 48, resetColor: true, resetBand: false));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Null(entry.Color);
        Assert.Equal("#111111", entry.BandColor);
    }

    [Fact]
    public void ResetNoteColorAction_BothPartsCleared_RemovesEntryEntirely()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#111111", setColor: true, setBand: true));

        doc.Execute(new ResetNoteColorAction(0, 48, resetColor: true, resetBand: true));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);

        doc.Undo();
        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#111111", entry.Color);
        Assert.Equal("#111111", entry.BandColor);
    }

    [Fact]
    public void ClearAllNoteColorsAction_ClearsEveryLane_UndoRestoresAll()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new PlaceNoteAction(1, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#ff0000", setColor: true, setBand: false));
        doc.Execute(new SetNoteColorAction(1, 96, "#00ff00", setColor: true, setBand: false));

        doc.Execute(new ClearAllNoteColorsAction());
        Assert.All(doc.CurrentTab.Lanes, l => Assert.Empty(l.ColorOverrides));

        doc.Undo();
        Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Single(doc.CurrentTab.Lanes[1].ColorOverrides);
    }

    [Fact]
    public void DeleteNoteAction_RemovesAssociatedColor_UndoRestoresBoth()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new SetNoteColorAction(0, 48, "#ff0000", setColor: true, setBand: false));

        doc.Execute(new DeleteNoteAction(0, 48));
        Assert.DoesNotContain(48L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);

        doc.Undo();
        Assert.Contains(48L, doc.CurrentTab.Lanes[0].Notes);
        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
    }

    [Fact]
    public void DeleteFreezeAction_RemovesAssociatedColor_UndoRestoresBoth()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#111111", setColor: true, setBand: true));

        doc.Execute(new DeleteFreezeAction(0, 48));
        Assert.Empty(doc.CurrentTab.Lanes[0].Freezes);
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);

        doc.Undo();
        Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#111111", entry.Color);
        Assert.Equal("#111111", entry.BandColor);
    }

    [Fact]
    public void MoveObjects_Note_CarriesColorToNewPosition()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48));
        doc.Execute(new SetNoteColorAction(0, 48, "#ff0000", setColor: true, setBand: false));
        var targets = new[] { new ObjectRef(ObjectKind.Note, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 1, tickDelta: 48));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides); // 元の位置には残らない
        var entry = Assert.Single(doc.CurrentTab.Lanes[1].ColorOverrides);
        Assert.Equal(96, entry.Tick);
        Assert.Equal("#ff0000", entry.Color);

        doc.Undo();
        Assert.Empty(doc.CurrentTab.Lanes[1].ColorOverrides);
        var back = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal(48, back.Tick);
        Assert.Equal("#ff0000", back.Color);
    }

    [Fact]
    public void MoveObjects_Freeze_CarriesColorToNewStartTick()
    {
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Execute(new SetNoteColorAction(0, 48, "#123456", setColor: true, setBand: true));
        var targets = new[] { new ObjectRef(ObjectKind.FreezeStart, 0, 48) };

        doc.Execute(new MoveObjectsAction(targets, laneDelta: 0, tickDelta: 100));

        var moved = doc.CurrentTab.Lanes[0].Freezes.Single();
        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal(moved.StartTick, entry.Tick);
        Assert.Equal("#123456", entry.Color);
        Assert.Equal("#123456", entry.BandColor);
    }
}
