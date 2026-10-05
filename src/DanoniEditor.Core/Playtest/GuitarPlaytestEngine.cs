using System.Linq;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Playtest;

/// <summary>
/// ギター系キー種(danoniplus custom.js「std_gt.js」相当、GuitarFreaks形式)のプレーテスト判定を、
/// レーン独立設計のPlaytestEngineの外側から補助するオーバーレイ(2026-09-27要望対応)。
///
/// PlaytestEngine自体は改修せず、「ピックレーン(テンプレートのDataName="space")のキー押下時にのみ、
/// フレットレーン側の同tickノートをまとめて消費する」という、std_gt.jsのコード判定
/// (_gtrFingerMatch/_gtrConsumeMemberHit相当)を実現する。フレットレーン単体のキー押下
/// (ピックを介さない)はPlaytestWindow側で抑制し(このクラスは関与しない)、ピック押下時のみ
/// <see cref="PickDown"/>を呼ぶことで「フレットは指板、ピックで弾く」という
/// ギターフリークス形式の判定を実現する。
///
/// フリーズのホールド中断からの復帰(PlaytestEngine.KeyDown先頭の「resumable」分岐、frzAttempt猶予内の
/// 再押下)は、既にホールドが始まっている(=一度はピック経由で消費済みの)フレットの話であり、
/// このクラスの対象外(呼び出し元PlaytestWindow側でPlaytestEngine.KeyDownを直接呼んでよい)。
///
/// Phase2範囲(2026-09-27b): ピック先行の猶予(<see cref="PickDown"/>で不一致時に即ミスにせず保留、
/// 呼び出し元が毎フレーム呼ぶ<see cref="Advance"/>で猶予内の指板一致・猶予超過を判定)、離し遅れ免除
/// (直前に解決した1ノート/1組・フリーズ終端直後の押しっぱなしを「余計な押下」として扱わない)、
/// ダブルピック許可設定(保留中の追加ピック入力を無視するかどうか)に対応した。
///
/// 2026-09-27c: 「オートピック/オートネック(std_gt.js Phase3相当の片手だけ自動アシスト)は設けず、
/// 本体実装の全体オートプレイに一元化する」という方針確定に伴い、この個別アシストは対応しない
/// (全体オートプレイは<see cref="PlaytestEngine.KeyDown"/>を全レーンへ直接呼ぶためこのクラスを
/// 経由しない。詳細はPlaytestWindow.AutoPlayAdvance参照)。
///
/// 同時に、Phase4(見た目の演出)着手前の見直しで、std_gt.jsには「ハンマリング(ピック非同時)
/// 矢印」というピックを介さずフレット単体で判定されるノート種別があることが判明したため、
/// <see cref="IsSimultaneousWithPick"/>を追加し、PlaytestWindow.HandleLaneKeyDownの
/// フレット単体押下の抑制ロジックをこれで正しく分岐させるよう修正した(std_gt.js _gtrIsPickSimul相当。
/// 同tickでない=ハンマリング=通常のPlaytestEngine.KeyDownをそのまま通す)。
/// </summary>
public sealed class GuitarPlaytestEngine
{
    private readonly PlaytestEngine _engine;
    private readonly GuitarSettings _settings;

    /// <summary>ピックレーンのインデックス(テンプレートのDataName="space"のレーン)。</summary>
    public int PickLane { get; }

    /// <summary>フレットレーンのインデックス一覧(ピックレーン以外の全レーン)。</summary>
    public IReadOnlyList<int> FretLanes { get; }

    /// <summary>直近に<see cref="PickDown"/>または<see cref="Advance"/>へ渡されたフレーム
    /// (呼び出し元の_currentFrame)。離し遅れ免除・フリーズ終端猶予の基準時刻として使う。</summary>
    private double _currentFrame;

    /// <summary>ピック先行の保留状態(押した時のフレーム, 対象のtick)。std_gt.js _gtrPendingPick相当。</summary>
    private (double Frame, long Tick)? _pendingPick;

