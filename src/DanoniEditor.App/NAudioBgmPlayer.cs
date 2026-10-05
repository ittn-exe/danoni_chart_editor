using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DanoniEditor.App;

/// <summary>
/// BGM(楽曲)再生エンジン(2026-07-26f全面改修、目視テスト・プレイテスト共通)。
///
/// これまでBGM再生はWPFの<see cref="System.Windows.Media.MediaPlayer"/>(Media Foundation経由)を
/// 使っており、ハンドクラップはBGMとは別のNAudio WasapiOutで、UIスレッドのDispatcherTimer(16ms間隔)
/// がMediaPlayer.Positionをポーリングして「通過したら別デバイスで鳴らす」という構造だった。
/// この構造には、①MediaPlayer.Positionそのものの粗さ・遅延、②ポーリング間隔(16ms)の粒度、
/// ③クラップ専用WasapiOutの別デバイスバッファ、という3つの遅延要因が積み重なっており、
/// 体感できるラグが解消できなかった(2026-07-26〜2026-07-26eのユーザー指摘・調査より)。
///
/// この改修では、BGM自体をNAudio(WasapiOut)で再生する自前エンジンに置き換え、ハンドクラップの
/// 発音判定・PCM重ね合わせをBGMのレンダーコールバック(<see cref="Render"/>、オーディオスレッド上で
/// 実行される)の内部で直接行う。BGMの「今まさに読んでいるサンプル位置」を基準にクラップの発音を
/// 決めるため、UIスレッドのポーリングも別デバイスの往復も発生しない。BMS等の外部キー音参照方式が
/// BGMと同じ音声クロック上でキー音をスケジューリングしているのと同じ考え方である。
///
/// 曲全体は一度だけ全デコードしてメモリに保持する(数分の曲でも数十MB程度で許容範囲、
/// WaveformDecoder.Decodeが既に同様の全読み込みを行っているのと同じ方針)。これにより、
/// 任意位置へのシーク・速度変更(ピッチ補正なしの単純な読み取り速度変更)を、ファイルの
/// 読み直しやシークを伴わずに実現できる。
/// </summary>
internal sealed class NAudioBgmPlayer : IDisposable
{
    private readonly object _lock = new();

    private float[]? _pcm;      // 全体PCM(interleaved, float32)。曲を開くまではnull。
    private WaveFormat? _format;
    private double _framePos;   // 現在の読み取りカーソル(サンプルフレーム単位、小数)
    private double _speed = 1.0;
    private bool _playing;
    private float _volume = 1.0f;

    /// <summary>2026-07-26f: Open()は非同期デコードのため、デコード完了前にPositionが設定された場合
    /// (呼び出し元がOpen直後に同期的にPosition/Play等を呼ぶ既存の使い方に合わせるため)、いったんここへ
    /// 保留し、OpenAsync完了時に反映する。</summary>
    private TimeSpan? _pendingSeek;

    private WasapiOut? _output;

    // --- 目視テストのスクロール外挿(2026-09-29要望対応、SmoothedPosition参照) ---
    private double _smoothAnchorFramePos;
    private long _smoothAnchorTimestamp;
    private bool _smoothAnchorInitialized;
    /// <summary>SmoothedPositionが生の_framePos更新から先読みして良い最大時間。これを超えて生の値が
    /// 更新されない場合は本当のスタック(既存のWASAPI自動復旧の対象)とみなし、それ以上は先読みしない。</summary>
    private const double MaxSmoothExtrapolationSeconds = 0.05;

    // --- ハンドクラップ(2026-07-26f: BGMのレンダースレッド内で直接発音判定・PCM重ね合わせを行う) ---
    private float[]? _clapPcm;
    private WaveFormat? _clapFormat;
    private List<double>? _clapFrames; // BGMのframe(60fps)基準、昇順
    private int _clapNextIndex;
    private float _clapVolume = 1.0f;
    private readonly List<double> _activeClapPositions = []; // 発音中クラップの経過フレーム位置(クラップPCM基準、複数同時可)

    /// <summary>曲の全体長(デコード完了後のみ値が入る)。</summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>診断用(2026-09-07: 「Spaceで目視テストを開始しても無音・再生位置ラインが動かない」
    /// 不具合の切り分け用): Play()/Stop()で切り替わる内部フラグの生値。</summary>
    public bool DiagIsPlayingFlag { get { lock (_lock) return _playing; } }

