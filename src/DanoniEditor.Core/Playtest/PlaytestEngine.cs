using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Playtest;

/// <summary>プレイテストの判定種別(danoniplus本家準拠、仕様書12.2.1)</summary>
public enum PlayJudge { Ii, Shakin, Matari, Shobon, Uwan, Kita, Iknai }

/// <summary>判定結果イベント。DiffFramesは「ノート基準フレーム − 入力フレーム」(正=早押し)</summary>
public sealed record JudgeResult(int Lane, PlayJudge Judge, double DiffFrames);

/// <summary>
/// プレイテストの判定エンジン(WPF非依存、2026-07-17g)。判定幅はdanoniplus本家の
/// g_judgObj(Normal)準拠: 矢印 ±2(イイ)/±4(シャキン)/±6(マターリ)/±8(ショボーン)、
/// 枠外経過でウワァン。フリーズは仕様書12.2.1確定仕様: 始点±4以内かつ終点まで押し続けで
/// O.K.(キター)、±5〜8/枠外/早離し(frzAttempt猶予超過)でN.G.(イクナイ)。
/// コンボ規則も本家準拠(judgeRecovery/judgeMatari/judgeDamage):
/// イイ・シャキン=加算、マターリ=維持(加算なし)、ショボーン・ウワァン=リセット。
/// フリーズはコンボ別勘定(fCombo相当)で、矢印コンボに影響しない。
/// 時刻は呼び出し側から渡される「現在フレーム」(音楽位置×60fps)で駆動され、
/// オフセット(タイミング調整、仕様書12.2)はコンストラクタでノート側フレームへ加算する。
/// </summary>
public sealed class PlaytestEngine
{
    public const double IiWindow = 2, ShakinWindow = 4, MatariWindow = 6, ShobonWindow = 8;
    public const double FrzOkWindow = 4, FrzNgWindow = 8;

    /// <summary>矢印1個の状態。Resultがnull以外なら判定済み(Uwan含む)</summary>
    public sealed class ArrowState
    {
        public double Frame { get; init; }
        public PlayJudge? Result { get; internal set; }
    }

    /// <summary>フリーズ1個の状態</summary>
    public sealed class FreezeState
    {
        public double StartFrame { get; init; }
        public double EndFrame { get; init; }
        public bool Started { get; internal set; }
        public bool Holding { get; internal set; }
        public double ReleasedAt { get; internal set; } = double.NaN;
        public PlayJudge? Result { get; internal set; } // Kita/Iknai確定後に非null
    }

    private readonly List<ArrowState>[] _arrows;
    private readonly List<FreezeState>[] _freezes;
    private readonly double _frzAttempt;

    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public int FreezeCombo { get; private set; }

    /// <summary>判定確定のたびに発火(UI側の判定文字・コンボ表示用)</summary>
    public event Action<JudgeResult>? Judged;

    /// <param name="minFrame">2026-07-26要望対応: 「再生開始ラインから始まる譜面を遊ぶ」形式のための
    /// 下限フレーム(タイミング調整offsetFrames適用前の素のフレームで判定)。この値未満のノート/フリーズ
    /// (始点基準)は判定対象から一切除外する(存在しないものとして扱う)。既定は無効(全ノート対象、
    /// 従来通り)。</param>
    public PlaytestEngine(DifficultyTab tab, TimingEngine timing, double frzAttempt, double offsetFrames = 0,
        double minFrame = double.NegativeInfinity)
    {
        int n = tab.Lanes.Count;
        _arrows = new List<ArrowState>[n];
        _freezes = new List<FreezeState>[n];
        _frzAttempt = frzAttempt;
        for (int i = 0; i < n; i++)
        {
            _arrows[i] = tab.Lanes[i].Notes.OrderBy(t => t)
                .Select(t => timing.TickToFrame(t))
                .Where(f => f >= minFrame)
                .Select(f => new ArrowState { Frame = f + offsetFrames })
                .ToList();
            _freezes[i] = tab.Lanes[i].Freezes.OrderBy(f => f.StartTick)
                .Select(f => (Start: timing.TickToFrame(f.StartTick), End: timing.TickToFrame(f.EndTick)))
                .Where(f => f.Start >= minFrame)
                .Select(f => new FreezeState { StartFrame = f.Start + offsetFrames, EndFrame = f.End + offsetFrames })
                .ToList();
        }
    }

    public IReadOnlyList<ArrowState> ArrowsOf(int lane) => _arrows[lane];
    public IReadOnlyList<FreezeState> FreezesOf(int lane) => _freezes[lane];

