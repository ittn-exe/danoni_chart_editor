using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>SKB操作モード(キーボード操作、2026-07-21確定仕様)のKeyboardModeControllerテスト</summary>
public class KeyboardModeControllerTests
{
    private static (EditorDocument Doc, KeyboardModeController Kbd) NewScene(string keyTypeId = "5")
    {
        var doc = TestFixtures.NewDocument(keyTypeId: keyTypeId);
        return (doc, new KeyboardModeController(doc));
    }

    private static long CursorTick(EditorDocument doc)
    {
        var engine = doc.Project.CreateTimingEngine();
        return (long)Math.Round(engine.FrameToTick(doc.Project.PlaybackStartFrame!.Value));
    }

    // --- EnterMode ---

    [Fact]
    public void EnterMode_InitializesPlaybackStartFrame_WhenNull()
    {
        var (doc, kbd) = NewScene();
        Assert.Null(doc.Project.PlaybackStartFrame);

        kbd.EnterMode();

        Assert.NotNull(doc.Project.PlaybackStartFrame);
        Assert.Equal(0, CursorTick(doc));
    }

    [Fact]
    public void EnterMode_DoesNotOverride_WhenAlreadySet()
    {
        var (doc, kbd) = NewScene();
        var engine = doc.Project.CreateTimingEngine();
        doc.Project.PlaybackStartFrame = engine.TickToFrame(500);

        kbd.EnterMode();

        Assert.Equal(500, CursorTick(doc));
    }

    // --- MoveCursor ---

    [Fact]
    public void MoveCursor_Forward_AdvancesByGridTicks()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        long grid = doc.Snap.GridTicks;

        kbd.MoveCursor(forward: true);

