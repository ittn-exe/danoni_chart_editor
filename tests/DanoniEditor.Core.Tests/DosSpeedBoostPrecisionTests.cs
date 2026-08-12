using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>2026-08-08要望対応の回帰テスト: dos.txt出力時のspeed/boost値を小数第2位までに丸める
/// (DosExporter.NumValue)。他のパラメータ(BPM/StartNumber等)の精度には影響しないことも確認する。</summary>
public class DosSpeedBoostPrecisionTests
{
    private static ChartProject NewProjectWithSpeedBoost(double speedValue, double boostValue, double bpm = 123.456)
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, bpm)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 100,
            BlankFrame = 0,
        };
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        tab.SpeedEvents.Add(new ValueEvent(TimingEngine.TicksPerBeat, speedValue));
        tab.BoostEvents.Add(new ValueEvent(TimingEngine.TicksPerBeat, boostValue));
        p.Tabs.Add(tab);
        return p;
    }

    [Fact]
    public void Export_SpeedValueWithManyDecimals_RoundsToTwoDecimalPlaces()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithSpeedBoost(1.23456, 0.5));

        Assert.Contains(",1.23", text);
        Assert.DoesNotContain("1.23456", text);
    }

    [Fact]
    public void Export_BoostValueWithManyDecimals_RoundsToTwoDecimalPlaces()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithSpeedBoost(1.0, 0.678));

        Assert.Contains(",0.68", text); // 四捨五入(AwayFromZero)
        Assert.DoesNotContain("0.678", text);
    }

    [Fact]
    public void Export_SpeedValue_WholeNumber_DoesNotPadWithTrailingZeros()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithSpeedBoost(2.0, 1.0));

        // speed_dataは1イベントのみなので"frame,2"の形(行末または|の直前)になるはず。
        // "2.00"や"2.0"ではなく"2"のまま(Numと同じ末尾0省略スタイル)であることを確認する。
        var line = text.Split('\n').Single(l => l.Contains("speed_data="));
        var value = line[(line.IndexOf('=') + 1)..].Split(',')[1].TrimEnd('|', '`', ' ', '\r');
        Assert.Equal("2", value);
    }

    [Fact]
    public void Export_SpeedValue_TwoDecimalsExactly_IsUnaffected()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithSpeedBoost(1.25, 0.5));

        Assert.Contains(",1.25", text);
    }

    [Fact]
    public void Export_Bpm_PrecisionUnaffectedBySpeedBoostRounding()
    {
        // speed/boost専用の丸めがBPM等、他のパラメータへ波及していないことの回帰確認
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithSpeedBoost(1.0, 1.0, bpm: 123.456), includeEditorMetadata: true);

        Assert.Contains("123.456", text);
    }
}