    /// <summary>診断用: WASAPI出力ストリーム自体の実際の再生状態(Stopped/Playing/Paused)。
    /// _output未初期化(音楽未読込)の場合は"(未初期化)"を返す。</summary>
    public string DiagOutputState => _output?.PlaybackState.ToString() ?? "(未初期化)";

    /// <summary>Open完了時に発火(WPF MediaPlayer.MediaOpenedの代替、呼び出し元の使い方を変えずに
    /// 済むよう同じ「非同期に後から通知」の形にしている)。デコードはバックグラウンドスレッドで行い、
    /// 完了後の通知はawait元のSynchronizationContext(WPFならUIスレッド)へ戻る。</summary>
    public event Action? MediaOpened;

    /// <summary>デコードに失敗した(ファイルが壊れている・未対応形式など)ことを通知する(2026-10-06)。
    /// 引数は対象のファイルパスと例外。従来は失敗しても何も通知されず「未読込」のままだった。</summary>
    public event Action<string, Exception>? OpenFailed;

    /// <summary>音声出力(WASAPI)の初期化に失敗したことを通知する(2026-10-06)。</summary>
    public event Action<Exception>? OutputFailed;

    /// <summary>音楽ファイルを開く(非同期、WPF MediaPlayer.Open()と同じく戻り値を待たずに使える)。
    /// 呼び出し元のスレッド(通常UIスレッド)のSynchronizationContextを捕捉した上でTask.Runへ
    /// デコードを逃がすため、完了後のMediaOpenedはUIスレッドへ戻って発火する。
    /// デコード失敗時は例外を投げず(呼び出し元は従来通りfire-and-forgetで呼ぶため)、単に
    /// 「未読込」状態のままにする(WPF MediaPlayerも不正ファイルを同期的には検知できなかった点は同じ)。</summary>
    public void Open(string path) => _ = OpenAsync(path);

    // 2026-10-05: OpenAsyncが連続して呼ばれた場合(曲の切替え直後など)、古い呼び出しのデコードが後から
    // 完了して新しい曲を上書きしないよう、世代番号で「最新の呼び出しの結果だけ」を採用する。
    private int _openGeneration;
    private volatile bool _disposed;

    public async Task OpenAsync(string path)
    {
        int generation = Interlocked.Increment(ref _openGeneration);
        Stop();
        (float[] Pcm, WaveFormat Format, TimeSpan Duration)? result;
        try { result = await Task.Run(() => DecodeWholeFile(path)).ConfigureAwait(true); }
        catch (Exception ex)
        {
            AppLog.Write("NAudioBgmPlayer.OpenAsync decode", ex);
            if (!_disposed && generation == Volatile.Read(ref _openGeneration))
                OpenFailed?.Invoke(path, ex);
            result = null;
        }
        if (result is not { } decoded) return;
        if (_disposed || generation != Volatile.Read(ref _openGeneration)) return; // 破棄済み/より新しいOpenが始まっている

        lock (_lock)
        {
            _pcm = decoded.Pcm;
            _format = decoded.Format;
            double maxFrame = _pcm.Length / (double)_format.Channels;
            _framePos = _pendingSeek is { } pending
                ? Math.Clamp(pending.TotalSeconds * _format.SampleRate, 0, maxFrame)
                : 0;
            _pendingSeek = null;
            _clapNextIndex = 0;
            _activeClapPositions.Clear();
            _smoothAnchorInitialized = false; // 新規読込時は次回SmoothedPosition取得時にアンカーを作り直す
            RecomputeClapCursorLocked();
        }
        Duration = decoded.Duration;
        try { SetupOutput(decoded.Format); }
        catch (Exception ex)
        {
            // 出力デバイスが無い/使用中などで初期化に失敗しても、呼び出し元(fire-and-forget)へ
            // 未観測例外として漏らさず、ログに残して「音は出ないが編集は続けられる」状態にする。
            AppLog.Write("NAudioBgmPlayer.SetupOutput", ex);
            OutputFailed?.Invoke(ex);
        }
        MediaOpened?.Invoke();
    }

