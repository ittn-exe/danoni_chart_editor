using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>tick分解能1680/拍化(2026-07-19g)のテスト: v1移行と5連・7連スナップ</summary>
public class TickResolutionMigrationTests
{
    [Fact]
    public void SchemaV1Project_IsMigratedTimes35()
    {
        // v1(48tick/拍)時代のプロジェクトJSON(手書きの最小構成)
        const string v1Json = """
        {
          "schemaVersion": 1,
          "project": {
            "projectName": "old",
            "bpmEvents": [ { "tick": 0, "bpm": 120 }, { "tick": 96, "bpm": 240 } ],
            "timeSignatures": [ { "measureIndex": 0, "numerator": 4, "denominator": 4 } ],
            "markers": [ { "tick": 192, "comment": "m" } ],
            "tabs": [ {
              "difficultyName": "N", "keyTypeId": "5",
              "lanes": [
                { "notes": [48], "freezes": [ { "startTick": 96, "endTick": 144 } ] },
                { "notes": [], "freezes": [] }, { "notes": [], "freezes": [] },
                { "notes": [], "freezes": [] }, { "notes": [], "freezes": [] }
              ],
              "speedEvents": [ { "tick": 48, "value": 2.0 } ],
              "boostEvents": []
            } ]
          }
        }
        """;
        var p = ProjectSerializer.Deserialize(v1Json);
        Assert.Contains(48L * 35, p.Tabs[0].Lanes[0].Notes);
        var f = Assert.Single(p.Tabs[0].Lanes[0].Freezes);
        Assert.Equal(96L * 35, f.StartTick);
        Assert.Equal(144L * 35, f.EndTick);
        Assert.Contains(p.BpmEvents, e => e.Tick == 96L * 35 && e.Bpm == 240);
        Assert.Contains(p.Project_MarkersProxy(), m => m.Tick == 192L * 35);
        Assert.Contains(p.Tabs[0].SpeedEvents, e => e.Tick == 48L * 35);
    }

    [Fact]
    public void QuintupletAndSeptupletGrids_AreIntegerTicks()
    {
        var snap = new SnapService();
        foreach (var (div, expected) in new[] { (20, 336L), (28, 240L), (40, 168L), (56, 120L) })
        {
            snap.Division = div;
            Assert.Equal(expected, snap.GridTicks);
            Assert.Equal(0, 4L * TimingEngine.TicksPerBeat % snap.GridTicks); // 小節を割り切る
        }
        Assert.Contains(20, SnapService.Divisions);
        Assert.Contains(28, SnapService.Divisions);
        Assert.Contains(40, SnapService.Divisions);
        Assert.Contains(56, SnapService.Divisions);
    }

    [Fact]
    public void QuintupletPositions_HaveExactFrames()
    {
        // BPM120: 1拍=30frame。拍5連(20分)の各位置は6frame刻みの厳密値になる
        var engine = new TimingEngine(0, [new BpmEvent(0, 120)]);
        long grid = 4L * TimingEngine.TicksPerBeat / 20; // 336
        for (int i = 0; i < 5; i++)
            Assert.Equal(i * 6.0, engine.TickToFrame(i * grid), 9);
    }
}

/// <summary>テスト補助: ChartProject.Markersへの短縮アクセス(名前衝突回避)</summary>
internal static class ProjectTestExtensions
{
    public static List<Marker> Project_MarkersProxy(this ChartProject p) => p.Markers;
}
