namespace DanoniEditor.Core.Audio;

/// <summary>
/// 波形表示用のピークキャッシュ(2026-07-18、要望メモ07-15項目7)。
/// モノラル化した音声サンプルを一定分解能(既定: 1/4フレーム=240分割/秒)のバケットに分け、
/// 各バケットの最小/最大振幅を保持する。描画時は「表示ピクセル行が覆うフレーム範囲」で
/// QueryPeakを呼び、その範囲の最小/最大を集計して描く(visible-range cullingは呼び出し側)。
/// WPF非依存・不変データのためスレッド安全(バックグラウンドデコード後に差し替える運用)。
/// </summary>
public sealed class WaveformPeaks
{
    /// <summary>1秒あたりのバケット数(60fps×16=1/16フレーム分解能)。
    /// 2026-07-18b: 240(1/4フレーム)では高ズーム時に波形が潰れて見づらいとの指摘で960へ引き上げ。
    /// メモリは5分曲で約2.3MB(960×300秒×min/max×4byte)と軽微。</summary>
    public const int BucketsPerSecond = 960;

    private readonly float[] _min;
    private readonly float[] _max;

    /// <summary>音声全体の長さ(フレーム、60fps基準)</summary>
    public double TotalFrames => _min.Length * 60.0 / BucketsPerSecond;

    public int BucketCount => _min.Length;

    private WaveformPeaks(float[] min, float[] max)
    {
        _min = min;
        _max = max;
    }

    /// <summary>モノラルサンプル列(-1..1)からピークキャッシュを構築する。
    /// ステレオ等は呼び出し側でモノラル化(平均)してから渡すこと。</summary>
    public static WaveformPeaks FromSamples(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        double samplesPerBucket = (double)sampleRate / BucketsPerSecond;
        int buckets = Math.Max(1, (int)Math.Ceiling(mono.Length / samplesPerBucket));
        var min = new float[buckets];
        var max = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            int s0 = (int)(b * samplesPerBucket);
            int s1 = Math.Min(mono.Length, (int)((b + 1) * samplesPerBucket));
            float lo = 0, hi = 0; // サンプルが無い端は無音扱い
            for (int i = s0; i < s1; i++)
            {
                var v = mono[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            min[b] = lo;
            max[b] = hi;
        }
        return new WaveformPeaks(min, max);
    }

    /// <summary>フレーム範囲[frameStart, frameEnd)の最小/最大振幅。範囲外は無音(0,0)。</summary>
    public (float Min, float Max) QueryPeak(double frameStart, double frameEnd)
    {
        if (frameEnd < frameStart) (frameStart, frameEnd) = (frameEnd, frameStart);
        if (frameEnd <= 0) return (0, 0); // 音声開始前(負フレーム)は無音
        int b0 = (int)Math.Floor(frameStart * BucketsPerSecond / 60.0);
        int b1 = (int)Math.Ceiling(frameEnd * BucketsPerSecond / 60.0);
        b0 = Math.Max(0, b0);
        b1 = Math.Min(_min.Length, Math.Max(b0 + 1, b1));
        if (b0 >= _min.Length) return (0, 0);
        float lo = 0, hi = 0;
        for (int b = b0; b < b1; b++)
        {
            if (_min[b] < lo) lo = _min[b];
            if (_max[b] > hi) hi = _max[b];
        }
        return (lo, hi);
    }
}
