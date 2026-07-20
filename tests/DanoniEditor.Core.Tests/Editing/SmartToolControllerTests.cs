using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

public class SmartToolControllerTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private static (EditorDocument Doc, SmartToolController Ctrl, ChartLayout Layout) NewScene()
    {
        var doc = TestFixtures.NewDocument();
        return (doc, new SmartToolController(doc), doc.CurrentLayout);
    }

    private static PointerPos At(ColumnInfo col, ChartLayout layout, long tick) => new(col.CenterX, layout.TickToY(tick));

    private static void Click(SmartToolController ctrl, PointerPos pos, PointerModifiers mods = PointerModifiers.None)
    {
        ctrl.BeginLeft(pos, mods);
        ctrl.End(pos);
    }

    private static void RightClick(SmartToolController ctrl, PointerPos pos)
    {
        ctrl.BeginRight(pos, PointerModifiers.None);
        ctrl.End(pos);
    }

    private static void DragLeft(SmartToolController ctrl, PointerPos from, PointerPos to, PointerModifiers mods = PointerModifiers.None)
    {
        ctrl.BeginLeft(from, mods);
        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            ctrl.Move(new PointerPos(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
        }
        ctrl.End(to);
    }

    private static void DragRight(SmartToolController ctrl, PointerPos from, PointerPos to)
    {
        ctrl.BeginRight(from, PointerModifiers.None);
        // 実際のマウス移動は多数のMoveイベントに分割される想定なので、テストでも細かく補間してパスをなぞる
        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            ctrl.Move(new PointerPos(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
        }
        ctrl.End(to);
    }

    // --- 6.3.1: click empty=place ---

    [Fact]
    public void ClickEmptyNoteLane_PlacesNote()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.NoteColumn(0);
        Click(ctrl, At(col, layout, 96 * T));
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    // --- 6.3.1: Shift+click=freeze place (note lanes only) ---

    [Fact]
    public void ShiftClickEmptyNoteLane_PlacesFreeze_WithSnapLengthDefault()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Snap.Division = 16; // GridTicks = 12
        var col = layout.NoteColumn(0);
        Click(ctrl, At(col, layout, 96 * T), PointerModifiers.Shift);
        var f = Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        Assert.Equal(96 * T, f.StartTick);
        Assert.Equal(96 * T + doc.Snap.GridTicks, f.EndTick);
    }

    // --- 6.3.1: click obj=select ---

    [Fact]
    public void ClickExistingNote_Selects()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        var col = layout.NoteColumn(0);
        Click(ctrl, At(col, layout, 48 * T));
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 48 * T);
    }

    // --- 6.3.1: right-click obj=delete ---

    [Fact]
    public void RightClickNote_Deletes()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        var col = layout.NoteColumn(0);
        RightClick(ctrl, At(col, layout, 48 * T));
        Assert.DoesNotContain(48 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.True(doc.UndoStack.CanUndo);
    }

    // --- 6.3.1: drag freeze start/end=resize (within lane) ---

    [Fact]
    public void DragFreezeStart_Resizes()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0 * T, 96 * T));
        var col = layout.NoteColumn(0);

        DragLeft(ctrl, At(col, layout, 0 * T), At(col, layout, 48 * T));

        var f = Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        Assert.Equal(48 * T, f.StartTick);
        Assert.Equal(96 * T, f.EndTick);
    }

    [Fact]
    public void DragFreezeEnd_Resizes()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0 * T, 96 * T));
        var col = layout.NoteColumn(0);

        DragLeft(ctrl, At(col, layout, 96 * T), At(col, layout, 192 * T));

        var f = Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        Assert.Equal(0 * T, f.StartTick);
        Assert.Equal(192 * T, f.EndTick);
    }

    // --- 6.3.1: drag obj=move ---

    [Fact]
    public void DragNote_MovesToNewTickAndLane()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        var from = At(layout.NoteColumn(0), layout, 48 * T);
        var to = At(layout.NoteColumn(1), layout, 96 * T);

        DragLeft(ctrl, from, to);

        Assert.DoesNotContain(48 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[1].Notes);
    }

    // --- 6.3.1: right-drag from obj=drag-delete mode ---

    [Fact]
    public void RightDragFromNote_DeletesAllTouched_AsOneUndoAction()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.NoteColumn(0);
        doc.Execute(new PlaceNoteAction(0, 0 * T));
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(0, 96 * T));
        int undoDepthBefore = doc.UndoStack.UndoDepth;

        DragRight(ctrl, At(col, layout, 0 * T), At(col, layout, 96 * T));

        Assert.Empty(doc.CurrentTab.Lanes[0].Notes);
        Assert.Equal(undoDepthBefore + 1, doc.UndoStack.UndoDepth); // 1ジェスチャ=1Undoアクション

        doc.Undo();
        Assert.Equal(3, doc.CurrentTab.Lanes[0].Notes.Count);
    }

    // --- 6.3.1: right-drag from empty=rect select ---

    [Fact]
    public void RightDragFromEmpty_RectSelectsObjectsInRange()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        doc.Execute(new PlaceNoteAction(2, 500 * T)); // 範囲外にしたい

        var col0 = layout.NoteColumn(0);
        var col2 = layout.NoteColumn(2);
        var from = new PointerPos(col0.X, layout.TickToY(0 * T));
        var to = new PointerPos(col2.X, layout.TickToY(150 * T));

        DragRight(ctrl, from, to);

        Assert.Contains(doc.Selection, r => r.Lane == 0 && r.Tick == 48 * T);
        Assert.Contains(doc.Selection, r => r.Lane == 1 && r.Tick == 96 * T);
        Assert.DoesNotContain(doc.Selection, r => r.Lane == 2 && r.Tick == 500 * T);
    }

    // --- 6.3.2: selected-set drag = group move (regardless of smart tool on/off) ---

    [Fact]
    public void DraggingSelectedMember_MovesEntireSelection()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 48 * T));

        var from = At(layout.NoteColumn(0), layout, 48 * T);
        var to = At(layout.NoteColumn(0), layout, 96 * T);
        DragLeft(ctrl, from, to);

        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[1].Notes);
    }

    [Fact]
    public void SmartToolOff_GroupMoveStillWorks_ButClickPlaceDoesNot()
    {
        var (doc, ctrl, layout) = NewScene();
        ctrl.SmartToolEnabled = false;
        var col = layout.NoteColumn(0);

        Click(ctrl, At(col, layout, 48 * T));
        Assert.Empty(doc.CurrentTab.Lanes[0].Notes); // OFF時はクリック配置不可

        // 選択中オブジェクトのグループ移動はOFFでも動く
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        DragLeft(ctrl, At(col, layout, 48 * T), At(col, layout, 96 * T));
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    // --- 7.4: マーカーレーン特殊ルール ---

    [Fact]
    public void ClickEmptyMarkerLane_SetsCurrentTick_WithoutPlacingMarker()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Marker);
        Click(ctrl, At(col, layout, 96 * T));
        Assert.Equal(96 * T, ctrl.CurrentTick);
        Assert.Empty(doc.Project.Markers);
    }

    [Fact]
    public void ShiftClickEmptyMarkerLane_PlacesMarker()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Marker);
        Click(ctrl, At(col, layout, 96 * T), PointerModifiers.Shift);
        Assert.Contains(doc.Project.Markers, m => m.Tick == 96 * T);
    }

    [Fact]
    public void ClickExistingMarker_Selects()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceMarkerAction(96 * T));
        var col = layout.Column(ColumnKind.Marker);
        Click(ctrl, At(col, layout, 96 * T));
        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Marker && r.Tick == 96 * T);
    }

    [Fact]
    public void LeftDragMarker_Moves()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceMarkerAction(48 * T));
        var col = layout.Column(ColumnKind.Marker);
        DragLeft(ctrl, At(col, layout, 48 * T), At(col, layout, 192 * T));
        Assert.Contains(doc.Project.Markers, m => m.Tick == 192 * T);
        Assert.DoesNotContain(doc.Project.Markers, m => m.Tick == 48 * T);
    }

    // --- 7.5: 拍子は物理小節頭にのみ配置(measure-head clamp) ---

    [Fact]
    public void ClickMeasureColumn_NotExactlyOnHead_ClampsToNearestMeasureHead()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Measure);
        // 4/4=192tick/小節。tick=250は小節1(head=192)寄り(距離58)であって小節2(head=384、距離134)ではない
        Click(ctrl, At(col, layout, 250 * T));
        Assert.Contains(doc.Project.TimeSignatures, s => s.MeasureIndex == 1);
        Assert.DoesNotContain(doc.Project.TimeSignatures, s => s.MeasureIndex == 2);
    }

    // --- 2026-07-17e: 空セル左クリックは押下(Begin)の瞬間に確定する(クリック応答改善) ---

    [Fact]
    public void PressEmptyNoteLane_PlacesNoteImmediately_BeforeRelease()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.NoteColumn(0);
        ctrl.BeginLeft(At(col, layout, 96 * T), PointerModifiers.None);
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes); // ボタンを離す前に配置済み
        ctrl.End(At(col, layout, 96 * T));
        Assert.Single(doc.CurrentTab.Lanes[0].Notes); // Endで二重配置されない
    }

    [Fact]
    public void PressEmptyNoteLane_ThenDrag_KeepsSinglePlacedNote()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.NoteColumn(0);
        int undoBefore = doc.UndoStack.UndoDepth;

        DragLeft(ctrl, At(col, layout, 96 * T), At(col, layout, 192 * T));

        Assert.Single(doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes); // 押下地点に配置されたまま(ドラッグで消えたり動いたりしない)
        Assert.Equal(undoBefore + 1, doc.UndoStack.UndoDepth); // 配置1回ぶんだけ
    }

    [Fact]
    public void PressEmptyMarkerLane_SetsCurrentTickImmediately_BeforeRelease()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Marker);
        ctrl.BeginLeft(At(col, layout, 96 * T), PointerModifiers.None);
        Assert.Equal(96 * T, ctrl.CurrentTick); // 離す前に設定済み
        ctrl.End(At(col, layout, 96 * T));
        Assert.Empty(doc.Project.Markers);
    }

    // --- 2026-07-17f: マーカーレーンWクリック=再生開始フレーム設定 / Delete=選択削除 ---

    [Fact]
    public void DoubleLeftOnMarkerLane_SetsPlaybackStartFrame()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Marker);
        Assert.True(ctrl.DoubleLeft(At(col, layout, 96 * T)));
        // BPM120: 1拍=30frame、tick96=2拍 → 60frame
        Assert.NotNull(doc.Project.PlaybackStartFrame);
        Assert.Equal(60.0, doc.Project.PlaybackStartFrame!.Value, 3);
    }

    [Fact]
    public void DoubleLeftOnNoteLane_NotHandled_NothingSet()
    {
        var (doc, ctrl, layout) = NewScene();
        Assert.False(ctrl.DoubleLeft(At(layout.NoteColumn(0), layout, 96 * T)));
        Assert.Null(doc.Project.PlaybackStartFrame);
    }

    [Fact]
    public void DeleteSelection_DeletesAllSelected_AsOneUndoAction()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 96 * T));
        int undoBefore = doc.UndoStack.UndoDepth;

        Assert.True(ctrl.DeleteSelection());

        Assert.Empty(doc.CurrentTab.Lanes[0].Notes);
        Assert.Empty(doc.CurrentTab.Lanes[1].Notes);
        Assert.Empty(doc.Selection);
        Assert.Equal(undoBefore + 1, doc.UndoStack.UndoDepth); // 1回のDelete=1Undoアクション

        doc.Undo();
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(96 * T, doc.CurrentTab.Lanes[1].Notes);
    }

    [Fact]
    public void DeleteSelection_FreezeSelectedByBothEnds_DeletesOnce()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceFreezeAction(0, 48, 96));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 0, 48));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeEnd, 0, 48)); // 同一フリーズの別参照

        Assert.True(ctrl.DeleteSelection());
        Assert.Empty(doc.CurrentTab.Lanes[0].Freezes);

        doc.Undo();
        Assert.Single(doc.CurrentTab.Lanes[0].Freezes); // 二重削除・二重復元にならない
    }

    [Fact]
    public void DeleteSelection_EmptySelection_ReturnsFalse()
    {
        var (doc, ctrl, _) = NewScene();
        Assert.False(ctrl.DeleteSelection());
        Assert.False(doc.UndoStack.CanUndo);
    }

    // --- tick0-BPM guard(コントローラ経由でも例外を投げない) ---

    [Fact]
    public void RightClickTick0Bpm_DoesNothing_NoException()
    {
        var (doc, ctrl, layout) = NewScene();
        var col = layout.Column(ColumnKind.Bpm);
        var ex = Record.Exception(() => RightClick(ctrl, At(col, layout, 0 * T)));
        Assert.Null(ex);
        Assert.Contains(doc.Project.BpmEvents, e => e.Tick == 0);
    }

    // =====================================================================
    // Ctrl+クリック/Ctrl+右ドラッグ = 選択に追加(2026-07-23)
    // =====================================================================

    [Fact]
    public void CtrlClick_AddsToSelection_WithoutClearingExisting()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));
        Click(ctrl, At(layout.NoteColumn(1), layout, 96 * T), PointerModifiers.Ctrl);

        Assert.Equal(2, doc.Selection.Count);
        Assert.Contains(doc.Selection, r => r.Lane == 0 && r.Tick == 48 * T);
        Assert.Contains(doc.Selection, r => r.Lane == 1 && r.Tick == 96 * T);
    }

    [Fact]
    public void CtrlClick_AlreadySelected_DoesNotDuplicate()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T), PointerModifiers.Ctrl);
        Assert.Single(doc.Selection);
    }

    [Fact]
    public void CtrlRightDrag_AddsToSelection_WithoutClearingExisting()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(2, 200 * T));
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        var col2 = layout.NoteColumn(2);
        double y = layout.TickToY(200 * T);
        var from = new PointerPos(col2.CenterX - 5, y - 20);
        var to = new PointerPos(col2.CenterX + 5, y + 20);
        ctrl.BeginRight(from, PointerModifiers.Ctrl);
        ctrl.Move(new PointerPos((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        ctrl.End(to);

        Assert.Equal(2, doc.Selection.Count);
        Assert.Contains(doc.Selection, r => r.Lane == 0 && r.Tick == 48 * T);
        Assert.Contains(doc.Selection, r => r.Lane == 2 && r.Tick == 200 * T);
    }

    [Fact]
    public void RightDrag_WithoutCtrl_ReplacesExistingSelection()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(2, 200 * T));
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        var col2 = layout.NoteColumn(2);
        double y = layout.TickToY(200 * T);
        DragRight(ctrl, new PointerPos(col2.CenterX - 5, y - 20), new PointerPos(col2.CenterX + 5, y + 20));

        Assert.Single(doc.Selection);
        Assert.Contains(doc.Selection, r => r.Lane == 2 && r.Tick == 200 * T);
    }

    // =====================================================================
    // 色編集モード(ncolor_data、2026-07-23)
    // =====================================================================

    [Fact]
    public void ColorEditMode_ClickNote_PaintsColorOnly()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#ff0000";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
        Assert.Null(entry.BandColor);
    }

    [Fact]
    public void ColorEditMode_ClickFreezeEdge_PaintsColorOnly()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#00ff00";

        Click(ctrl, At(layout.NoteColumn(0), layout, 0)); // 始点(端点)

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#00ff00", entry.Color);
        Assert.Null(entry.BandColor);
    }

    [Fact]
    public void ColorEditMode_ClickFreezeBody_PaintsBandOnly()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#0000ff";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T)); // 帯(始点終点の中間)

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Null(entry.Color);
        Assert.Equal("#0000ff", entry.BandColor);
    }

    [Fact]
    public void ColorEditMode_ShiftClickFreezeEdge_PaintsColorAndBandTogether()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#123456";

        Click(ctrl, At(layout.NoteColumn(0), layout, 0), PointerModifiers.Shift);

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#123456", entry.Color);
        Assert.Equal("#123456", entry.BandColor);
    }

    [Fact]
    public void ColorEditMode_MiddleClickFreeze_PaintsColorAndBandTogether()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#654321";

        ctrl.MiddleClick(At(layout.NoteColumn(0), layout, 0));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#654321", entry.Color);
        Assert.Equal("#654321", entry.BandColor);
    }

    [Fact]
    public void ColorEditMode_RightClickColoredNote_ResetsColor_ButNotTheNote()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#ff0000";
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));
        Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);

        RightClick(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void ColorEditMode_RightClickUncoloredNote_DoesNothing()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        int undoDepthBefore = doc.UndoStack.UndoDepth;

        RightClick(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes); // 通常削除に化けない
        Assert.Equal(undoDepthBefore, doc.UndoStack.UndoDepth); // 空振りでUndo履歴も積まれない
    }

    [Fact]
    public void ColorEditMode_EmptyCellClick_DoesNotPlaceNote()
    {
        var (doc, ctrl, layout) = NewScene();
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#ff0000";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        Assert.Empty(doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void ColorEditMode_MarkerLaneClick_StillSetsCurrentTick()
    {
        var (doc, ctrl, layout) = NewScene();
        ctrl.ColorEditModeEnabled = true;
        var col = layout.Column(ColumnKind.Marker);

        Click(ctrl, At(col, layout, 48 * T));

        Assert.Equal(48 * T, ctrl.CurrentTick);
    }

    [Fact]
    public void ColorEditMode_LeftDrag_DoesNotMoveSelectedObjects()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        ctrl.ColorEditModeEnabled = true;

        DragLeft(ctrl, At(layout.NoteColumn(0), layout, 48 * T), At(layout.NoteColumn(0), layout, 96 * T));

        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.DoesNotContain(96 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void ColorEditMode_DeleteKey_ResetsOnlyColoredSelectedNotes()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 48 * T)); // こちらは色未設定のまま
        ctrl.ColorEditModeEnabled = true;
        ctrl.PaintColorCode = "#ff0000";
        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 48 * T));

        Assert.True(ctrl.DeleteSelection());
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes); // ノート自体は消えない
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[1].Notes);
    }

    [Fact]
    public void ColorEditMode_DeleteKey_NoColoredSelection_ReturnsFalse()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));

        Assert.False(ctrl.DeleteSelection());
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void BulkFillSelection_PaintsNoteAndFreeze_IgnoresOtherKinds()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceFreezeAction(1, 96 * T, 192 * T));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 48 * T, 1.5));

        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 1, 96 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Speed, -1, 48 * T)); // 無視される

        Assert.True(ctrl.BulkFillSelection("#abcdef"));

        var noteEntry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#abcdef", noteEntry.Color);
        var freezeEntry = Assert.Single(doc.CurrentTab.Lanes[1].ColorOverrides);
        Assert.Equal("#abcdef", freezeEntry.Color);
        Assert.Equal("#abcdef", freezeEntry.BandColor);
    }

    [Fact]
    public void BulkFillSelection_NoNoteOrFreezeSelected_ReturnsFalse()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 48 * T, 1.5));
        doc.Selection.Add(new ObjectRef(ObjectKind.Speed, -1, 48 * T));

        Assert.False(ctrl.BulkFillSelection("#abcdef"));
    }

    [Fact]
    public void ClearAllNoteColors_RemovesAllOverrides_UndoRestores()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new SetNoteColorAction(0, 48 * T, "#ff0000", setColor: true, setBand: false));

        Assert.True(ctrl.ClearAllNoteColors());
        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);

        doc.Undo();
        Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
    }

    [Fact]
    public void ClearAllNoteColors_NoOverrides_ReturnsFalse()
    {
        var (doc, ctrl, _) = NewScene();
        Assert.False(ctrl.ClearAllNoteColors());
    }

    // =====================================================================
    // SnappedTickAt(2026-07-25、マウスカーソルライン表示用に公開)
    // =====================================================================

    [Fact]
    public void SnappedTickAt_SnapEnabled_MatchesSnapService()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Snap.Division = 16; // GridTicks = 12
        var col = layout.NoteColumn(0);
        var pos = At(col, layout, 100 * T); // グリッドから少しずれた位置

        long expected = doc.Snap.Snap(layout.YToTick(pos.Y));
        Assert.Equal(expected, ctrl.SnappedTickAt(pos));
    }

    [Fact]
    public void SnappedTickAt_SnapDisabled_RoundsToNearestFrame()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Snap.Enabled = false;
        var col = layout.NoteColumn(0);
        var pos = At(col, layout, 100 * T);

        // スナップOFF時は「最寄りの整数フレーム」に丸められるため、tick単位の素の丸めとは異なりうる。
        // ここでは少なくとも0以上の妥当なtickが返ることと、実際の配置結果(クリック)と一致することを確認する。
        long snapped = ctrl.SnappedTickAt(pos);
        Assert.True(snapped >= 0);

        Click(ctrl, pos);
        Assert.Contains(snapped, doc.CurrentTab.Lanes[0].Notes);
    }

    // =====================================================================
    // Shadowサブモード / FrzHitサブモード(2026-07-24)
    // =====================================================================

    [Fact]
    public void ShadowSubMode_ClickNote_PaintsArrowShadowOnly()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.Shadow;
        ctrl.PaintArrowShadowColor = "#111111";
        ctrl.PaintNormalShadowColor = "#222222"; // ノートには使われないはず

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#111111", entry.ShadowColor);
        Assert.Null(entry.Color);
    }

    [Fact]
    public void ShadowSubMode_ClickFreezeAnyPart_PaintsNormalShadow()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.Shadow;
        ctrl.PaintNormalShadowColor = "#333333";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T)); // 帯部分をクリック

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#333333", entry.ShadowColor);
        Assert.Null(entry.Color);
        Assert.Null(entry.BandColor);
    }

    [Fact]
    public void ShadowSubMode_NoColorSet_DoesNothing()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.Shadow;

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
    }

    [Fact]
    public void BulkFillShadowSelection_PaintsNoteAndFreezeWithRespectiveColors()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceFreezeAction(1, 96 * T, 192 * T));
        ctrl.PaintArrowShadowColor = "#aaaaaa";
        ctrl.PaintNormalShadowColor = "#bbbbbb";

        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 1, 96 * T));

        Assert.True(ctrl.BulkFillShadowSelection());

        var noteEntry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#aaaaaa", noteEntry.ShadowColor);
        var freezeEntry = Assert.Single(doc.CurrentTab.Lanes[1].ColorOverrides);
        Assert.Equal("#bbbbbb", freezeEntry.ShadowColor);
    }

    [Fact]
    public void BulkFillShadowSelection_NoColorsSet_ReturnsFalse()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));

        Assert.False(ctrl.BulkFillShadowSelection());
    }

    [Fact]
    public void FrzHitSubMode_ClickFreeze_PaintsOnlyEnabledFields()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.FrzHit;
        ctrl.HitEnabled = true;
        ctrl.PaintHitColor = "#ff0000";
        ctrl.HitBarEnabled = false;
        ctrl.PaintHitBarColor = "#00ff00"; // Bar側は無効化されているので反映されないはず
        ctrl.HitShadowEnabled = true;
        ctrl.PaintHitShadowColor = "#0000ff";

        Click(ctrl, At(layout.NoteColumn(0), layout, 0));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.HitColor);
        Assert.Null(entry.HitBarColor);
        Assert.Equal("#0000ff", entry.HitShadowColor);
    }

    [Fact]
    public void FrzHitSubMode_ClickNote_DoesNothing()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.FrzHit;
        ctrl.HitEnabled = true;
        ctrl.PaintHitColor = "#ff0000";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
    }

    [Fact]
    public void FrzHitSubMode_AllCheckboxesDisabled_DoesNothing()
    {
        var (doc, ctrl, layout) = NewScene();
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 96 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.FrzHit;
        ctrl.PaintHitColor = "#ff0000"; // 色は入っているがEnabledが全てfalse
        ctrl.PaintHitBarColor = "#00ff00";
        ctrl.PaintHitShadowColor = "#0000ff";

        Click(ctrl, At(layout.NoteColumn(0), layout, 0));

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
    }

    [Fact]
    public void BulkFillFrzHitSelection_FreezeOnly_IgnoresNotes()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceFreezeAction(1, 96 * T, 192 * T));
        ctrl.HitEnabled = true;
        ctrl.PaintHitColor = "#123456";
        ctrl.HitBarEnabled = true;
        ctrl.PaintHitBarColor = "#654321";

        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 1, 96 * T));

        Assert.True(ctrl.BulkFillFrzHitSelection());

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides); // ノートは無視される
        var freezeEntry = Assert.Single(doc.CurrentTab.Lanes[1].ColorOverrides);
        Assert.Equal("#123456", freezeEntry.HitColor);
        Assert.Equal("#654321", freezeEntry.HitBarColor);
    }

    [Fact]
    public void BulkFillFrzHitSelection_NoCheckboxEnabled_ReturnsFalse()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceFreezeAction(0, 48 * T, 96 * T));
        ctrl.PaintHitColor = "#123456"; // Enabled無しなので不成立
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 0, 48 * T));

        Assert.False(ctrl.BulkFillFrzHitSelection());
    }

    [Fact]
    public void NormalSubMode_UnaffectedByShadowOrHitFields()
    {
        // SubMode切り替えロジックがNormalモードの既存動作を壊していないことの回帰確認
        var (doc, ctrl, layout) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        ctrl.ColorEditModeEnabled = true;
        ctrl.SubMode = ColorEditSubMode.Normal;
        ctrl.PaintColorCode = "#ff0000";
        ctrl.PaintArrowShadowColor = "#000000";

        Click(ctrl, At(layout.NoteColumn(0), layout, 48 * T));

        var entry = Assert.Single(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
        Assert.Null(entry.ShadowColor);
    }
}
