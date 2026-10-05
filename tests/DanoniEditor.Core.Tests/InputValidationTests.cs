using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Tests.Editing;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests;

/// <summary>外部入力(読み込みファイル)由来の不正値でクラッシュ・無限ループしないことの検証(2026-10-05)。</summary>
public class InputValidationTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(-120.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TimingEngine_RejectsInvalidBpm(double bpm)
    {
        Assert.Throws<ArgumentException>(() => new TimingEngine(0, [new BpmEvent(0, bpm)]));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-3, 4)]
    [InlineData(4, -4)]
    public void TimingEngine_RejectsInvalidTimeSignature(int num, int den)
    {
        Assert.Throws<ArgumentException>(() =>
            new TimingEngine(0, [new BpmEvent(0, 120)], [new TimeSignatureEvent(0, num, den)]));
    }

    [Fact]
    public void TimingEngine_RejectsNonFiniteStartNumber()
    {
        Assert.Throws<ArgumentException>(() => new TimingEngine(double.NaN, [new BpmEvent(0, 120)]));
    }

    [Fact]
    public void TimingEngine_ValidInput_StillWorks()
    {
        var e = new TimingEngine(0, [new BpmEvent(0, 120)], [new TimeSignatureEvent(0, 3, 4)]);
        Assert.Equal(3L * TimingEngine.TicksPerBeat, e.MeasureStartTick(1));
    }

    [Fact]
    public void Deserialize_ZeroNumeratorTimeSignature_IsRejectedInsteadOfLooping()
    {
        var project = new ChartProject
        {
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 0, 4)],
        };
        var json = ProjectSerializer.Serialize(project);
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_NegativeBpm_IsRejected()
    {
        var project = new ChartProject { BpmEvents = [new BpmEvent(0, -1)] };
        var json = ProjectSerializer.Serialize(project);
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_HugeNoteTick_IsRejected()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject();
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        tab.Lanes[0].Notes.Add(long.MaxValue - 5);
        project.Tabs.Add(tab);
        var json = ProjectSerializer.Serialize(project);
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_ValidProject_RoundTrips()
    {
        var project = new ChartProject();
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Single(back.BpmEvents);
    }

    [Fact]
    public void DosImport_InvalidDeBpm_FallsBackToDefaultWithWarning()
    {
        var repo = TestFixtures.Repository();
        var text = "|difData=5,Normal,3.5|\n|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,0|\n";
        var r = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.Contains(r.Warnings, w => w.Contains("タイミング"));
        Assert.All(r.Project.BpmEvents, b => Assert.True(b.Bpm > 0));
    }

    [Fact]
    public void DosImport_GarbageDeBpm_FallsBackToDefaultWithWarning()
    {
        var repo = TestFixtures.Repository();
        var text = "|difData=5,Normal,3.5|\n|de_schemaVersion=1|\n|de_startNumber=abc|\n|de_bpm=x,y|\n";
        var r = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.Contains(r.Warnings, w => w.Contains("タイミング"));
    }

    [Fact]
    public void DosImport_ZeroTimeSignature_FallsBackToDefault()
    {
        var repo = TestFixtures.Repository();
        var text = "|difData=5,Normal,3.5|\n|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n|de_timeSig=0,0,4|\n";
        var r = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.All(r.Project.TimeSignatures, s => Assert.True(s.Numerator > 0));
    }

    private const string SkbBase = """
    {
      "keyKind": "5",
      "scores": [ { "notes": [[0],[],[],[],[]], "freezes": [[],[],[],[],[]], "speeds": [] } ],
      "blankFrame": 0,
      "timings": [ TIMINGS ],
      "scoreNumber": 1,
      "scorePrefix": ""
    }
    """;

    [Fact]
    public void SkbImport_NonMonotonicTimingPosition_IsIgnoredWithWarning()
    {
        var repo = TestFixtures.Repository();
        var json = SkbBase.Replace("TIMINGS",
            "{\"label\":1,\"startNum\":0,\"bpm\":120,\"pageBlockNum\":8}," +
            "{\"label\":3,\"startNum\":500,\"bpm\":140,\"pageBlockNum\":8}," +
            "{\"label\":2,\"startNum\":900,\"bpm\":150,\"pageBlockNum\":8}");
        var r = new SkbImporter(repo.Get).Import(json);
        Assert.Equal(2, r.BpmEvents.Count);
        Assert.Contains(r.Warnings, w => w.Contains("label=2"));
    }

    [Fact]
    public void SkbImport_InvalidBpm_IsIgnoredWithWarning()
    {
        var repo = TestFixtures.Repository();
        var json = SkbBase.Replace("TIMINGS",
            "{\"label\":1,\"startNum\":0,\"bpm\":120,\"pageBlockNum\":8}," +
            "{\"label\":3,\"startNum\":500,\"bpm\":0,\"pageBlockNum\":8}");
        var r = new SkbImporter(repo.Get).Import(json);
        Assert.Single(r.BpmEvents);
        Assert.Contains(r.Warnings, w => w.Contains("label=3"));
    }

    [Fact]
    public void SkbImport_InvalidFirstTiming_Throws()
    {
        var repo = TestFixtures.Repository();
        var json = SkbBase.Replace("TIMINGS", "{\"label\":1,\"startNum\":0,\"bpm\":0,\"pageBlockNum\":8}");
        Assert.Throws<InvalidDataException>(() => new SkbImporter(repo.Get).Import(json));
    }
}