        Assert.Equal(grid, CursorTick(doc));
    }

    [Fact]
    public void MoveCursor_Backward_ClampsAtZero()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode(); // tick0開始

        kbd.MoveCursor(forward: false);

        Assert.Equal(0, CursorTick(doc));
    }

    [Fact]
    public void MoveCursor_ForwardThenBackward_ReturnsToOrigin()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();

        kbd.MoveCursor(forward: true);
        kbd.MoveCursor(forward: false);

        Assert.Equal(0, CursorTick(doc));
    }

    // --- MoveCursorByMeasure(2026-07-21追加: ←/→系ショートカット) ---

    [Fact]
    public void MoveCursorByMeasure_Forward1_MovesToNextMeasureStart()
    {
        var (doc, kbd) = NewScene(); // 既定4/4、1小節=4拍=4*1680=6720tick
        kbd.EnterMode();

        kbd.MoveCursorByMeasure(1);

        Assert.Equal(6720, CursorTick(doc));
    }

    [Fact]
    public void MoveCursorByMeasure_Forward2And4_MovesByThatManyMeasures()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();

        kbd.MoveCursorByMeasure(2);
        Assert.Equal(6720 * 2, CursorTick(doc));

        kbd.MoveCursorByMeasure(4);
        Assert.Equal(6720 * 6, CursorTick(doc));
    }

    [Fact]
    public void MoveCursorByMeasure_Backward_ClampsAtMeasure0()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode(); // tick0(小節0)から開始

        kbd.MoveCursorByMeasure(-1);

        Assert.Equal(0, CursorTick(doc)); // 小節0より前には行かない
    }

    [Fact]
    public void MoveCursorByMeasure_ForwardThenBackward_ReturnsToOrigin()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();

        kbd.MoveCursorByMeasure(2);
        kbd.MoveCursorByMeasure(-2);

        Assert.Equal(0, CursorTick(doc));
    }

    [Fact]
    public void MoveCursorByMeasure_AcrossTimeSignatureChange_UsesCorrectMeasureLength()
    {
        var (doc, kbd) = NewScene();
        // 小節2から3/4拍子(1小節=3*1680=5040tick)に変更。小節0-1は4/4(6720tick)のまま。
        doc.Project.TimeSignatures.Add(new DanoniEditor.Core.Timing.TimeSignatureEvent(2, 3, 4));
        var engine = doc.Project.CreateTimingEngine();
        doc.Project.PlaybackStartFrame = engine.TickToFrame(6720 * 2); // 小節2の頭(拍子変化点)から開始
        kbd.EnterMode(); // 既にPlaybackStartFrame設定済みなので上書きされない

        kbd.MoveCursorByMeasure(1); // 小節3の頭 = 小節2の頭 + 3/4拍子1小節分(5040tick)

        Assert.Equal(6720 * 2 + 5040, CursorTick(doc));
    }

    [Fact]
    public void MoveCursorByMeasure_IsExplicitMove_ResetsSimultaneousPressGrouping()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        long grid = doc.Snap.GridTicks;
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(10); // 閾値内だが、間にMoveCursorByMeasureを挟むため同時押しにはならないはず

        kbd.ToggleNoteAtCursor(0, t0); // tick0配置、カーソルはgridへ前進
        kbd.MoveCursorByMeasure(1);    // 明示移動: 同時押し記録をリセットするはず
        kbd.ToggleNoteAtCursor(1, t1); // 新規チョード扱いになるべき(移動後のカーソル位置に配置)

        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(6720L, doc.CurrentTab.Lanes[1].Notes); // 小節1の頭(grid前進後にさらに1小節移動した位置)
    }

    // --- MoveCursorToPreviousMeasureOrCurrentStart(2026-07-22追加: ←の特別仕様) ---

    [Fact]
    public void MoveCursorToPreviousMeasureOrCurrentStart_MidMeasure_SnapsToCurrentMeasureStart()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        kbd.MoveCursorByMeasure(1); // 小節1の頭(6720)へ
        kbd.MoveCursor(forward: true); // 小節途中(6720+420)へずらす

        kbd.MoveCursorToPreviousMeasureOrCurrentStart();

        Assert.Equal(6720, CursorTick(doc)); // 1つ前の小節(0)へは進まず、現在の小節(1)の頭へ戻る
    }

    [Fact]
    public void MoveCursorToPreviousMeasureOrCurrentStart_AtMeasureStart_MovesToPreviousMeasureStart()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        kbd.MoveCursorByMeasure(1); // 小節1の頭(6720)、ちょうど小節頭

        kbd.MoveCursorToPreviousMeasureOrCurrentStart();

        Assert.Equal(0, CursorTick(doc)); // 既に小節頭だったので1つ前の小節(0)の頭へ
    }

    [Fact]
    public void MoveCursorToPreviousMeasureOrCurrentStart_AtMeasure0Start_ClampsAt0()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode(); // tick0(小節0の頭)

        kbd.MoveCursorToPreviousMeasureOrCurrentStart();

        Assert.Equal(0, CursorTick(doc));
    }

    // --- ToggleNoteAtCursor ---

    [Fact]
    public void ToggleNoteAtCursor_InvalidLane_ReturnsFalse()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        Assert.False(kbd.ToggleNoteAtCursor(-1, DateTime.UtcNow));
        Assert.False(kbd.ToggleNoteAtCursor(999, DateTime.UtcNow));
    }

    [Fact]
    public void ToggleNoteAtCursor_PlacesNote_AndAdvancesCursor_WhenNotSimultaneous()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        long grid = doc.Snap.GridTicks;
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(kbd.ToggleNoteAtCursor(0, t0));

        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Equal(grid, CursorTick(doc)); // 同時押しでない単発入力は直後にカーソルが進む
    }

    [Fact]
    public void ToggleNoteAtCursor_PressedTwiceAtSameTick_TogglesOff()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(10); // 閾値超え、かつ同じカーソル位置に戻すためカーソルを一度戻す

        kbd.ToggleNoteAtCursor(0, t0); // 配置 + カーソル前進
        kbd.MoveCursor(forward: false); // カーソルを配置位置(tick0)に戻す

        Assert.True(kbd.ToggleNoteAtCursor(0, t1)); // 同じtick0への再入力=削除
        Assert.DoesNotContain(0L, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void ToggleNoteAtCursor_SimultaneousPress_UsesSameTick_AndDoesNotAdvanceCursorFurther()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        kbd.ThresholdMs = 30;
        long grid = doc.Snap.GridTicks;
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(10); // 閾値内=同時押し扱い

        kbd.ToggleNoteAtCursor(0, t0); // lane0にtick0で配置、カーソルはgridへ前進
        kbd.ToggleNoteAtCursor(1, t1); // 同時押し: lane1もtick0に配置されるべき(前進後のtickではない)

        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(0L, doc.CurrentTab.Lanes[1].Notes);
        Assert.Equal(grid, CursorTick(doc)); // 同時押しはカーソルをさらに進めない
    }

    [Fact]
    public void ToggleNoteAtCursor_PressBeyondThreshold_IsTreatedAsNewChord()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        kbd.ThresholdMs = 30;
        long grid = doc.Snap.GridTicks;
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(200); // 閾値超え

        kbd.ToggleNoteAtCursor(0, t0); // tick0配置、カーソルはgridへ
        kbd.ToggleNoteAtCursor(1, t1); // 新規チョード: 現在のカーソル位置(grid)に配置されるべき

        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(grid, doc.CurrentTab.Lanes[1].Notes);
        Assert.Equal(grid * 2, CursorTick(doc));
    }

    // --- ToggleFreezeAtCursor ---

    [Fact]
    public void ToggleFreezeAtCursor_TwoPresses_CreatesFreezeSpanningBothTicks()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(1); // 閾値超え(意図的に別チョードとして扱う)

        long grid = doc.Snap.GridTicks;

        Assert.True(kbd.ToggleFreezeAtCursor(0, t0)); // 開始点登録(tick0)。単発入力なのでカーソルはgridへ自動前進する
        Assert.True(kbd.PendingFreezeStarts.ContainsKey(0));
        Assert.Equal(grid, CursorTick(doc));

        Assert.True(kbd.ToggleFreezeAtCursor(0, t1)); // 完了点(現在のカーソル位置=grid)
        Assert.False(kbd.PendingFreezeStarts.ContainsKey(0));

        var freeze = Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        Assert.Equal(0, freeze.StartTick);
        Assert.Equal(grid, freeze.EndTick);
    }

    [Fact]
    public void ToggleFreezeAtCursor_SameTickTwice_CancelsWithoutCreatingFreeze()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(10); // 閾値内=同時押し扱い→同じtickのまま

        kbd.ToggleFreezeAtCursor(0, t0); // 開始点登録
        kbd.ToggleFreezeAtCursor(0, t1); // 同じtickでの2回目=キャンセル

        Assert.Empty(doc.CurrentTab.Lanes[0].Freezes);
        Assert.False(kbd.PendingFreezeStarts.ContainsKey(0));
    }

    [Fact]
    public void ToggleFreezeAtCursor_InvalidLane_ReturnsFalse()
    {
        var (_, kbd) = NewScene();
        Assert.False(kbd.ToggleFreezeAtCursor(999, DateTime.UtcNow));
    }

    // --- DeleteAtCursor ---

    [Fact]
    public void DeleteAtCursor_NoData_ReturnsFalse()
    {
        var (_, kbd) = NewScene();
        kbd.EnterMode();
        Assert.False(kbd.DeleteAtCursor());
    }

    [Fact]
    public void DeleteAtCursor_RemovesNotesAndFreezes_AcrossAllLanes_AtCursorTickOnly()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        doc.Execute(new PlaceNoteAction(0, 0));
        doc.Execute(new PlaceFreezeAction(1, 0, 420));
        doc.Execute(new PlaceNoteAction(2, 420)); // カーソル位置(tick0)ではないので対象外

        Assert.True(kbd.DeleteAtCursor());

        Assert.DoesNotContain(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Empty(doc.CurrentTab.Lanes[1].Freezes);
        Assert.Contains(420L, doc.CurrentTab.Lanes[2].Notes); // 他tickは残る
    }

    [Fact]
    public void DeleteAtCursor_ClearsPendingFreezeStart_AtCursorTick()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        var t0 = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        kbd.ToggleFreezeAtCursor(0, t0); // tick0に開始点だけ登録(未完了)。単発入力なのでカーソルはgridへ自動前進する
        kbd.MoveCursor(forward: false); // カーソルを開始点(tick0)まで戻す

        Assert.True(kbd.DeleteAtCursor());
        Assert.False(kbd.PendingFreezeStarts.ContainsKey(0));
    }

    [Fact]
    public void DeleteAtCursor_IsSingleUndoAction()
    {
        var (doc, kbd) = NewScene();
        kbd.EnterMode();
        doc.Execute(new PlaceNoteAction(0, 0));
        doc.Execute(new PlaceNoteAction(1, 0));
        int undoBefore = doc.UndoStack.UndoDepth;

        Assert.True(kbd.DeleteAtCursor());
        Assert.Equal(undoBefore + 1, doc.UndoStack.UndoDepth);

        doc.Undo();
        Assert.Contains(0L, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(0L, doc.CurrentTab.Lanes[1].Notes);
    }
}
