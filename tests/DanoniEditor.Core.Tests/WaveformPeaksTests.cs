using DanoniEditor.Core.Audio;

namespace DanoniEditor.Core.Tests;

/// <summary>波形ピークキャッシュのテスト(2026-07-18)</summary>
public class WaveformPeaksTests
{
    [Fact]
    public void FromSamples_And_Query_BasicPeaks()
    {
        // 48kHz・1秒。前半0.5秒は±0.5の矩形波、後半は無音
        const int rate = 48000;
        var mono = new float[rate];
        for (int i = 0; i < rate / 2; i++) mono[i] = i % 2 == 0 ? 0.5f : -0.5f;

        var peaks = WaveformPeaks.FromSamples(mono, rate);

        Assert.Equal(60.0, peaks.TotalFrames, 1); // 1秒=60frame

        var (lo1, hi1) = peaks.QueryPeak(0, 30);   // 前半
        Assert.Equal(-0.5f, lo1, 3);
        Assert.Equal(0.5f, hi1, 3);

        var (lo2, hi2) = peaks.QueryPeak(31, 60);  // 後半は無音
        Assert.Equal(0f, lo2, 3);
        Assert.Equal(0f, hi2, 3);
    }

    [Fact]
    public void Query_OutOfRange_ReturnsSilence()
    {
        var peaks = WaveformPeaks.FromSamples(new float[48000], 48000);
        var (lo, hi) = peaks.QueryPeak(100000, 100010);
        Assert.Equal(0f, lo);
        Assert.Equal(0f, hi);
    }

    [Fact]
    public void Query_SubFrameResolution()
    {
        // 1/4フレーム分解能: 先頭バケット(0〜0.25frame相当=200サンプル)だけ振幅1
        const int rate = 48000;
        var mono = new float[rate];
        for (int i = 0; i < rate / WaveformPeaks.BucketsPerSecond; i++) mono[i] = 1.0f;

        var peaks = WaveformPeaks.FromSamples(mono, rate);
        Assert.Equal(1.0f, peaks.QueryPeak(0, 0.25).Max, 3);
        Assert.Equal(0f, peaks.QueryPeak(0.5, 1.0).Max, 3);
    }
}
