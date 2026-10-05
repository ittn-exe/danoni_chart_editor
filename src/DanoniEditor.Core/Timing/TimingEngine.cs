namespace DanoniEditor.Core.Timing;

/// <summary>BPM変化点(仕様書7.3)。Tickは拍位置(48tick=4分音符1個)。
/// FrameAnchor(2026-07-17c追加): 通常はnull(前区間からの連続積算)。値がある場合、この区間の
/// 開始地点で絶対フレームをこの値へ強制的にリセットする。SKBエディタのtimings(区間ごとに
/// 独自のstartNumberを持つ「再同期」機能)を正確に再現するための拡張。
/// LinkGridDivision(2026-08-23要望対応、「BPMの始点終点リンク(直線ランプ)」): nullの場合は通常の
/// イベント(リンク無し、区間内は一定BPM)。非nullの場合、このイベントはtick順で直後(次)のBPM
/// イベントと自動的にリンクしており、両者の間はtick位置に対してBPMが直線的に変化する
/// (ValueEventのLinkGridDivisionと同じ命名・規約: 「tick順で早い方のイベントがLinkGridDivisionを
/// 持つ」形で表現する)。dos.txt本体はそもそもBPMという概念を持たず絶対フレーム値しか読まないため、
/// TimingEngine.TickToFrame/FrameToTickが対数/指数を用いた解析解でランプ区間を厳密に計算する
/// (中間点への分解は不要、近似ではなく正確な値になる)。設置間隔(4/8/16/32分)の値自体は
/// TickToFrame/FrameToTickの計算には使わず、SKBエディタ/FUJIエディタ向けエクスポート
/// (離散BPMしか扱えない外部形式)でランプを離散ステップへ分解する際の粒度としてのみ用いる。
/// dos.txtのde_bpm(エディタ独自の復元用メタデータ)は現状拡張しておらず、リンク情報は保持しない
/// (プロジェクトファイル(.json)経由の保存・読み込みでは完全に保持される)。</summary>
public sealed record BpmEvent(long Tick, double Bpm, double? FrameAnchor = null, int? LinkGridDivision = null);

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

    // 2026-10-05: 構築時に一度だけ計算するキャッシュ(従来はTickToFrame/FrameToTick/MeasureStartTick等の
    // 呼び出しのたびに区間開始フレームを再計算しており、描画ループから大量に呼ばれる際の負荷の主因だった)。
    // このクラスはイミュータブル(イベント列は構築後に変更されない)なので、キャッシュの無効化は不要。
    private readonly double[] _segmentStartFrames;
    private readonly long[] _sigStartTicks;

    // 外部からキャスト経由でイベント列を書き換えられないよう、読み取り専用ラッパーで公開する。
    public IReadOnlyList<BpmEvent> BpmEvents { get; }
    public IReadOnlyList<TimeSignatureEvent> TimeSignatures { get; }

    public TimingEngine(double startNumber, IEnumerable<BpmEvent> bpmEvents,
                        IEnumerable<TimeSignatureEvent>? timeSignatures = null)
    {
        if (!double.IsFinite(startNumber))
            throw new ArgumentException("StartNumberが有限の数値ではありません");
        StartNumber = startNumber;
        _bpmEvents = bpmEvents.OrderBy(e => e.Tick).ToList();
        if (_bpmEvents.Count == 0 || _bpmEvents[0].Tick != 0)
            throw new ArgumentException("tick 0 に初期BPMイベントが必要です(仕様書7.3)");

        _timeSignatures = (timeSignatures ?? []).OrderBy(e => e.MeasureIndex).ToList();

        // 不正値の検出(2026-10-05): BPM<=0/非数/無限大は0除算・無限ループ・NaN伝播の原因になり、
        // 拍子の分子/分母<=0は1小節のtick数が0以下になって小節探索が無限ループする。
        // 外部ファイル由来の値で描画ループやUIスレッドが固まらないよう、構築時に弾く。
        var problem = TimingValidator.FindProblem(_bpmEvents, _timeSignatures);
        if (problem is not null)
            throw new ArgumentException(problem);

        if (_timeSignatures.Count == 0 || _timeSignatures[0].MeasureIndex != 0)
            _timeSignatures.Insert(0, new TimeSignatureEvent(0, 4, 4)); // デフォルト4/4(仕様書7.5)
        BpmEvents = _bpmEvents.AsReadOnly();
        TimeSignatures = _timeSignatures.AsReadOnly();
        _segmentStartFrames = ComputeSegmentStartFrames();
        _sigStartTicks = ComputeSignatureStartTicks();
    }

    /// <summary>各BPM区間の開始tickにおける絶対フレーム値をあらかじめ計算しておく。
    /// 2026-07-17e: 従来はTickToFrame/FrameToTick内でその場積算しつつ「tickがちょうど区間境界と
    /// 一致する」ケースでbreak判定とアンカー適用の順序が噛み合わず、アンカーが適用されないまま
    /// 返ってしまう不具合があった(境界問い合わせはページ先頭を調べる最頻出パターンのため実害が大きい)。
    /// 区間ごとの開始フレームを先に確定させることで、境界のあいまいさを完全に排除する。
    /// 2026-08-23: 直前の区間がBPMリンク(LinkGridDivision)されている場合、区分定数の積算ではなく
    /// RampElapsedFrames(解析解)で積算する(FrameAnchorが優先される点は従来通り)。</summary>
    private long[] ComputeSignatureStartTicks()
    {
        var starts = new long[_timeSignatures.Count];
        for (int i = 1; i < starts.Length; i++)
        {
            long measures = (long)_timeSignatures[i].MeasureIndex - _timeSignatures[i - 1].MeasureIndex;
            starts[i] = starts[i - 1] + measures * _timeSignatures[i - 1].TicksPerMeasure;
        }
        return starts;
    }

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
            else if (_bpmEvents[i - 1].LinkGridDivision is { } div && div > 0)
            {
                starts[i] = starts[i - 1] + RampElapsedFrames(
                    _bpmEvents[i - 1].Tick, _bpmEvents[i - 1].Bpm, _bpmEvents[i].Tick, _bpmEvents[i].Bpm, _bpmEvents[i].Tick);
            }
            else
            {
                long span = _bpmEvents[i].Tick - _bpmEvents[i - 1].Tick;
                starts[i] = starts[i - 1] + (double)span / TicksPerBeat * (FramesPerMinute / _bpmEvents[i - 1].Bpm);
            }
        }
        return starts;
    }

    /// <summary>2026-08-23要望対応(BPMリンク): tick0(BPM=b0)からtick1(BPM=b1)まで、BPMがtick位置に
    /// 対してtick0から直線的に変化するランプ区間における、tick0からtick(区間内、外挿も可)までの
    /// 経過フレーム数を解析解で計算する。
    /// d(frame)/d(tick) = (FramesPerMinute/BPM(tick))/TicksPerBeatであり、BPM(tick)がtickの1次式
    /// (傾きk=(b1-b0)/(t1-t0))であることから、1/(1次式)の積分は対数になる:
    /// frame(t)-frame(t0) = (FramesPerMinute/TicksPerBeat) * ln(BPM(t)/b0) / k
    /// b0==b1(両端が同値、実質リンク無しと等価)の場合は対数の底が1になり定義できないため、
    /// 通常の一定BPM区間と同じ線形式にフォールバックする。</summary>
    private static double RampElapsedFrames(long t0, double b0, long t1, double b1, long t)
    {
        if (b0 == b1)
        {
            long span = t - t0;
            return (double)span / TicksPerBeat * (FramesPerMinute / b0);
        }
        double k = (b1 - b0) / (t1 - t0);
        double bpmAtT = b0 + k * (t - t0);
        return (FramesPerMinute / TicksPerBeat) * Math.Log(bpmAtT / b0) / k;
    }

    /// <summary>RampElapsedFramesの逆関数(2026-08-23、BPMリンクのFrameToTick用解析解)。
    /// 区間内で区間開始からframesElapsed経過した時点のtickを返す。
    /// frame(t)-frame(t0)=F とすると、BPM(t)=b0*exp(F*k*TicksPerBeat/FramesPerMinute)、
    /// t=t0+(BPM(t)-b0)/k で逆算できる。</summary>
    private static double RampTickAtFrames(long t0, double b0, long t1, double b1, double framesElapsed)
    {
        if (b0 == b1)
        {
            double framesPerTick = FramesPerMinute / b0 / TicksPerBeat;
            return t0 + framesElapsed / framesPerTick;
        }
        double k = (b1 - b0) / (t1 - t0);
        double bpmAtT = b0 * Math.Exp(framesElapsed * k * TicksPerBeat / FramesPerMinute);
        return t0 + (bpmAtT - b0) / k;
    }

    /// <summary>拍位置(tick)→絶対フレーム。BPM区間ごとに区分線形で積算する。
    /// 2026-07-17c: 区間の先頭にFrameAnchorが明示されていれば、その区間へ進む際に積算をリセットする
    /// (SKBの「区間ごとに独自の絶対フレーム基準を持つ」再同期を正確に再現するため)。
    /// 2026-08-23: 区間がBPMリンクされていれば、区分定数ではなくRampElapsedFrames(解析解)を使う。</summary>
    public double TickToFrame(long tick)
    {
        var starts = _segmentStartFrames;
        // tick以下で最後のBPMイベントを二分探索(イベント列はTick昇順)
        int lo = 0, hi = _bpmEvents.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (tick >= _bpmEvents[mid].Tick) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found >= 0)
        {
            int i = found;
            if (_bpmEvents[i].LinkGridDivision is { } div && div > 0 && i + 1 < _bpmEvents.Count)
            {
                return starts[i] + RampElapsedFrames(
                    _bpmEvents[i].Tick, _bpmEvents[i].Bpm, _bpmEvents[i + 1].Tick, _bpmEvents[i + 1].Bpm, tick);
            }
            long span = tick - _bpmEvents[i].Tick;
            return starts[i] + (double)span / TicksPerBeat * (FramesPerMinute / _bpmEvents[i].Bpm);
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
    /// 2026-07-17c: FrameAnchor対応(TickToFrameと対称な扱い)。
    /// 2026-08-23: 区間がBPMリンクされていれば、RampTickAtFrames(解析解の逆関数)を使う。</summary>
    public double FrameToTick(double frame)
    {
        // FrameAnchorにより区間開始フレームが単調とは限らないため、二分探索はせず線形探索のまま
        // (「frame >= starts[i] を満たす最後のi」という従来の意味を保つ)。
        var starts = _segmentStartFrames;
        int active = 0;
        for (int i = 0; i < _bpmEvents.Count; i++)
        {
            if (frame >= starts[i])
                active = i;
        }
        if (_bpmEvents[active].LinkGridDivision is { } div && div > 0 && active + 1 < _bpmEvents.Count)
        {
            return RampTickAtFrames(
                _bpmEvents[active].Tick, _bpmEvents[active].Bpm,
                _bpmEvents[active + 1].Tick, _bpmEvents[active + 1].Bpm, frame - starts[active]);
        }
        double framesPerTick = FramesPerMinute / _bpmEvents[active].Bpm / TicksPerBeat;
        return _bpmEvents[active].Tick + (frame - starts[active]) / framesPerTick;
    }

    /// <summary>論理小節mの開始tick(拍子イベント列に従って積算)。事前計算した区間開始tickと二分探索で
    /// O(log 拍子イベント数)。</summary>
    public long MeasureStartTick(int measureIndex)
    {
        if (measureIndex <= 0) return 0;
        // MeasureIndex <= measureIndex を満たす最後の拍子イベントを探す
        int lo = 0, hi = _timeSignatures.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_timeSignatures[mid].MeasureIndex <= measureIndex) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var sig = _timeSignatures[found];
        return _sigStartTicks[found] + ((long)measureIndex - sig.MeasureIndex) * sig.TicksPerMeasure;
    }

    /// <summary>tickがどの論理小節の何tick目かを返す(二分探索、O(log 拍子イベント数))</summary>
    public (int MeasureIndex, long TickInMeasure) TickToMeasurePosition(long tick)
    {
        // tick<0は従来どおり「小節0からの負のオフセット」として返す
        if (tick < 0) return (0, tick);
        // 区間開始tickが tick 以下である最後の拍子イベントを探す
        int lo = 0, hi = _timeSignatures.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_sigStartTicks[mid] <= tick) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var sig = _timeSignatures[found];
        long offset = tick - _sigStartTicks[found];
        long measure = sig.MeasureIndex + offset / sig.TicksPerMeasure;
        return ((int)Math.Min(measure, int.MaxValue), offset % sig.TicksPerMeasure);
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
