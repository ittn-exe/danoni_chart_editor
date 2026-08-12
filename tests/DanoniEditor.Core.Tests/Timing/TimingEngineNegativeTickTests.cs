using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Timing;

/// <summary>2026-08-08新設の回帰テスト: TickToFrameのtick&lt;0境界条件の修正。
/// 「speed/boostのマイナスフレームを許容する」表示設定(ChartProject.AllowNegativeFrameSpeedBoost)
/// の追加を受けて、実際にtick&lt;0へ置いたオブジェクトがdos.txt上で正しい負のフレームとして
/// 出力されるかを検証する。修正前はtick&lt;0が一律starts[0](tick=0のフレーム)へ潰れていた。</summary>
public class TimingEngineNegativeTickTests
{
    [Fact]
    public void TickToFrame_NegativeTick_ExtrapolatesLinearly_NotClampedToTickZero()
    {
        // BPM120、tick0のフレームは200(StartNumber)。1拍=1680tick=3600/120=30フレーム。
        var engine = new TimingEngine(200, [new BpmEvent(0, 120)]);

        double frameAt0 = engine.TickToFrame(0);
        double frameAtMinus1Beat = engine.TickToFrame(-TimingEngine.TicksPerBeat);
        double frameAtMinus2Beat = engine.TickToFrame(-2 * TimingEngine.TicksPerBeat);

        Assert.Equal(200, frameAt0);
        // 修正前は両方とも200(starts[0])に潰れていた。修正後は1拍=30フレーム分ずつ負方向へ進む。
        Assert.Equal(170, frameAtMinus1Beat, precision: 6);
        Assert.Equal(140, frameAtMinus2Beat, precision: 6);
    }

    [Fact]
    public void TickToFrame_NegativeTick_DoesNotCollapseDistinctTicksToSameFrame()
    {
        var engine = new TimingEngine(0, [new BpmEvent(0, 120)]);

        double a = engine.TickToFrame(-100);
        double b = engine.TickToFrame(-5000);

        // 修正前はどちらもstarts[0]=0に潰れて一致してしまっていた。
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TickToFrame_And_FrameToTick_AreSymmetric_ForNegativeValues()
    {
        var engine = new TimingEngine(200, [new BpmEvent(0, 145.5)]);

        // FrameToTickは元々負のframeを正しく外挿する実装だったため、これを基準に往復一致を確認する。
        double tick = engine.FrameToTick(-300);
        double roundTrippedFrame = engine.TickToFrame((long)Math.Round(tick));

        Assert.Equal(-300, roundTrippedFrame, precision: 1);
    }

    [Fact]
    public void TickToFrame_NegativeTick_WithMultipleBpmSegments_UsesFirstSegmentBpm()
    {
        // tick 0とtick 1680(BPM変化)の2区間。tick<0は常に先頭区間(tick0、BPM100)の速さで外挿されるべき。
        var engine = new TimingEngine(0, [new BpmEvent(0, 100), new BpmEvent(TimingEngine.TicksPerBeat, 200)]);

        double framesPerBeatAtBpm100 = TimingEngine.FramesPerMinute / 100; // 36フレーム/拍
        double expected = -framesPerBeatAtBpm100;

        Assert.Equal(expected, engine.TickToFrame(-TimingEngine.TicksPerBeat), precision: 6);
    }
}