    private static (float[] Pcm, WaveFormat Format, TimeSpan Duration) DecodeWholeFile(string path)
    {
        using var reader = new AudioFileReader(path);
        var format = reader.WaveFormat;
        // 2026-10-05: 従来はList<float>へ追記→ToArray()で、デコード中のピークメモリが最大でPCM本体の約3倍に
        // なっていた。想定サイズの配列を直接確保して書き込み、サイズが外れた場合のみ倍々で拡張する
        // (最後に実サイズへ詰め直す1回のコピーのみ)。
        long expected = Math.Max(reader.Length / 4, 1024);
        if (expected > int.MaxValue / 2) throw new System.IO.InvalidDataException("音声ファイルが大きすぎます");
        var pcm = new float[expected];
        int count = 0;
        var buf = new float[format.SampleRate * format.Channels];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            if (count + n > pcm.Length)
            {
                long newSize = Math.Max((long)pcm.Length * 2, (long)count + n);
                if (newSize > Array.MaxLength) throw new System.IO.InvalidDataException("音声ファイルが大きすぎます");
                Array.Resize(ref pcm, (int)newSize);
            }
            Array.Copy(buf, 0, pcm, count, n);
            count += n;
        }
        if (count != pcm.Length) Array.Resize(ref pcm, count);
        return (pcm, format, reader.TotalTime);
    }

    private void SetupOutput(WaveFormat format)
    {
        try { _output?.Stop(); } catch { /* 破棄前提のため無視 */ }
        _output?.Dispose();
        // 2026-07-26f: 共有モード・イベント同期・短いレイテンシ(20ms)。以前のクラップ専用出力(40ms)より
        // 短くしつつ、単独のBGM+クラップ処理程度なら十分安定するレイテンシ値として選定。
        _output = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        _output.Init(new RenderProvider(this, format));
        _output.Play();
    }

    /// <summary>現在の再生位置。</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
                return _format is null ? _pendingSeek ?? TimeSpan.Zero : TimeSpan.FromSeconds(_framePos / _format.SampleRate);
        }
        set
        {
            lock (_lock)
            {
                if (_format is null) { _pendingSeek = value; return; }
                double maxFrame = _pcm is null ? 0 : _pcm.Length / (double)_format.Channels;
                _framePos = Math.Clamp(value.TotalSeconds * _format.SampleRate, 0, maxFrame);
                _smoothAnchorInitialized = false; // シーク直後は次回SmoothedPosition取得時に必ずアンカーを作り直す
                RecomputeClapCursorLocked();
            }
        }
    }

    /// <summary>2026-09-29要望対応: 目視テストのスクロールが「WasapiOutのバッファ更新間隔(既定20ms)ごとに
    /// しか_framePosが進まない」ことに起因してガクガクして見える問題への対策として追加した、外挿済みの
    /// 再生位置。生の<see cref="Position"/>は一切変更しない(スタック検知・曲末尾判定・診断ログなど、
    /// 正確性が必要な既存処理は引き続き生のPositionを使うこと)。こちらは「スクロール・ノート描画位置の
    /// 計算専用」として使う想定。
    ///
    /// 仕組みは単純で、「直近に観測した生の_framePosの値と、それを観測した実時刻(Stopwatch基準の
    /// 高精度タイマー、DateTime.UtcNowでは分解能が粗く20ms未満の外挿には使えない)」をアンカーとして保持し、
    /// 呼ばれるたびに①生の_framePosがアンカーと変わっていれば、その新しい値へ即座にアンカーを更新
    /// (補間ではなく瞬時に飛ぶ。シーク・ループ再生・WASAPI自動復旧による位置の再設定も、内部的には
    /// すべて_framePosの変化として現れるため、特別扱い無しでこの仕組みだけで自然に追従できる)、
    /// ②変わっていなければ「アンカーからの経過実時間 × 再生速度」で先読みする、という2択のみ。
    /// 再生速度(_speed/SpeedRatio)はこのエンジン自身が完全に把握している値のため、ブラウザ拡張機能の
    /// ように観測から推定する必要がない(既知の値をそのまま使えるぶん、こちらの方が単純かつ正確)。
    ///
    /// 先読みできる時間はMaxSmoothExtrapolationSeconds(既定50ms)で頭打ちにする。これにより、本当に
    /// 音声がスタックした場合(既存のPlaybackTimer_Tickの250ms・完全一致によるスタック検知の対象)でも、
    /// この値は50ms先読みした時点で追従を止めて生の値相走に張り付くため、スタック検知ロジック側は
    /// このプロパティの存在を意識せず(=生のPositionを見続けるだけで)引き続き正しく機能する。</summary>
    public TimeSpan SmoothedPosition
    {
        get
        {
            lock (_lock)
            {
                if (_format is null) return _pendingSeek ?? TimeSpan.Zero;

                long now = Stopwatch.GetTimestamp();
                if (!_smoothAnchorInitialized || _framePos != _smoothAnchorFramePos)
                {
                    _smoothAnchorFramePos = _framePos;
                    _smoothAnchorTimestamp = now;
                    _smoothAnchorInitialized = true;
                }

                double smoothedFramePos = _smoothAnchorFramePos;
                if (_playing)
                {
                    double elapsedSeconds = (now - _smoothAnchorTimestamp) / (double)Stopwatch.Frequency;
                    double cappedElapsedSeconds = Math.Clamp(elapsedSeconds, 0, MaxSmoothExtrapolationSeconds);
                    smoothedFramePos += cappedElapsedSeconds * _format.SampleRate * _speed;
                }

                double maxFrame = _pcm is null ? 0 : _pcm.Length / (double)_format.Channels;
                smoothedFramePos = Math.Clamp(smoothedFramePos, 0, maxFrame);
                return TimeSpan.FromSeconds(smoothedFramePos / _format.SampleRate);
            }
        }
    }

    /// <summary>再生速度(ピッチ補正なしの単純な読み取り速度変更、WPF MediaPlayer.SpeedRatio相当)。</summary>
    public double SpeedRatio { get => _speed; set => _speed = Math.Max(0.01, value); }

    /// <summary>再生音量(0.0〜1.0)。</summary>
    public double Volume { get => _volume; set => _volume = (float)Math.Clamp(value, 0.0, 1.0); }

    public void Play() { lock (_lock) _playing = true; }
    public void Stop() { lock (_lock) _playing = false; }

    /// <summary>診断用/自動復旧(2026-09-13要望対応): WASAPI出力ストリームが、内部的には「再生中」の
    /// ままなのに実際のレンダリングコールバックが呼ばれなくなり、位置が進まなくなる不具合
    /// (環境報告の診断情報で確認済み、NAudioのイベント同期に起因すると見られる)への対策。
    /// 既存の音声データ(_pcm/_format/_framePos)はそのまま使い、出力デバイスストリーム(_output、
    /// WasapiOutインスタンス)だけを作り直す。呼び出し元がPosition/Playを設定し直す前提
    /// (MainWindow.PlaybackTimer_Tick参照)。</summary>
    public void RecoverOutput()
    {
        WaveFormat? fmt;
        lock (_lock) fmt = _format;
        if (fmt is null) return;
        SetupOutput(fmt); // _lockを保持したまま呼ぶとStop()がオーディオスレッド合流待ちでデッドロックしうるため、ロック外で呼ぶ
    }

    /// <summary>ハンドクラップのスケジュールを設定する(2026-07-26f)。framesはBGMのframe(60fps)基準、
    /// 昇順・呼び出し元で必要な範囲(再生開始frame以降等)に絞り込んだ状態で渡すこと。
    /// nullを渡すとクラップ無効化。</summary>
    public void SetClapSchedule(HandClapPlayer? clap, IReadOnlyList<double>? frames, double volume)
    {
        lock (_lock)
        {
            if (clap is { Available: true } && frames is not null)
            {
                _clapPcm = clap.AudioData;
                _clapFormat = clap.WaveFormat;
                _clapFrames = [.. frames];
            }
            else
            {
                _clapPcm = null;
                _clapFormat = null;
                _clapFrames = null;
            }
            _clapVolume = (float)Math.Clamp(volume, 0.0, 1.0);
            _clapNextIndex = 0;
            _activeClapPositions.Clear();
            RecomputeClapCursorLocked();
        }
    }

    /// <summary>クラップ音量のみ即時反映する(2026-07-26f、環境設定の音量欄からのライブ反映用)。</summary>
    public void SetClapVolume(double volume) { lock (_lock) _clapVolume = (float)Math.Clamp(volume, 0.0, 1.0); }

    /// <summary>現在のBGM位置(_framePos)を基準に、次に発音すべきクラップのインデックスへ合わせ直す。
    /// シーク(Position設定)やスケジュール再設定のたびに呼ぶことで、巻き戻し後の重複発音や、
    /// 早送り後の「本来もう鳴っているはずの音」の取りこぼしを防ぐ。呼び出し元で_lock保持済みが前提。</summary>
    private void RecomputeClapCursorLocked()
    {
        _activeClapPositions.Clear();
        if (_clapFrames is null || _format is null) { _clapNextIndex = 0; return; }
        double frame60 = _framePos / _format.SampleRate * 60.0;
        int idx = _clapFrames.FindIndex(f => f >= frame60);
        _clapNextIndex = idx < 0 ? _clapFrames.Count : idx;
    }

    /// <summary>レンダリング本体(WasapiOutのオーディオコールバックスレッドから呼ばれる)。
    /// BGMサンプルの読み取り(速度可変、線形補間)と、クラップの発音判定・PCM重ね合わせを
    /// 同じループ内・同じサンプル位置基準で行う(2026-07-26f、本改修の核心部分)。</summary>
    private int Render(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_pcm is null || _format is null) { Array.Clear(buffer, offset, count); return count; }
            int channels = _format.Channels;
            int frames = count / channels;
            double maxFrame = _pcm.Length / (double)channels;

            for (int f = 0; f < frames; f++)
            {
                bool canRead = _playing && _framePos < maxFrame - 1;
                if (canRead)
                {
                    // クラップ発音判定: このBGMサンプル位置(_framePos)を基準に、通過したノートframeが
                    // あれば発音キューへ追加する(UIスレッドを介さず、この場で即座に判定する)。
                    if (_clapFrames is not null)
                    {
                        double frame60 = _framePos / _format.SampleRate * 60.0;
                        while (_clapNextIndex < _clapFrames.Count && _clapFrames[_clapNextIndex] <= frame60)
                        {
                            _activeClapPositions.Add(0);
                            _clapNextIndex++;
                        }
                    }

                    for (int ch = 0; ch < channels; ch++)
                        buffer[offset + f * channels + ch] = SampleAt(_pcm, channels, _framePos, ch) * _volume;

                    _framePos += _speed;
                }
                else
                {
                    for (int ch = 0; ch < channels; ch++) buffer[offset + f * channels + ch] = 0f;
                }

                // クラップの重ね合わせ(等倍速固定、複数同時発音対応)
                if (_clapPcm is { Length: > 0 } && _clapFormat is not null)
                {
                    int clapChannels = _clapFormat.Channels;
                    double clapMaxFrame = _clapPcm.Length / (double)clapChannels;
                    for (int ci = _activeClapPositions.Count - 1; ci >= 0; ci--)
                    {
                        double cp = _activeClapPositions[ci];
                        if (cp >= clapMaxFrame - 1) { _activeClapPositions.RemoveAt(ci); continue; }
                        for (int ch = 0; ch < channels; ch++)
                        {
                            int srcCh = Math.Min(ch, clapChannels - 1);
                            buffer[offset + f * channels + ch] += SampleAt(_clapPcm, clapChannels, cp, srcCh) * _clapVolume;
                        }
                        _activeClapPositions[ci] = cp + 1;
                    }
                }
            }
            return count;
        }
    }

    /// <summary>フレーム位置(小数、隣接フレーム間を線形補間)から1サンプルを読む。</summary>
    private static float SampleAt(float[] pcm, int channels, double framePos, int ch)
    {
        long f0 = (long)framePos;
        double frac = framePos - f0;
        long i0 = f0 * channels + ch;
        long i1 = i0 + channels;
        float s0 = i0 >= 0 && i0 < pcm.Length ? pcm[i0] : 0f;
        float s1 = i1 >= 0 && i1 < pcm.Length ? pcm[i1] : 0f;
        return (float)(s0 + (s1 - s0) * frac);
    }

    private sealed class RenderProvider(NAudioBgmPlayer owner, WaveFormat format) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = format;
        public int Read(float[] buffer, int offset, int count) => owner.Render(buffer, offset, count);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _output?.Stop(); } catch { /* 終了処理なので失敗しても無視 */ }
        _output?.Dispose();
    }
}