    /// <summary>キー押下。対象ノートが判定枠内に無ければ何もしない(空押しペナルティなし)</summary>
    public void KeyDown(int lane, double frame)
    {
        // ホールド中断からのfrzAttempt猶予内の再押下はホールド復帰として扱う
        var resumable = _freezes[lane].FirstOrDefault(f => f.Started && f.Result is null && !f.Holding);
        if (resumable is not null)
        {
            resumable.Holding = true;
            resumable.ReleasedAt = double.NaN;
            return;
        }

        var arrow = _arrows[lane]
            .Where(a => a.Result is null && Math.Abs(a.Frame - frame) <= ShobonWindow)
            .OrderBy(a => Math.Abs(a.Frame - frame))
            .FirstOrDefault();
        var frz = _freezes[lane]
            .Where(f => !f.Started && f.Result is null && Math.Abs(f.StartFrame - frame) <= FrzNgWindow)
            .OrderBy(f => Math.Abs(f.StartFrame - frame))
            .FirstOrDefault();

        if (arrow is not null && (frz is null || Math.Abs(arrow.Frame - frame) <= Math.Abs(frz.StartFrame - frame)))
        {
            double d = Math.Abs(arrow.Frame - frame);
            var judge = d <= IiWindow ? PlayJudge.Ii
                      : d <= ShakinWindow ? PlayJudge.Shakin
                      : d <= MatariWindow ? PlayJudge.Matari
                      : PlayJudge.Shobon;
            arrow.Result = judge;
            ApplyArrowJudge(lane, judge, arrow.Frame - frame);
        }
        else if (frz is not null)
        {
            double d = Math.Abs(frz.StartFrame - frame);
            if (d <= FrzOkWindow)
            {
                frz.Started = true;
                frz.Holding = true;
            }
            else
            {
                // 始点±5〜8フレームはN.G.確定(仕様書12.2.1)
                frz.Result = PlayJudge.Iknai;
                FreezeCombo = 0;
                Emit(lane, PlayJudge.Iknai, frz.StartFrame - frame);
            }
        }
    }

    /// <summary>キー離し。ホールド中フリーズがあれば猶予計測を開始する(確定はAdvance側)</summary>
    public void KeyUp(int lane, double frame)
    {
        var f = _freezes[lane].FirstOrDefault(f => f.Started && f.Result is null && f.Holding);
        if (f is null) return;
        f.Holding = false;
        f.ReleasedAt = frame;
    }

    /// <summary>時間経過処理。フレームは単調増加で渡すこと(見逃しウワァン・フリーズ確定を行う)</summary>
    public void Advance(double frame)
    {
        for (int lane = 0; lane < _arrows.Length; lane++)
        {
            foreach (var a in _arrows[lane])
            {
                if (a.Result is null && a.Frame + ShobonWindow < frame)
                {
                    a.Result = PlayJudge.Uwan;
                    ApplyArrowJudge(lane, PlayJudge.Uwan, a.Frame - frame);
                }
            }

            foreach (var f in _freezes[lane])
            {
                if (f.Result is not null) continue;

                if (!f.Started)
                {
                    if (f.StartFrame + FrzNgWindow < frame)
                    {
                        f.Result = PlayJudge.Iknai; // 始点見逃し
                        FreezeCombo = 0;
                        Emit(lane, PlayJudge.Iknai, f.StartFrame - frame);
                    }
                    continue;
                }

                // 早離し: frzAttempt猶予を超えて再押下が無く、まだ終点前ならN.G.
                if (!f.Holding && frame > f.ReleasedAt + _frzAttempt && frame < f.EndFrame)
                {
                    f.Result = PlayJudge.Iknai;
                    FreezeCombo = 0;
                    Emit(lane, PlayJudge.Iknai, 0);
                    continue;
                }

                // 終点到達: ホールド継続中、または猶予内の離しならO.K.
                if (frame >= f.EndFrame && (f.Holding || frame <= f.ReleasedAt + _frzAttempt))
                {
                    f.Result = PlayJudge.Kita;
                    FreezeCombo++;
                    Emit(lane, PlayJudge.Kita, 0);
                }
            }
        }
    }

    private void ApplyArrowJudge(int lane, PlayJudge judge, double diff)
    {
        switch (judge)
        {
            case PlayJudge.Ii or PlayJudge.Shakin:
                Combo++;
                if (Combo > MaxCombo) MaxCombo = Combo;
                break;
            case PlayJudge.Matari:
                break; // コンボ維持・加算なし(本家judgeMatari準拠)
            default:
                Combo = 0; // ショボーン/ウワァン(本家judgeDamage準拠)
                break;
        }
        Emit(lane, judge, diff);
    }

    private void Emit(int lane, PlayJudge judge, double diff) => Judged?.Invoke(new JudgeResult(lane, judge, diff));

    /// <summary>再生開始フレームからのやり直し用(2026-07-26d要望対応、プレイテスト中のBackSpace)。
    /// 全ノート/フリーズの判定結果・ホールド状態・コンボをコンストラクタ直後の状態へ戻す。
    /// (ノート自体のFrameはタイミング固定のため再生成不要、Resultだけ消せば十分)</summary>
    public void Reset()
    {
        foreach (var lane in _arrows)
            foreach (var a in lane) a.Result = null;
        foreach (var lane in _freezes)
            foreach (var f in lane)
            {
                f.Started = false;
                f.Holding = false;
                f.ReleasedAt = double.NaN;
                f.Result = null;
            }
        Combo = 0;
        MaxCombo = 0;
        FreezeCombo = 0;
    }
}
