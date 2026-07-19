using DanoniEditor.Core.Import;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// SKBインポートのStartNumber軸修正(2026-07-18c)のテスト。
/// エディタ内部軸は「曲頭=0」: blankFrameを加算せず、startNumをそのまま使うこと、
/// 先頭区間にFrameAnchorを張らない(=右パネルのStartNumber編集が効く)ことを検証する。
/// </summary>
public class SkbImporterStartNumberTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private const string Json = """
    {
      "keyKind": "5",
      "scores": [
        { "notes": [[0],[],[],[],[]], "freezes": [[],[],[],[],[]], "speeds": [] },
        { "notes": [[],[],[],[],[]], "freezes": [[],[],[],[],[]], "speeds": [] },
        { "notes": [[],[],[],[],[]], "freezes": [[],[],[],[],[]], "speeds": [] }
      ],
      "blankFrame": 200,
      "timings": [
        { "label": 1, "startNum": -190, "bpm": 120, "pageBlockNum": 8 },
        { "label": 3, "startNum": 500, "bpm": 140, "pageBlockNum": 8 }
      ],
      "scoreNumber": 1,
      "scorePrefix": ""
    }
    """;

    private static SkbImportResult ImportSample()
    {
        var repo = TestFixtures.Repository();
        return new SkbImporter(repo.Get).Import(Json);
    }

    [Fact]
    public void NegativeStartNum_ImportsAsIs_WithoutBlankFrame()
    {
        var r = ImportSample();
        Assert.Equal(-190, r.StartNumber);      // 従来は blank(200) + (-190) = +10 になっていた
        Assert.Equal(200, r.BlankFrame);        // blankFrameはヘッダー値として別途保持
        Assert.Equal(-190, r.CreateTimingEngine().TickToFrame(0), 6);
    }

    [Fact]
    public void FirstSegment_HasNoFrameAnchor_SoStartNumberEditsWork()
    {
        var r = ImportSample();
        Assert.Null(r.BpmEvents[0].FrameAnchor);

        // StartNumberを差し替えてエンジンを組み直すと先頭区間が追従する(右パネル編集相当)
        var edited = new TimingEngine(-100, r.BpmEvents); // SKBに拍子概念は無い(常に4/4)
        Assert.Equal(-100, edited.TickToFrame(0), 6);
    }

    [Fact]
    public void LaterSegments_AnchorOnSongAxis_WithoutBlankFrame()
    {
        var r = ImportSample();
        // label=3 → tick = 2ページ × (8×TicksPerBeat)。アンカーは startNum=500(blank加算なし)
        var anchor = r.BpmEvents.First(b => b.Tick == 768 * T);
        Assert.Equal(500, anchor.FrameAnchor!.Value, 6);
        Assert.Equal(500, r.CreateTimingEngine().TickToFrame(768 * T), 6);
    }
}
