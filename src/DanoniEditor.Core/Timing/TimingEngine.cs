namespace DanoniEditor.Core.Timing;

/// <summary>BPM変化点(仕様書7.3)。Tickは拍位置(48tick=4分音符1個)。
/// FrameAnchor(2026-07-17c追加): 通常はnull(前区間からの連続積算)。値がある場合、この区間の
/// 開始地点で絶対フレームをこの値へ強制的にリセットする。SKBエディタのtimings(区間ごとに
/// 独自のstartNumberを持つ「再同期」機能)を正確に再現するための拡張。</summary>
public sealed record BpmEvent(long Tick, double Bpm, double? FrameAnchor = null);

/// <summary>拍子変化点(仕様書7.5)。論理小節の頭にのみ配置される。</summary>
public sealed record TimeSignatureEvent(int MeasureIndex, int Numerator, int Denominator)
{
    /// <summary>この拍子の1小節あたりtick数(4分音符=48tick基準)</summary>
    public long TicksPerMeasure => Numerator * (4L * TimingEngine.TicksPerBeat) / Denominator;
}

/// <summary>
/// 拍(tick)⇔フレームの相互変換エンジン(仕様書7.2「拍/小節位置での内部管理」の中核)。
/// - 1拍(4分音符) = 1680tick(4/8/12/16/24/32/48/64分に加え20/28/40/56分も整数tickになる。2026-07-19g)
/// - フレームは60fps基準: 1拍のフレーム数 = 3600 / BPM
/// - StartNumber = tick 0(小節0の頭)が置かれる絶対フレーム(仕様書7.3、実体はblankFrame相当)
/// </summary>
public sealed class TimingEngine
{
    /// <summary>2026-07-19g: 48→1680へ引き上げ(5連符・7連符対応、ユーザー承認済みの案A)。
    /// 1680 = 48×35 = 4/8/12/16/24/32/48/64分と20/28/40/56分(拍5連・7連とその半分)の全てが
    /// 整数tickになる最小分解能。旧48tick基準のプロジェクトはProjectSerializerがv1→v2移行(×35)する。</summary>
    public const int TicksPerBeat = 1680;
    public const double FramesPerMinute = 3600; // 60fps × 60sec

    private readonly List<BpmEvent> _bpmEvents;
    private readonly List<TimeSignatureEvent> _timeSignatures;

    /// <summary>tick 0 の絶対フレーム位置(仕様書のStartNumber)</summary>
    public double StartNumber { get; }

    public IReadOnlyList<BpmEvent> BpmEvents => _bpmEvents;
    public IReadOnlyList<TimeSignatureEvent> TimeSignatures => _timeSignatures;

    public TimingEngine(double startNumber, IEnumerable<BpmEvent> bpmEvents,
                        IEnumerable<TimeSignatureEvent>? timeSignatures = null)
    {
        StartNumber = startNumber;
        _bpmEvents = bpmEvents.OrderBy(e => e.Tick).ToList();
        if (_bpmEvents.Count == 0 || _bpmEvents[0].Tick != 0)
            throw new ArgumentException("tick 0 に初期BPMイベントが必要です(仕様書7.3)");

        _timeSignatures = (timeSignatures ?? []).OrderBy(e => e.MeasureIndex).ToList();
        if (_timeSignatures.Count == 0 || _timeSignatures[0].MeasureIndex != 0)
            _timeSignatures.Insert(0, new TimeSignatureEvent(0, 4, 4)); // デフォルト4/4(仕様書7.5)
    }

    /// <summary>各BPM区間の開始tickにおける絶対フレーム値をあらかじめ計算しておく。
    /// 2026-07-17e: 従来はTickToFrame/FrameToTick内でその場積算しつつ「tickがちょうど区間境界と
    /// 一致する」ケースでbreak判定とアンカー適用の順序が噛み合わず、アンカーが適用されないまま
    /// 返ってしまう不具合があった(境界問い合わせはページ先頭を調べる最頻出パターンのため実害が大きい)。
    /// 区間ごとの開始フレームを先に確定させることで、境界のあいまいさを完全に排除する。</summary>
    private double[] ComputeSegmentStartFrames()
    {
        var starts = new double[_bpmEvents.Count];
        starts[0] = _bpmEvents[0].FrameAnchor ?? StartNumber;
        for (int i = 1; i < _bpmEvents.Count; i++)
        {
            if (_bpmEvents[i].FrameAnchor is { } anchor)
            {
                starts[i] = anchor;
            }
            else
            {
                long span = _bpmEvents[i].Tick - _bpmEvents[i - 1].Tick;
                starts[i] = starts[i - 1] + (double)span / TicksPerBeat * (FramesPerMinute / _bpmEvents[i - 1].Bpm);
            }
        }
        return starts;
    }

