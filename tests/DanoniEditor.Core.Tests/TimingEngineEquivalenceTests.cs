using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// TimingEngineの二分探索・事前計算版が、従来の線形走査実装(ここに参照実装として保持)と
/// 同じ結果を返すことをランダム入力で検証する(2026-10-05)。
/// </summary>
public class TimingEngineEquivalenceTests
{
    private static long RefMeasureStartTick(IReadOnlyList<TimeSignatureEvent> sigs, int measureIndex)
    {
        long tick = 0; int m = 0;
        for (int i = 0; i < sigs.Count && m < measureIndex; i++)
        {
            int segEnd = i + 1 < sigs.Count ? Math.Min(sigs[i + 1].MeasureIndex, measureIndex) : measureIndex;
            tick += (long)(segEnd - m) * sigs[i].TicksPerMeasure;
            m = segEnd;
        }
        return tick;
    }

    private static (int, long) RefTickToMeasurePosition(IReadOnlyList<TimeSignatureEvent> sigs, long tick)
    {
        long cursor = 0; int measure = 0;
        for (int i = 0; i < sigs.Count; i++)
        {
            var sig = sigs[i];
            long next = i + 1 < sigs.Count ? sigs[i + 1].MeasureIndex : int.MaxValue;
            while (measure < next)
            {
                if (tick < cursor + sig.TicksPerMeasure) return (measure, tick - cursor);
                cursor += sig.TicksPerMeasure; measure++;
            }
        }
        return (measure, tick - cursor);
    }

    [Fact]
    public void MeasureFunctions_MatchLinearReference_OnRandomSignatures()
    {
        var rnd = new Random(12345);
        int[] dens = [1, 2, 4, 8, 16];
        for (int trial = 0; trial < 200; trial++)
        {
            var sigs = new List<TimeSignatureEvent>();
            int measure = rnd.Next(0, 2); // 0始まりでない場合は既定4/4が挿入される
            int count = rnd.Next(0, 6);
            for (int i = 0; i < count; i++)
            {
                sigs.Add(new TimeSignatureEvent(measure, rnd.Next(1, 9), dens[rnd.Next(dens.Length)]));
                measure += rnd.Next(0, 5); // 0を含む(同一小節の重複イベント)
            }
            var engine = new TimingEngine(0, [new BpmEvent(0, 120)], sigs);
            var effective = engine.TimeSignatures;

            for (int m = 0; m < 40; m++)
                Assert.Equal(RefMeasureStartTick(effective, m), engine.MeasureStartTick(m));

            long limit = RefMeasureStartTick(effective, 40) + 5000;
            for (int k = 0; k < 200; k++)
            {
                long tick = rnd.NextInt64(0, limit);
                Assert.Equal(RefTickToMeasurePosition(effective, tick), engine.TickToMeasurePosition(tick));
            }
        }
    }

    [Fact]
    public void TickToFrame_MatchesPiecewiseLinearReference()
    {
        var rnd = new Random(777);
        for (int trial = 0; trial < 100; trial++)
        {
            var bpms = new List<BpmEvent> { new(0, 60 + rnd.Next(0, 180)) };
            long t = 0;
            for (int i = 0; i < rnd.Next(0, 8); i++)
            {
                t += rnd.Next(1, 20) * 1680L;
                bpms.Add(new BpmEvent(t, 60 + rnd.Next(0, 180)));
            }
            double start = rnd.Next(-100, 100);
            var engine = new TimingEngine(start, bpms);

            for (int k = 0; k < 100; k++)
            {
                long tick = rnd.NextInt64(-5000, t + 50000);
                double expected = start;
                for (int i = 0; i < bpms.Count; i++)
                {
                    long segStart = bpms[i].Tick;
                    long segEnd = i + 1 < bpms.Count ? bpms[i + 1].Tick : long.MaxValue;
                    if (i == 0 && tick < 0) { expected += (double)tick / 1680 * (3600 / bpms[0].Bpm); break; }
                    long upTo = Math.Min(tick, segEnd);
                    if (upTo > segStart) expected += (double)(upTo - segStart) / 1680 * (3600 / bpms[i].Bpm);
                    if (tick <= segEnd) break;
                }
                Assert.Equal(expected, engine.TickToFrame(tick), 6);
            }
        }
    }

    [Fact]
    public void BpmEvents_CannotBeMutatedThroughCast()
    {
        var engine = new TimingEngine(0, [new BpmEvent(0, 120)]);
        Assert.False(engine.BpmEvents is List<BpmEvent>);
        Assert.False(engine.TimeSignatures is List<TimeSignatureEvent>);
    }
}
