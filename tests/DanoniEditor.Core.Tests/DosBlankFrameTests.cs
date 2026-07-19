using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>dos.txt入出力のblankFrame軸変換(2026-07-19f: 出力=内部+blank、入力=−blank)のテスト</summary>
public class DosBlankFrameTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private static ChartProject NewProject(int blankFrame)
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, 150)], // 1拍=24frame
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 100,
            BlankFrame = blankFrame,
        };
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        tab.Lanes[0].Notes.Add(48 * T); // 内部frame = 100 + 24 = 124
        p.Tabs.Add(tab);
        return p;
    }

    [Fact]
    public void Export_AddsBlankFrameToDataFrames()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProject(200));
        Assert.Contains("=324", text);       // 124 + 200
        Assert.DoesNotContain("=124", text); // 内部値そのままは出力されない
        Assert.Contains("|blankFrame=200|", text);
    }

    [Fact]
    public void RoundTrip_WithBlankFrame_RestoresTicks()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProject(200), includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.Contains(48 * T, back.Project.Tabs[0].Lanes[0].Notes);
        Assert.Equal(200, back.Project.BlankFrame);
        Assert.Equal(100, back.Project.StartNumber, 3);
    }

    [Fact]
    public void RoundTrip_ZeroBlankFrame_Unchanged()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProject(0), includeEditorMetadata: true);
        Assert.Contains("=124", text);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.Contains(48 * T, back.Project.Tabs[0].Lanes[0].Notes);
    }
}