    /// <summary>拍位置(tick)→絶対フレーム。BPM区間ごとに区分線形で積算する。
    /// 2026-07-17c: 区間の先頭にFrameAnchorが明示されていれば、その区間へ進む際に積算をリセットする
    /// (SKBの「区間ごとに独自の絶対フレーム基準を持つ」再同期を正確に再現するため)。</summary>
    public double TickToFrame(long tick)
    {
        var starts = ComputeSegmentStartFrames();
        for (int i = _bpmEvents.Count - 1; i >= 0; i--)
        {
            if (tick >= _bpmEvents[i].Tick)
            {
                long span = tick - _bpmEvents[i].Tick;
                return starts[i] + (double)span / TicksPerBeat * (FramesPerMinute / _bpmEvents[i].Bpm);
            }
        }
        // 2026-08-08要望対応: tick<0(先頭BPMイベントより手前)は従来starts[0]を無条件に返しており、
        // どれだけ負のtickであっても同一フレームに潰れてしまっていた(speed/boostをtick<0へ配置しても
        // dos.txt上で正しい負のフレームにならない不具合)。FrameToTick(このメソッドの逆変換)は元々
        // 先頭区間を負方向へ正しく線形外挿する実装になっているため、それと対称になるよう
        // 先頭区間(tick0)の式をspanが負のまま適用する形にする。
        long span0 = tick - _bpmEvents[0].Tick;
        return starts[0] + (double)span0 / TicksPerBeat * (FramesPerMinute / _bpmEvents[0].Bpm);
    }

    /// <summary>絶対フレーム→拍位置(tick)。TickToFrameの逆変換(端数は実数tickで返す)。
    /// 2026-07-17c: FrameAnchor対応(TickToFrameと対称な扱い)。</summary>
    public double FrameToTick(double frame)
    {
        var starts = ComputeSegmentStartFrames();
        int active = 0;
        for (int i = 0; i < _bpmEvents.Count; i++)
        {
            if (frame >= starts[i])
                active = i;
        }
        double framesPerTick = FramesPerMinute / _bpmEvents[active].Bpm / TicksPerBeat;
        return _bpmEvents[active].Tick + (frame - starts[active]) / framesPerTick;
    }

    /// <summary>論理小節mの開始tick(拍子イベント列に従って積算)</summary>
    public long MeasureStartTick(int measureIndex)
    {
        long tick = 0;
        int m = 0;
        for (int i = 0; i < _timeSignatures.Count && m < measureIndex; i++)
        {
            int segEnd = i + 1 < _timeSignatures.Count
                ? Math.Min(_timeSignatures[i + 1].MeasureIndex, measureIndex)
                : measureIndex;
            tick += (long)(segEnd - m) * _timeSignatures[i].TicksPerMeasure;
            m = segEnd;
        }
        return tick;
    }

    /// <summary>tickがどの論理小節の何tick目かを返す</summary>
    public (int MeasureIndex, long TickInMeasure) TickToMeasurePosition(long tick)
    {
        long cursor = 0;
        int measure = 0;
        for (int i = 0; i < _timeSignatures.Count; i++)
        {
            var sig = _timeSignatures[i];
            long nextBoundaryMeasure = i + 1 < _timeSignatures.Count
                ? _timeSignatures[i + 1].MeasureIndex : int.MaxValue;
            while (measure < nextBoundaryMeasure)
            {
                if (tick < cursor + sig.TicksPerMeasure)
                    return (measure, tick - cursor);
                cursor += sig.TicksPerMeasure;
                measure++;
            }
        }
        return (measure, tick - cursor);
    }

    /// <summary>指定tickの時点で有効な拍子を返す</summary>
    public TimeSignatureEvent SignatureAt(long tick)
    {
        var (m, _) = TickToMeasurePosition(tick);
        TimeSignatureEvent current = _timeSignatures[0];
        foreach (var sig in _timeSignatures)
        {
            if (sig.MeasureIndex > m) break;
            current = sig;
        }
        return current;
    }
}
