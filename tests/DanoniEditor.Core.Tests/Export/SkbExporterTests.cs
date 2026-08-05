using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests.Export;

/// <summary>SkbExporter(2026-08-03、SkbImporterの逆方向)のテスト。
/// Export→Importの往復でノート・フリーズ・速度変化・BPMが一致することを軸に検証する。</summary>
public class SkbExporterTests
{
    private static (ChartProject Project, DifficultyTab Tab, KeyTemplate Template) BuildSample(double bpm = 120)
    {
        var repo = TestFixtures.Repository();
        var template = repo.Get("5");
        var project = TestFixtures.NewProject(bpm);
        var tab = project.Tabs[0];

        long beat = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat;
        tab.Lanes[0].Notes.Add(0);
        tab.Lanes[0].Notes.Add(beat * 2);
        tab.Lanes[1].Freezes.Add(new FreezeNote(beat, beat * 3));
        tab.SpeedEvents.Add(new ValueEvent(0, 1.0));
        tab.SpeedEvents.Add(new ValueEvent(beat * 4, 2.0));
        tab.BoostEvents.Add(new ValueEvent(beat, 1.5));

        return (project, tab, template);
    }

    [Fact]
    public void Export_RoundtripsNotesFreezesAndSpeeds()
    {
        var (project, tab, template) = BuildSample();
        var result = SkbExporter.Export(project, tab, template, new SkbExportOptions());
        Assert.Empty(result.Warnings);

        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(result.Json);

        Assert.Equal(tab.Lanes[0].Notes.OrderBy(x => x), imported.Tab.Lanes[0].Notes.OrderBy(x => x));
        Assert.Equal(tab.Lanes[1].Freezes.Select(f => (f.StartTick, f.EndTick)),
                     imported.Tab.Lanes[1].Freezes.Select(f => (f.StartTick, f.EndTick)));
        Assert.Equal(tab.SpeedEvents.Select(e => (e.Tick, e.Value)), imported.Tab.SpeedEvents.Select(e => (e.Tick, e.Value)));
        Assert.Equal(tab.BoostEvents.Select(e => (e.Tick, e.Value)), imported.Tab.BoostEvents.Select(e => (e.Tick, e.Value)));
    }

    [Fact]
    public void Export_RoundtripsBpmAndBlankFrame()
    {
        var (project, tab, template) = BuildSample(bpm: 150);
        project.BlankFrame = 300;
        var result = SkbExporter.Export(project, tab, template, new SkbExportOptions());

        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(result.Json);

        Assert.Equal(300, imported.BlankFrame);
        Assert.Equal(150, imported.BpmEvents[0].Bpm);
    }

    [Fact]
    public void Export_MisalignedBpmEvent_RoundsToNearestPageBoundaryByDefault()
    {
        var (project, tab, template) = BuildSample();
        long beat = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat;
        // pageBlockNum=4のページ境界(4拍単位)から最も近いのはtick=4拍だが、0拍(先頭)ではない
        // 位置(5拍目)にBPM変化を追加し、丸め後も先頭のBPM変化(tick0)と衝突しないようにする
        project.BpmEvents.Add(new DanoniEditor.Core.Timing.BpmEvent(beat * 5, 140));

        var result = SkbExporter.Export(project, tab, template, new SkbExportOptions { PageBlockNum = 4, RoundMisalignedBpmEvents = true });

        Assert.Contains(result.Warnings, w => w.Contains("丸めました"));
        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(result.Json);
        Assert.Equal(2, imported.BpmEvents.Count);
        Assert.Equal(0, imported.BpmEvents[1].Tick % (4 * beat));
    }

    [Fact]
    public void Export_MisalignedBpmEvent_DeletesWhenRoundingDisabled()
    {
        var (project, tab, template) = BuildSample();
        long beat = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat;
        project.BpmEvents.Add(new DanoniEditor.Core.Timing.BpmEvent(beat, 140));

        var result = SkbExporter.Export(project, tab, template, new SkbExportOptions { PageBlockNum = 4, RoundMisalignedBpmEvents = false });

        Assert.Contains(result.Warnings, w => w.Contains("削除しました"));
        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(result.Json);
        Assert.Single(imported.BpmEvents);
    }

    [Fact]
    public void Export_AlignedBpmEvent_NoWarning()
    {
        var (project, tab, template) = BuildSample();
        long beat = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat;
        // pageBlockNum=4のページ境界にちょうど乗るBPM変化(4拍目)
        project.BpmEvents.Add(new DanoniEditor.Core.Timing.BpmEvent(beat * 4, 140));

        var result = SkbExporter.Export(project, tab, template, new SkbExportOptions { PageBlockNum = 4 });

        Assert.DoesNotContain(result.Warnings, w => w.Contains("丸めました") || w.Contains("削除しました"));
        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(result.Json);
        Assert.Equal(2, imported.BpmEvents.Count);
    }

    /// <summary>
    /// 2026-08-03に発覚したSKBネイティブtick(1/48拍)⇔内部tick(TicksPerBeat=1680)の変換漏れ不具合の
    /// 回帰テスト。ユーザー提供の実データ(By Node_S-HARD_skb.json、pageBlockNum=4・keyKind=7)から
    /// 抜粋した2ページ分をそのままSkbImporterで読み込み、PosScale(=35)による変換が正しく適用され、
    /// 内部tickとして期待どおりの値になることを検証する(修正前はpos値がそのままtickとして扱われ、
    /// 実際のSKBエディタで開いた際に位置が最大35倍近くズレる不具合があった)。
    /// </summary>
    [Fact]
    public void Import_RealWorldSkbFile_AppliesPosScaleCorrectly()
    {
        // keyKind="7"の実データ(By Node_S-HARD_skb.json)から抜粋したpos値をそのまま流用しつつ、
        // テスト環境のテンプレート資産に合わせてkeyKind="5"の5レーン構成に写して検証する
        // (検証したいのはPosScale変換ロジックであり、キー数そのものではないため)。
        const string json = """
        {
          "keyKind": "5",
          "scores": [
            { "notes": [[],[],[],[],[]], "freezes": [[],[],[],[],[]], "speeds": [] },
            { "notes": [[0,5040],[],[],[],[2520]], "freezes": [[],[],[],[],[]], "speeds": [] }
          ],
          "blankFrame": 200,
          "timings": [{ "label": 1, "startNum": 2.269352741171419, "bpm": 185, "pageBlockNum": 4 }],
          "scoreNumber": 1,
          "scorePrefix": null
        }
        """;

        var imported = new SkbImporter(TestFixtures.Repository().Get).Import(json);

        long beat = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat; // 1680
        const long posScale = 35; // TicksPerBeat(1680) / SKBネイティブ48tick
        long pageBase = 4 * beat; // ページ1(0始まり) × (pageBlockNum4 × TicksPerBeat)

        Assert.Equal(new[] { pageBase + 0 * posScale, pageBase + 5040 * posScale },
                     imported.Tab.Lanes[0].Notes.OrderBy(x => x));
        Assert.Equal(pageBase + 2520 * posScale, Assert.Single(imported.Tab.Lanes[4].Notes));
    }
}