    // --- 離し遅れ免除の追跡(std_gt.js _gtrTrackResolvedLanes/_gtrLastResolvedLanes/Frame相当) ---
    // PlaytestEngine.Judgedは呼び出し元(PickDown/Advance)が渡したフレーム内で同期的に発火するため、
    // 「同じ_currentFrameの間に発火した分をまとめて1組とし、フレームが変わったら置き換える」ことで、
    // 呼び出し経路(ピック経由の消費/フレット直接のホールド復帰/PlaytestEngine自身のタイムアウト)を
    // 問わず、std_gt.jsの「1回のピック処理は同一フレーム内で完結する」という前提を再現する。
    private readonly List<int> _batchLanes = [];
    private double _batchFrame = double.NaN;
    private HashSet<int> _lastResolvedLanes = [];
    private double _lastResolvedFrame = double.NegativeInfinity;

    /// <summary>レーン別: 最後にフリーズが終端した(Kita/Iknai問わず)フレーム。
    /// std_gt.js _gtrFrzEndFrame相当。未終端のレーンはキーを持たない。</summary>
    private readonly Dictionary<int, double> _freezeEndFrame = [];

    private GuitarPlaytestEngine(PlaytestEngine engine, GuitarSettings settings, int pickLane, int[] fretLanes)
    {
        _engine = engine;
        _settings = settings;
        PickLane = pickLane;
        FretLanes = fretLanes;
        _engine.Judged += OnJudged;
    }

    /// <summary>
    /// テンプレート・環境設定からギターオーバーレイを構築する。対象外キー種、あるいは
    /// ピックレーン(DataName="space")が見つからないテンプレートではnullを返す
    /// (呼び出し元は通常のPlaytestEngine.KeyDownのみで判定を続行すればよい)。
    /// </summary>
    public static GuitarPlaytestEngine? TryCreate(KeyTemplate template, PlaytestEngine engine, GuitarSettings settings)
    {
        if (!settings.TargetKeyTypeIds.Contains(template.KeyTypeId)) return null;

        int pickLane = -1;
        for (int i = 0; i < template.Lanes.Count; i++)
        {
            if (template.Lanes[i].DataName == "space") { pickLane = i; break; }
        }
        if (pickLane < 0) return null;

        var fretLanes = Enumerable.Range(0, template.Lanes.Count).Where(i => i != pickLane).ToArray();
        if (fretLanes.Length == 0) return null;

        return new GuitarPlaytestEngine(engine, settings, pickLane, fretLanes);
    }

    /// <summary>
    /// レーンfretLaneの未判定先頭ターゲットが、ピックレーンの未判定先頭ターゲットと同tick(=ピックで
    /// まとめて消費すべきコード構成メンバー)かどうか(std_gt.js _gtrIsPickSimul相当)。
    /// falseの場合、そのフレットノートはピックを介さず単体で判定される「ハンマリング」ノートであり、
    /// フレットキー単体の押下がそのまま通常のPlaytestEngine.KeyDownで判定されるべきことを表す
    /// (呼び出し元PlaytestWindow.HandleLaneKeyDown参照。このクラス自身はハンマリングノートの消費に
    /// 関与しない、常に通常のPlaytestEngine.KeyDown任せでよい)。
    /// </summary>
    public bool IsSimultaneousWithPick(int fretLane)
    {
        var pick = Head(PickLane);
        var fret = Head(fretLane);
        return pick is not null && fret is not null && pick.Value.Tick == fret.Value.Tick;
    }

    /// <summary>
    /// 2026-09-27c追加(Phase4描画用): tickの位置にある、いずれかのフレットレーンの未判定ノート
    /// (矢印またはフリーズ始点)のレーン番号一覧(std_gt.js _gtrCollectMembers相当)。
    /// <see cref="IsSimultaneousWithPick"/>・PickDown等は「現在の判定対象(先頭ターゲット)」しか見ないが、
    /// 描画は画面上のすべての未判定ノートについて、それぞれのtickでのコード構成を知る必要があるため、
    /// 任意のtickを指定できるこのメソッドを別途用意する。
    /// </summary>
    public IReadOnlyList<int> MembersAtTick(long tick) =>
        FretLanes.Where(j => HasUnresolvedAt(j, tick)).ToArray();

