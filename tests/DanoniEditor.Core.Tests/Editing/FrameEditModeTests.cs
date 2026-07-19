using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// フレーム情報モード(仕様書7.6、2026-07-17i)のテスト。BPM120基準(1tick=0.625frame、tick96=60frame)。
/// 「BPMを動かしてもオブジェクトの絶対フレームが動かない」「モード切替を跨いだUndoが成立する」
/// 「丸め衝突の検出と統合」を検証する。
/// </summary>
public class FrameEditModeTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    [Fact]
    public void BpmChangeInFrameMode_KeepsAbsoluteFrames()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.CurrentTab.Lanes[0].Notes.Add(96 * T);   // 60frame
        doc.CurrentTab.Lanes[0].Notes.Add(192 * T);  // 120frame
        doc.EnterFrameEditMode();

        // tick96にBPM240を追加 → 以降は1tick=0.3125frame。frame120は tick96 + 60/0.3125 = 288
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96 * T, 240));

        Assert.Contains(96 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.Contains(288 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.DoesNotContain(192 * T, doc.CurrentTab.Lanes[0].Notes);

        // Undoで1手(BPM追加+逆算再配置がまとめて)巻き戻る
        doc.Undo();
        Assert.Contains(192 * T, doc.CurrentTab.Lanes[0].Notes);
        Assert.DoesNotContain(doc.Project.BpmEvents, e => e.Tick == 96);

        doc.Redo();
        Assert.Contains(288 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void RepeatedBpmChanges_DoNotAccumulateRoundingError()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.CurrentTab.Lanes[0].Notes.Add(192 * T); // 120frame
        doc.EnterFrameEditMode();

        // BPM変更を繰り返しても、毎回スナップショット(120frame)から逆算されるため元に戻せば完全一致
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96 * T, 181)); // 中途半端なBPM
        doc.Execute(new DeleteValueEventAction(ValueEventKind.Bpm, 96 * T));
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 48 * T, 233));
        doc.Execute(new DeleteValueEventAction(ValueEventKind.Bpm, 48 * T));

        Assert.Contains(192 * T, doc.CurrentTab.Lanes[0].Notes); // 誤差ゼロで復元
    }

    [Fact]
    public void UndoWorksAcrossModeSwitches()
    {
        // ユーザー確定仕様の例: 通常で配置→フレームモードで配置→通常で配置 = Undo履歴3件
        var doc = TestFixtures.NewDocument();
        doc.Execute(new PlaceNoteAction(0, 48 * T));   // A(通常)
        doc.EnterFrameEditMode();
        doc.Execute(new PlaceNoteAction(0, 96 * T));   // B(フレームモード)
        doc.ExitFrameEditMode(mergeDuplicates: false);
        doc.Execute(new PlaceNoteAction(0, 144 * T));  // C(通常)

        Assert.Equal(3, doc.CurrentTab.Lanes[0].Notes.Count);
        doc.Undo(); // C
        Assert.DoesNotContain(144 * T, doc.CurrentTab.Lanes[0].Notes);
        doc.Undo(); // B(モードOFF後でもFrameModeActionのUndoが正しく動く)
        Assert.DoesNotContain(96 * T, doc.CurrentTab.Lanes[0].Notes);
        doc.Undo(); // A
        Assert.Empty(doc.CurrentTab.Lanes[0].Notes);
        Assert.False(doc.UndoStack.CanUndo);
    }

    [Fact]
    public void ObjectPlacedInFrameMode_AlsoKeepsFrameOnLaterBpmChange()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.EnterFrameEditMode();
        doc.Execute(new PlaceNoteAction(0, 96 * T)); // 60frame(配置後にスナップショットへ反映される)

        // tick48にBPM240 → frame60 = tick48 + (60-30)/0.3125 = 144
        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 48 * T, 240));
        Assert.Contains(144 * T, doc.CurrentTab.Lanes[0].Notes);
    }

    [Fact]
    public void DirectTimingChange_CollisionDetection_AndMerge()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.CurrentTab.Lanes[0].Notes.Add(0);
        doc.CurrentTab.Lanes[0].Notes.Add(1); // 0.625frame差
        doc.EnterFrameEditMode();

        // tick0の共通BPMをUI直接書換相当で7.5へ(1tick=10frame) → 両ノートともtick0へ丸まる
        doc.Project.BpmEvents[0] = doc.Project.BpmEvents[0] with { Bpm = 7.5 };
        doc.OnTimingChangedDirectly();

        var collisions = doc.FindFrameEditCollisions();
        Assert.NotEmpty(collisions);

        doc.ExitFrameEditMode(mergeDuplicates: true);
        Assert.False(doc.IsFrameEditMode);
        Assert.Single(doc.CurrentTab.Lanes[0].Notes); // 統合済み

        doc.Undo(); // 統合はUndo可能
        Assert.Equal(2, doc.CurrentTab.Lanes[0].Notes.Count);
    }

    [Fact]
    public void Retick_PreservesValueEventValuesAndMarkerComments()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.CurrentTab.SpeedEvents.Add(new ValueEvent(192 * T, 2.5));  // 120frame
        doc.Project.Markers.Add(new Marker(192 * T, "サビ頭"));
        doc.EnterFrameEditMode();

        doc.Execute(new PlaceValueEventAction(ValueEventKind.Bpm, 96 * T, 240)); // frame120→tick288

        var sp = Assert.Single(doc.CurrentTab.SpeedEvents);
        Assert.Equal(288 * T, sp.Tick);
        Assert.Equal(2.5, sp.Value);
        var m = Assert.Single(doc.Project.Markers);
        Assert.Equal(288 * T, m.Tick);
        Assert.Equal("サビ頭", m.Comment);
    }

    [Fact]
    public void Retick_FreezeKeepsMinimumLength()
    {
        var doc = TestFixtures.NewDocument(bpm: 120);
        doc.CurrentTab.Lanes[0].Freezes.Add(new FreezeNote(0, 1)); // 0.625frame差
        doc.EnterFrameEditMode();

        doc.Project.BpmEvents[0] = doc.Project.BpmEvents[0] with { Bpm = 7.5 };
        doc.OnTimingChangedDirectly();

        var f = Assert.Single(doc.CurrentTab.Lanes[0].Freezes);
        Assert.True(f.EndTick > f.StartTick); // start<end不変条件の維持(最低1tick)
    }

    [Fact]
    public void ExitWithoutCollisions_NoExtraUndoEntry()
    {
        var doc = TestFixtures.NewDocument();
        doc.EnterFrameEditMode();
        doc.Execute(new PlaceNoteAction(0, 48 * T));
        int depth = doc.UndoStack.UndoDepth;
        doc.ExitFrameEditMode(mergeDuplicates: false);
        Assert.Equal(depth, doc.UndoStack.UndoDepth); // 衝突なし終了は履歴を汚さない
    }
}
