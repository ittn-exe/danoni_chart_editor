using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Timing;

/// <summary>2026-08-23新設: BpmEvent.LinkGridDivision(BPMの始点終点リンク、直線ランプ)の回帰テスト。
/// tick位置に対してBPMが直線的に変化する区間で、TickToFrame/FrameToTickが対数/指数の解析解により
/// 正確な値を返すことを検証する(近似ではなく厳密な往復一致を確認する)。</summary>
public class TimingEngineBpmLinkTests
{
    [Fact]
    public void TickToFrame_LinkedSegment_MatchesAnalyticEndpoints()
    {
        // BPM100→BPM200へ、tick0からtick1680(1拍)にかけて直線的にランプする区間。
        // 区間の両端(tick0とtick1680)では、リンク無しの通常計算と一致するはず。
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(0, [
            new BpmEvent(0, 100, LinkGridDivision: 16),
            new BpmEvent(beat, 200),
        ]);

        double frameAt0 = engine.TickToFrame(0);
        double frameAtEnd = engine.TickToFrame(beat);

        Assert.Equal(0, frameAt0, precision: 6);
        // frame(t1)-frame(t0) = (FramesPerMinute/TicksPerBeat) * ln(200/100) / ((200-100)/1680)
        double expected = (TimingEngine.FramesPerMinute / beat) * Math.Log(2.0) / (100.0 / beat);
        Assert.Equal(expected, frameAtEnd, precision: 6);
    }

    [Fact]
    public void TickToFrame_LinkedSegment_IsStrictlyMonotonicAndBetweenConstantBpmBounds()
    {
        // ランプ中の中間tickのフレーム値は、区間開始/終了それぞれのBPMを一定と仮定した場合の
        // フレーム値の間に収まるはず(BPMが単調増加するランプでは、経過フレームは
        // 「終端BPM一定」より多く「始端BPM一定」より少なくなる)。
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(0, [
            new BpmEvent(0, 100, LinkGridDivision: 16),
            new BpmEvent(beat, 200),
        ]);

        long midTick = beat / 2;
        double frameAtMid = engine.TickToFrame(midTick);

        double lowerBound = TimingEngine.FramesPerMinute / 200 / beat * midTick; // 終端BPM一定なら最短
        double upperBound = TimingEngine.FramesPerMinute / 100 / beat * midTick; // 始端BPM一定なら最長

        Assert.True(frameAtMid > lowerBound, $"{frameAtMid} should be > {lowerBound}");
        Assert.True(frameAtMid < upperBound, $"{frameAtMid} should be < {upperBound}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(840)]
    [InlineData(1679)]
    [InlineData(1680)]
    public void TickToFrame_And_FrameToTick_AreSymmetric_WithinLinkedSegment(long tick)
    {
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(500, [
            new BpmEvent(0, 90, LinkGridDivision: 8),
            new BpmEvent(beat, 210),
        ]);

        double frame = engine.TickToFrame(tick);
        double roundTrippedTick = engine.FrameToTick(frame);

        Assert.Equal(tick, roundTrippedTick, precision: 6);
    }

    [Fact]
    public void TickToFrame_LinkedSegment_EqualEndpointBpm_FallsBackToLinearFormula()
    {
        // 両端が同じBPM(実質リンク無しと等価)の場合、対数計算(底が1になり定義不能)を避けて
        // 通常の一定BPM線形式にフォールバックすることを確認する。
        long beat = TimingEngine.TicksPerBeat;
        var linked = new TimingEngine(0, [
            new BpmEvent(0, 150, LinkGridDivision: 16),
            new BpmEvent(beat, 150),
        ]);
        var unlinked = new TimingEngine(0, [new BpmEvent(0, 150)]);

        long midTick = beat / 3;
        Assert.Equal(unlinked.TickToFrame(midTick), linked.TickToFrame(midTick), precision: 6);
    }

    [Fact]
    public void TickToFrame_MultipleLinkedSegmentsInSequence_ChainCorrectly()
    {
        // 100→200(tick0〜1680)、200→100(tick1680〜3360)と、2つの連続したランプ区間。
        // 2つ目の区間の開始フレームは1つ目の区間の解析解の到達点と一致するはず。
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(0, [
            new BpmEvent(0, 100, LinkGridDivision: 16),
            new BpmEvent(beat, 200, LinkGridDivision: 16),
            new BpmEvent(beat * 2, 100),
        ]);

        double frameAtBoundary = engine.TickToFrame(beat);
        double frameAtEnd = engine.TickToFrame(beat * 2);

        double expectedBoundary = (TimingEngine.FramesPerMinute / beat) * Math.Log(2.0) / (100.0 / beat);
        Assert.Equal(expectedBoundary, frameAtBoundary, precision: 6);

        // 2区間目(200→100への下降ランプ)の解析解を境界フレームへ加算した値と一致するはず。
        double expectedSecondSpan = (TimingEngine.FramesPerMinute / beat) * Math.Log(100.0 / 200.0) / (-100.0 / beat);
        Assert.Equal(expectedBoundary + expectedSecondSpan, frameAtEnd, precision: 6);
    }

    [Fact]
    public void TickToFrame_UnlinkedEvent_BehavesExactlyAsBefore()
    {
        // LinkGridDivisionを設定しない既存の挙動(区分定数BPM)が変わっていないことの回帰確認。
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(0, [new BpmEvent(0, 100), new BpmEvent(beat, 200)]);

        double framesPerBeatAtBpm100 = TimingEngine.FramesPerMinute / 100;
        Assert.Equal(framesPerBeatAtBpm100, engine.TickToFrame(beat), precision: 6);
    }

    [Fact]
    public void TickToFrame_LinkGridDivisionOnLastEvent_IsIgnored()
    {
        // 最後のBPMイベントにLinkGridDivisionが設定されていても(次が存在しないため)無視され、
        // 通常の一定BPM外挿として扱われる(クラッシュしないことも含めて確認)。
        long beat = TimingEngine.TicksPerBeat;
        var engine = new TimingEngine(0, [new BpmEvent(0, 100), new BpmEvent(beat, 200, LinkGridDivision: 16)]);

        double framesPerBeatAtBpm200 = TimingEngine.FramesPerMinute / 200;
        Assert.Equal(
            engine.TickToFrame(beat) + framesPerBeatAtBpm200,
            engine.TickToFrame(beat * 2), precision: 6);
    }
}
