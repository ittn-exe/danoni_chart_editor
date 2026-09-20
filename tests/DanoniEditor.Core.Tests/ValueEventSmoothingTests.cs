using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>speed/boostの「始点終点オートスムージング出力」(2026-07-30要望対応)のテスト。
/// ValueEventSmoothing.ExpandLinkedEventsの補間ロジック本体、およびDosExporter出力への反映を検証する。</summary>
public class ValueEventSmoothingTests
{
    private const long Beat = TimingEngine.TicksPerBeat; // 4分音符1つぶんのtick数

    [Fact]
    public void ExpandLinkedEvents_NoLink_ReturnsEventsUnchangedInTickOrder()
    {
        var events = new List<ValueEvent> { new(Beat * 4, 2.0), new(0, 1.0) };

        var result = ValueEventSmoothing.ExpandLinkedEvents(events);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Tick);
        Assert.Equal(Beat * 4, result[1].Tick);
    }

    [Fact]
    public void ExpandLinkedEvents_Linked4th_GeneratesThreeIntermediatePointsWithLinearValues()
    {
        // 0(値1.0)と4拍後(値2.0)を4分間隔でリンク → 中間点は1拍後/2拍後/3拍後の3点
        var events = new List<ValueEvent> { new(0, 1.0, LinkGridDivision: 4), new(Beat * 4, 2.0) };

        var result = ValueEventSmoothing.ExpandLinkedEvents(events);

        Assert.Equal(5, result.Count); // 始点+中間3点+終点
        Assert.Equal(0, result[0].Tick);
        Assert.Equal(Beat * 1, result[1].Tick);
        Assert.Equal(1.25, result[1].Value, 3);
        Assert.Equal(Beat * 2, result[2].Tick);
        Assert.Equal(1.50, result[2].Value, 3);
        Assert.Equal(Beat * 3, result[3].Tick);
        Assert.Equal(1.75, result[3].Value, 3);
        Assert.Equal(Beat * 4, result[4].Tick);
        Assert.Equal(2.0, result[4].Value, 3);
    }

    [Fact]
    public void ExpandLinkedEvents_Linked8th_GeneratesFinerIntermediatePoints()
    {
        // 8分間隔=Beat/2ごとの中間点(0〜2拍の間で1点のみ、8分刻みだと3点)
        var events = new List<ValueEvent> { new(0, 0.0, LinkGridDivision: 8), new(Beat * 2, 2.0) };

        var result = ValueEventSmoothing.ExpandLinkedEvents(events);

        Assert.Equal(5, result.Count); // 始点+中間3点(0.5,1.0,1.5拍)+終点
        Assert.Equal((long)(Beat * 0.5), result[1].Tick);
        Assert.Equal(0.5, result[1].Value, 3);
        Assert.Equal(Beat * 1, result[2].Tick);
        Assert.Equal(1.0, result[2].Value, 3);
    }

    [Fact]
    public void ExpandLinkedEvents_LinkOnLastEvent_IsIgnored()
    {
        // 最後のイベントにLinkGridDivisionが付いていても、その先に「次」が無いため無視される
        var events = new List<ValueEvent> { new(0, 1.0), new(Beat * 4, 2.0, LinkGridDivision: 4) };

        var result = ValueEventSmoothing.ExpandLinkedEvents(events);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExpandLinkedEvents_ChainedLinks_ExpandsBothSegments()
    {
        // 3点連続リンク(0→4拍→8拍)。両区間とも中間点が生成される。
        var events = new List<ValueEvent>
        {
            new(0, 0.0, LinkGridDivision: 4),
            new(Beat * 4, 4.0, LinkGridDivision: 4),
            new(Beat * 8, 0.0),
        };

        var result = ValueEventSmoothing.ExpandLinkedEvents(events);

        Assert.Equal(3 + 3 + 3, result.Count); // 元3点 + 各区間中間3点ずつ
        Assert.Contains(result, e => e.Tick == Beat * 2 && Math.Abs(e.Value - 2.0) < 0.001);
        Assert.Contains(result, e => e.Tick == Beat * 6 && Math.Abs(e.Value - 2.0) < 0.001);
    }

    [Fact]
    public void ExpandLinkedEvents_Empty_ReturnsEmpty()
    {
        Assert.Empty(ValueEventSmoothing.ExpandLinkedEvents([]));
    }

    // =====================================================================
    // ExpandLinkedBpmEvents(BPMの始点終点リンク、直線ランプ、2026-08-23要望対応)
    // SKB/FUJIエクスポート(離散BPMしか扱えない外部形式)向けの分解専用。TimingEngine自体は
    // 対数/指数の解析解を使うため、このメソッドの結果に依存しない(TimingEngineBpmLinkTests参照)。
    // =====================================================================

    [Fact]
    public void ExpandLinkedBpmEvents_NoLink_ReturnsEventsUnchangedInTickOrder()
    {
        var events = new List<BpmEvent> { new(Beat * 4, 200), new(0, 100) };

        var result = ValueEventSmoothing.ExpandLinkedBpmEvents(events);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Tick);
        Assert.Equal(Beat * 4, result[1].Tick);
    }

    [Fact]
    public void ExpandLinkedBpmEvents_Linked4th_GeneratesThreeIntermediatePointsWithLinearBpm()
    {
        var events = new List<BpmEvent> { new(0, 100, LinkGridDivision: 4), new(Beat * 4, 200) };

        var result = ValueEventSmoothing.ExpandLinkedBpmEvents(events);

        Assert.Equal(5, result.Count);
        Assert.Equal(Beat * 1, result[1].Tick);
        Assert.Equal(125, result[1].Bpm, 3);
        Assert.Equal(Beat * 2, result[2].Tick);
        Assert.Equal(150, result[2].Bpm, 3);
        Assert.Equal(Beat * 3, result[3].Tick);
        Assert.Equal(175, result[3].Bpm, 3);
    }

    [Fact]
    public void ExpandLinkedBpmEvents_LinkOnLastEvent_IsIgnored()
    {
        var events = new List<BpmEvent> { new(0, 100), new(Beat * 4, 200, LinkGridDivision: 4) };

        var result = ValueEventSmoothing.ExpandLinkedBpmEvents(events);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExpandLinkedBpmEvents_GeneratedIntermediatePoints_HaveNoFrameAnchorOrLink()
    {
        var events = new List<BpmEvent> { new(0, 100, LinkGridDivision: 4), new(Beat * 4, 200) };

        var result = ValueEventSmoothing.ExpandLinkedBpmEvents(events);
        var intermediate = result[1];

        Assert.Null(intermediate.FrameAnchor);
        Assert.Null(intermediate.LinkGridDivision);
    }

    [Fact]
    public void ExpandLinkedBpmEvents_Empty_ReturnsEmpty()
    {
        Assert.Empty(ValueEventSmoothing.ExpandLinkedBpmEvents([]));
    }

    [Fact]
    public void Export_LinkedSpeedEvents_IncludesInterpolatedFramesInSpeedData()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "Normal"));
        project.Tabs[0].SpeedEvents = [new ValueEvent(0, 1.0, LinkGridDivision: 4), new ValueEvent(Beat * 4, 2.0)];

        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|speed_data=", text);
        // BPM120: 1拍(Beat tick)=30frame。中間点は30,60,90frame目に生成されるはず。
        Assert.Contains("30,1.25", text);
        Assert.Contains("60,1.5", text);
        Assert.Contains("90,1.75", text);
    }
}