    /// <summary>2026-09-27c追加(Phase4描画用): ピックレーンにtickの位置の未判定矢印があるか
    /// (std_gt.js _gtrVisOnSpawnの「!grp.pick」判定相当。falseなら、そのtickのフレットノートは
    /// ピックを介さない「ハンマリング」ノートとして描画すべきことを表す)。</summary>
    public bool HasPickAt(long tick) => _engine.ArrowsOf(PickLane).Any(a => a.Result is null && a.Tick == tick);

    private bool HasUnresolvedAt(int lane, long tick) =>
        _engine.ArrowsOf(lane).Any(a => a.Result is null && a.Tick == tick) ||
        _engine.FreezesOf(lane).Any(f => f.Result is null && f.StartTick == tick);

    private readonly record struct HeadTarget(long Tick, double Frame);

    /// <summary>レーンjの未判定先頭ターゲット(矢印/フリーズ始点のうち近い方)のtick/frame。
    /// 無ければnull(std_gt.js _gtrHeadTarget相当)。</summary>
    private HeadTarget? Head(int lane)
    {
        var arrow = _engine.ArrowsOf(lane).FirstOrDefault(a => a.Result is null);
        var freeze = _engine.FreezesOf(lane).FirstOrDefault(f => f.Result is null);
        if (arrow is not null && freeze is not null)
            return arrow.Frame <= freeze.StartFrame
                ? new HeadTarget(arrow.Tick, arrow.Frame)
                : new HeadTarget(freeze.StartTick, freeze.StartFrame);
        if (arrow is not null) return new HeadTarget(arrow.Tick, arrow.Frame);
        if (freeze is not null) return new HeadTarget(freeze.StartTick, freeze.StartFrame);
        return null;
    }

    private void OnJudged(JudgeResult r)
    {
        if (_batchFrame != _currentFrame)
        {
            _batchLanes.Clear();
            _batchFrame = _currentFrame;
        }
        _batchLanes.Add(r.Lane);
        _lastResolvedLanes = [.. _batchLanes];
        _lastResolvedFrame = _currentFrame;

        if (r.Judge is PlayJudge.Kita or PlayJudge.Iknai) _freezeEndFrame[r.Lane] = _currentFrame;
    }

    /// <summary>レーンjで現在進行中(判定未確定・ホールド中)のフリーズがあるか
    /// (std_gt.js _gtrLaneHoldingFrz相当)。</summary>
    private bool HoldingFreeze(int lane) => _engine.FreezesOf(lane).Any(f =>
        f.Result is null && f.Started && f.Holding && f.StartFrame <= _currentFrame && _currentFrame < f.EndFrame);

    /// <summary>レーンjで失敗済みフリーズの帯がまだ流れているか
    /// (std_gt.js _gtrLaneFailedFrzDraining相当)。</summary>
    private bool FailedFreezeDraining(int lane) => _engine.FreezesOf(lane).Any(f =>
        f.Result == PlayJudge.Iknai && _currentFrame < f.EndFrame);

    /// <summary>レーンjの押しっぱなしを「離し遅れ」として免除してよいか(std_gt.js forgiven相当)。
    /// need(現在評価中のコードに必要なレーン集合)に含まれるレーンは免除に関係なく実押下が必須なので、
    /// このメソッドはneedに含まれない「余計な押下」の側からのみ呼ぶ。</summary>
    private bool Forgiven(int lane, ISet<int> need)
    {
        if (HoldingFreeze(lane) || FailedFreezeDraining(lane)) return true;
        if (!_settings.ReleaseExemptEnable) return false;

        double freezeEnd = _freezeEndFrame.TryGetValue(lane, out var f) ? f : double.NegativeInfinity;
        if (_currentFrame - freezeEnd <= _settings.FreezeEndGraceFrames) return true;

        return _lastResolvedLanes.Contains(lane) && !need.Contains(lane)
            && _currentFrame - _lastResolvedFrame <= _settings.ReleaseExemptMaxFrames;
    }

