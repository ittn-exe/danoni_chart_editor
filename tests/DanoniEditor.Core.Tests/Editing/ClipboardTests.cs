using System.Linq;
using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>クリップボード(仕様書13章: Ctrl+X/C/V、6.3上段「クリップボード系」)のテスト</summary>
public class ClipboardTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35)

    public ClipboardTests() => EditorClipboard.Clear(); // staticなクリップボードを各テストの開始時にリセット

    private static (EditorDocument Doc, SmartToolController Ctrl, ChartLayout Layout) NewScene(string keyTypeId = "5")
    {
        var doc = TestFixtures.NewDocument(keyTypeId: keyTypeId);
        return (doc, new SmartToolController(doc), doc.CurrentLayout);
    }

    private static PointerPos At(ColumnInfo col, ChartLayout layout, long tick) => new(col.CenterX, layout.TickToY(tick));

    private static void Click(SmartToolController ctrl, PointerPos pos, PointerModifiers mods = PointerModifiers.None)
    {
        ctrl.BeginLeft(pos, mods);
        ctrl.End(pos);
    }

    /// <summary>再生開始フレーム(Project.PlaybackStartFrame)を直接tick指定で設定するヘルパ
    /// (2026-08-04: PasteのAnchorがCurrentTickからこちらへ変更された)。エンジンのTickToFrameで
    /// 変換するので、Paste側のFrameToTick変換と厳密に往復一致する。</summary>
    private static void SetPlaybackStartFrame(EditorDocument doc, long tick)
    {
        var engine = doc.Project.CreateTimingEngine();
        doc.Project.PlaybackStartFrame = engine.TickToFrame(tick);
    }

    // --- Copy: 有効化条件(仕様書6.3上段) ---

    [Fact]
    public void CopySelection_EmptySelection_ReturnsFalse()
    {
        var (_, ctrl, _) = NewScene();
        Assert.False(ctrl.CopySelection());
        Assert.False(EditorClipboard.HasContent);
    }

    [Fact]
    public void CopySelection_ExcludesTick0Bpm_AndTimeSignature()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Selection.Add(new ObjectRef(ObjectKind.Bpm, -1, 0)); // tick0のBPM(不変条件)
        doc.Selection.Add(new ObjectRef(ObjectKind.TimeSignature, -1, 0)); // 拍子(7.5: 物理小節頭固定)

        Assert.False(ctrl.CopySelection()); // コピー可能な対象が1つも無い
        Assert.False(EditorClipboard.HasContent);
    }

    // --- Paste: 基準点(PlaybackStartFrame) ---

    [Fact]
    public void Paste_EmptyClipboard_ReturnsFalse()
    {
        var (_, ctrl, _) = NewScene();
        Assert.False(ctrl.Paste());
    }

    [Fact]
    public void CopyThenPaste_Note_PastesAtPlaybackStartFrame_OriginalUntouched()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(1, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 48 * T));
        Assert.True(ctrl.CopySelection());

        SetPlaybackStartFrame(doc, 200 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains(200 * T, doc.CurrentTab.Lanes[1].Notes); // 新規貼り付け
        Assert.Contains(48 * T, doc.CurrentTab.Lanes[1].Notes);  // 元のノートはコピーなので残る
    }

    [Fact]
    public void Paste_WithoutPlaybackStartFrame_AnchorsAtTick0()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        ctrl.CopySelection();

        Assert.True(ctrl.Paste());
        Assert.Contains(0 * T, doc.CurrentTab.Lanes[0].Notes); // PlaybackStartFrame未設定 → tick0基準
    }

    [Fact]
    public void Paste_MultipleNotes_PreservesRelativeSpacing()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(0, 96 * T)); // 最小tick(48*T)から+48*T
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 96 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 500 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains(500 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(500 * T + 48 * T, doc.CurrentTab.Lanes[0].Notes); // 相対間隔を維持
    }

    [Fact]
    public void Paste_PreservesOriginalLane_NoLateralShift()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(3, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 3, 48 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 200 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains(200 * T, doc.CurrentTab.Lanes[3].Notes); // 元のレーン(3)のまま
        Assert.DoesNotContain(200 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    // --- Cut ---

    [Fact]
    public void CutSelection_RemovesOriginal_AsOneUndoAction_ClipboardStillPastable()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        int undoBefore = doc.UndoStack.UndoDepth;

        Assert.True(ctrl.CutSelection());
        Assert.Empty(doc.CurrentTab.Lanes[0].Notes); // 元は消える
        Assert.Equal(undoBefore + 1, doc.UndoStack.UndoDepth); // 削除ぶんのみ1Undo(コピー自体は非Undo対象)

        Assert.True(ctrl.Paste());
        Assert.Contains(0 * T, doc.CurrentTab.Lanes[0].Notes); // 貼り付けで復活
    }

    [Fact]
    public void CutSelection_EmptySelection_ReturnsFalse_NoUndoPushed()
    {
        var (doc, ctrl, _) = NewScene();
        Assert.False(ctrl.CutSelection());
        Assert.False(doc.UndoStack.CanUndo);
    }

    // --- フリーズ・マーカー・speed/boost/BPM ---

    [Fact]
    public void CopyThenPaste_Freeze_PreservesDuration()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceFreezeAction(0, 48 * T, 96 * T)); // 長さ48*T
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 0, 48 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        var pasted = doc.CurrentTab.Lanes[0].Freezes.FirstOrDefault(f => f.StartTick == 300 * T);
        Assert.NotNull(pasted);
        Assert.Equal(300 * T + 48 * T, pasted!.EndTick);
    }

    [Fact]
    public void CopyThenPaste_Marker_PreservesComment()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceMarkerAction(48 * T, "サビ頭"));
        doc.Selection.Add(new ObjectRef(ObjectKind.Marker, -1, 48 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains(doc.Project.Markers, m => m.Tick == 300 * T && m.Comment == "サビ頭");
    }

    [Fact]
    public void CopyThenPaste_SpeedEvent_PreservesValue()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Speed, 48 * T, 1.5));
        doc.Selection.Add(new ObjectRef(ObjectKind.Speed, -1, 48 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains(doc.CurrentTab.SpeedEvents, e => e.Tick == 300 * T && e.Value == 1.5);
    }

    [Fact]
    public void CopyThenPaste_Bpm_LandingOnTick0_IsSkipped()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 48 * T, 150));
        doc.Selection.Add(new ObjectRef(ObjectKind.Bpm, -1, 48 * T));
        ctrl.CopySelection(); // 相対tickオフセットは48*Tから見て0

        // PlaybackStartFrame未設定→貼り付け基準tick0。オフセット0なので着地先もtick0となり不変条件でスキップされる
        Assert.False(ctrl.Paste());
    }

    // --- キー種違いのプロジェクトへの貼り付け(レーン範囲外はスキップ) ---

    [Fact]
    public void Paste_SkipsObjectsOutsideTargetLaneCount()
    {
        var (srcDoc, srcCtrl, _) = NewScene("23");
        srcDoc.Execute(new PlaceNoteAction(20, 48 * T)); // 23key側のみ存在するレーン
        srcDoc.Selection.Add(new ObjectRef(ObjectKind.Note, 20, 48 * T));
        Assert.True(srcCtrl.CopySelection());

        var (dstDoc, dstCtrl, _) = NewScene("5"); // laneCount=5(0-4)、lane20は範囲外
        Assert.False(dstCtrl.Paste());
        Assert.All(dstDoc.CurrentTab.Lanes, l => Assert.Empty(l.Notes));
    }

    // --- 選択状態・Undo単位 ---

    [Fact]
    public void Paste_SelectsPastedObjects()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        ctrl.CopySelection();
        doc.Selection.Clear();

        SetPlaybackStartFrame(doc, 300 * T);
        ctrl.Paste();

        Assert.Contains(doc.Selection, r => r.Kind == ObjectKind.Note && r.Lane == 0 && r.Tick == 300 * T);
    }

    // --- 2026-08-05要望対応: frame情報以外(色情報・警告マーカー)を保持したままコピペ ---

    [Fact]
    public void CopyThenPaste_Note_PreservesColorOverrideAndAnnotation()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.CurrentTab.Lanes[0].ColorOverrides.Add(new NColorEntry(48 * T, "#FF0000", null, true,
            "#00FF00", "#0000FF", "#FFFF00", "#FF00FF"));
        doc.CurrentTab.Lanes[0].Annotations.Add(new NoteAnnotation(48 * T, "注意コメント", true));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        Assert.True(ctrl.CopySelection());

        SetPlaybackStartFrame(doc, 200 * T);
        Assert.True(ctrl.Paste());

        var color = doc.CurrentTab.Lanes[0].ColorOverrides.FirstOrDefault(c => c.Tick == 200 * T);
        Assert.NotNull(color);
        Assert.Equal("#FF0000", color!.Color);
        Assert.True(color.AllFlag);
        Assert.Equal("#00FF00", color.ShadowColor);
        Assert.Equal("#0000FF", color.HitColor);
        Assert.Equal("#FFFF00", color.HitBarColor);
        Assert.Equal("#FF00FF", color.HitShadowColor);

        var annotation = doc.CurrentTab.Lanes[0].Annotations.FirstOrDefault(a => a.Tick == 200 * T);
        Assert.NotNull(annotation);
        Assert.Equal("注意コメント", annotation!.Comment);
        Assert.True(annotation.Warning);

        // 元のオブジェクト側のデータもそのまま残っていること(コピーなので破壊されない)
        Assert.Contains(doc.CurrentTab.Lanes[0].ColorOverrides, c => c.Tick == 48 * T);
        Assert.Contains(doc.CurrentTab.Lanes[0].Annotations, a => a.Tick == 48 * T);
    }

    [Fact]
    public void CopyThenPaste_Freeze_PreservesColorOverrideAndAnnotation()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceFreezeAction(0, 48 * T, 96 * T));
        doc.CurrentTab.Lanes[0].ColorOverrides.Add(new NColorEntry(48 * T, "#ABCDEF", "#123456"));
        doc.CurrentTab.Lanes[0].Annotations.Add(new NoteAnnotation(48 * T, "フリーズ注釈", false));
        doc.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, 0, 48 * T));
        Assert.True(ctrl.CopySelection());

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        var pasted = doc.CurrentTab.Lanes[0].Freezes.FirstOrDefault(f => f.StartTick == 300 * T);
        Assert.NotNull(pasted);

        var color = doc.CurrentTab.Lanes[0].ColorOverrides.FirstOrDefault(c => c.Tick == 300 * T);
        Assert.NotNull(color);
        Assert.Equal("#ABCDEF", color!.Color);
        Assert.Equal("#123456", color.BandColor);

        var annotation = doc.CurrentTab.Lanes[0].Annotations.FirstOrDefault(a => a.Tick == 300 * T);
        Assert.NotNull(annotation);
        Assert.Equal("フリーズ注釈", annotation!.Comment);
    }

    [Fact]
    public void CopyThenPaste_Note_WithoutColorOrAnnotation_DoesNotCreateSpuriousEntries()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        Assert.True(ctrl.CopySelection());

        SetPlaybackStartFrame(doc, 200 * T);
        Assert.True(ctrl.Paste());

        Assert.Empty(doc.CurrentTab.Lanes[0].ColorOverrides);
        Assert.Empty(doc.CurrentTab.Lanes[0].Annotations);
    }

    [Fact]
    public void CopyThenPaste_Note_ColorOverrideUndo_RemovesPastedEntry()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.CurrentTab.Lanes[0].ColorOverrides.Add(new NColorEntry(48 * T, "#FF0000", null));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 200 * T);
        Assert.True(ctrl.Paste());
        Assert.Contains(doc.CurrentTab.Lanes[0].ColorOverrides, c => c.Tick == 200 * T);

        doc.Undo();
        Assert.DoesNotContain(doc.CurrentTab.Lanes[0].ColorOverrides, c => c.Tick == 200 * T);
        Assert.Contains(doc.CurrentTab.Lanes[0].ColorOverrides, c => c.Tick == 48 * T); // 元は残る
    }

    [Fact]
    public void Paste_RaisesObjectsPlaced_WithPastedCount()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 96 * T));
        ctrl.CopySelection();

        var placedCounts = new List<int>();
        doc.StatRecorded += (kind, c) => { if (kind == EditorStatKind.ObjectsPlaced) placedCounts.Add(c); };

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        Assert.Equal([2], placedCounts);
    }

    // --- 2026-08-05: 統計情報(Copy/Cut/Paste操作カウント)の検証 ---

    [Fact]
    public void CopySelection_RaisesCopyStat_Once()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));

        var recorded = new List<(EditorStatKind, int)>();
        doc.StatRecorded += (kind, c) => recorded.Add((kind, c));

        Assert.True(ctrl.CopySelection());

        Assert.Equal([(EditorStatKind.Copy, 1)], recorded);
    }

    [Fact]
    public void CutSelection_RaisesCutStat_NotCopyStat_PlusObjectsDeleted()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));

        var recorded = new List<(EditorStatKind, int)>();
        doc.StatRecorded += (kind, c) => recorded.Add((kind, c));

        Assert.True(ctrl.CutSelection());

        Assert.DoesNotContain(recorded, r => r.Item1 == EditorStatKind.Copy); // Cut自体はCopy統計を増やさない
        Assert.Contains((EditorStatKind.Cut, 1), recorded);
        Assert.Contains((EditorStatKind.ObjectsDeleted, 1), recorded);
    }

    [Fact]
    public void Paste_RaisesPasteStat_Once_SeparateFromObjectsPlaced()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        ctrl.CopySelection();

        var recorded = new List<(EditorStatKind, int)>();
        doc.StatRecorded += (kind, c) => recorded.Add((kind, c));

        SetPlaybackStartFrame(doc, 300 * T);
        Assert.True(ctrl.Paste());

        Assert.Contains((EditorStatKind.Paste, 1), recorded);
        Assert.Contains((EditorStatKind.ObjectsPlaced, 1), recorded);
    }

    [Fact]
    public void Paste_MultipleObjects_IsSingleUndoAction()
    {
        var (doc, ctrl, _) = NewScene();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        doc.Execute(new PlaceNoteAction(1, 96 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 0, 48 * T));
        doc.Selection.Add(new ObjectRef(ObjectKind.Note, 1, 96 * T));
        ctrl.CopySelection();

        SetPlaybackStartFrame(doc, 300 * T);
        int undoBefore = doc.UndoStack.UndoDepth;
        Assert.True(ctrl.Paste());
        Assert.Equal(undoBefore + 1, doc.UndoStack.UndoDepth);

        doc.Undo();
        Assert.DoesNotContain(300 * T, doc.CurrentTab.Lanes[0].Notes);
    }
}