    /// <summary>指板照合(std_gt.js _gtrFingerMatch相当)。必要レーン(need)は全て実押下必須、
    /// 必要でないレーンを押している場合は<see cref="Forgiven"/>で免除されない限り不一致。</summary>
    private bool FingerMatch(HashSet<int> need, Func<int, bool> isFretHeld) =>
        need.All(isFretHeld) &&
        FretLanes.Where(j => !need.Contains(j) && isFretHeld(j)).All(j => Forgiven(j, need));

    /// <summary>
    /// ピックレーンのキー押下(std_gt.js _gtrPickDown相当)。
    /// ピック側の未判定先頭ターゲットと同tickのフレットレーンを「コード構成メンバー」として集め、
    /// 指板が一致する場合はピック+全メンバーを同フレームでPlaytestEngine.KeyDownへ渡して消費する。
    /// 不一致の場合は即ミスにせず保留し(ピック先行の猶予)、以後<see cref="Advance"/>で解決を待つ。
    /// 保留中に新たなピック入力があった場合、AllowRepick=falseなら無視する(ダブルピック禁止)。
    /// </summary>
    /// <param name="frame">現在フレーム(呼び出し元の_currentFrame)。</param>
    /// <param name="isFretHeld">フレットレーンindexを受け取り、そのレーンの割当キーが
    /// 現在押されているかを返す。</param>
    public void PickDown(double frame, Func<int, bool> isFretHeld)
    {
        _currentFrame = frame;

        if (_pendingPick is not null && !_settings.AllowRepick) return; // 保留中の追加ピック入力を無視

        _pendingPick = null; // 新しいピックは古い保留を上書き(許可時のみここに到達)

        var target = Head(PickLane);
        if (target is null) return; // 判定対象なし(空ピック) → 素通し(本家同様ペナルティなし)

        var members = FretLanes.Where(j => Head(j) is { } t && t.Tick == target.Value.Tick).ToList();
        var need = new HashSet<int>(members);

        if (FingerMatch(need, isFretHeld))
        {
            _engine.KeyDown(PickLane, frame);
            foreach (var j in members) _engine.KeyDown(j, frame);
            return;
        }

        // 指板不一致: 即ミスピックにせず保留(ピック先行の猶予)。
        _pendingPick = (frame, target.Value.Tick);
    }

    /// <summary>
    /// 毎フレーム呼び出す(std_gt.js _gtrResolvePendingPick+離し遅れ免除追跡相当)。
    /// ピック先行の保留があれば、対象が変わっていないか確認しつつ指板一致を再チェックする。
    /// 一致すればその時点のフレームで確定消費し、猶予(PickEarlyGraceFrames)を超えても
    /// 一致しなければ保留を諦める(以後は通常の枠外タイムアウト判定に任せる)。
    /// 呼び出し元は、境界フレームでの猶予判定を優先させるため、このメソッドを
    /// PlaytestEngine.Advance(frame)より先に呼ぶこと(std_gt.js同様「グループ一括ミスより先に見る」)。
    /// </summary>
    /// <param name="frame">現在フレーム(呼び出し元の_currentFrame)。</param>
    /// <param name="isFretHeld">フレットレーンindexを受け取り、そのレーンの割当キーが
    /// 現在押されているかを返す。</param>
    public void Advance(double frame, Func<int, bool> isFretHeld)
    {
        _currentFrame = frame;
        if (_pendingPick is null) return;

        var (pendingFrame, pendingTick) = _pendingPick.Value;
        var target = Head(PickLane);
        if (target is null || target.Value.Tick != pendingTick)
        {
            _pendingPick = null; // 対象が既に別経路で解決済み(自然タイムアウト等) → 保留を諦める
            return;
        }

        var members = FretLanes.Where(j => Head(j) is { } t && t.Tick == pendingTick).ToList();
        var need = new HashSet<int>(members);

        if (FingerMatch(need, isFretHeld))
        {
            _pendingPick = null;
            _engine.KeyDown(PickLane, frame);
            foreach (var j in members) _engine.KeyDown(j, frame);
            return;
        }

        if (frame - pendingFrame >= _settings.PickEarlyGraceFrames)
            _pendingPick = null; // 猶予超過 → 保留を諦める(以後は自然なタイムアウト判定に任せる)
    }
}
