using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>
/// SKB操作モード(キーボード操作、2026-07-21確定仕様)のコントローラ。WPF非依存(Editing層)。
///
/// - カレントタイミングは<see cref="EditorDocument.Project"/>の<c>PlaybackStartFrame</c>を流用する
///   (常に設定済みとして扱う。モード開始時にnullならtick0で初期化)。
/// - カーソル移動(↑/Space=前進、↓/B=後退)は<see cref="EditorDocument.Snap"/>のグリッド単位(GridTicks)。
/// - ノート入力キーは1回の押下でトグル(存在すれば削除、無ければ配置)。
/// - 同時押し判定はSKBエディタの実装(Date.now()ベース、閾値内は「直前の入力位置」を再利用しカーソルを
///   進めない、閾値超えは「現在のカーソル位置」を新規入力位置として記録しカーソルを進める)に準拠。
///   閾値は<see cref="ThresholdMs"/>(既定30ms、環境設定で変更可、AppSettings側から都度反映)。
/// - フリーズはShift+ノートキーで、SKBの奇偶パリティ方式ではなく、レーンごとの明示的な
///   開始/完了ステートマシン(このコードベースの<c>FreezeNote(StartTick, EndTick)</c>モデルに合わせる)。
/// - Backspace(キーボードモード中のみ、SKB準拠)は現在のカーソル位置にある全レーンのノート/フリーズを削除する
///   (マウスモード中のBackspaceは既存仕様のまま = PlaybackStartFrameのクリアであり、本コントローラでは扱わない)。
/// </summary>
public sealed class KeyboardModeController
{
    private readonly EditorDocument _doc;

    /// <summary>レーンごとの未完了フリーズ開始tick(Shift+ノートキー1回目で登録、2回目で確定)</summary>
    private readonly Dictionary<int, long> _pendingFreezeStart = [];

    private DateTime? _lastPressAt;
    private long _lastChordTick;

    public KeyboardModeController(EditorDocument doc) => _doc = doc;

    /// <summary>同時押しとみなす時間閾値(ms)。既定30、AppSettings.SimultaneousPressThresholdMsから反映する想定。</summary>
    public double ThresholdMs { get; set; } = 30;

    /// <summary>レーンごとの未完了フリーズ開始tick(UI側でのカーソル表示・警告表示などに利用可)</summary>
    public IReadOnlyDictionary<int, long> PendingFreezeStarts => _pendingFreezeStart;

    /// <summary>現在のカーソルtick(PlaybackStartFrameから逆算)</summary>
    public long CursorTick => CurrentCursorTick(_doc.Project.CreateTimingEngine());

    /// <summary>キーボードモード開始時に呼ぶ。PlaybackStartFrame未設定ならtick0で初期化する。</summary>
    public void EnterMode()
    {
        if (_doc.Project.PlaybackStartFrame is not null) return;
        var engine = _doc.Project.CreateTimingEngine();
        _doc.Project.PlaybackStartFrame = engine.TickToFrame(0);
        _doc.NotifyChanged(markModified: false);
    }

    /// <summary>カーソルを1グリッド分移動する(forward=trueで時間前進)。
    /// 2026-07-26: キー→時間方向のマッピングは呼び出し元(MainWindow)が譜面ビューのReverse設定を
    /// 参照して「画面上の見た目方向」基準で決定する(通常表示: ↑/B=forward:false、↓/Space=forward:true、
    /// Reverse表示: 全キー反転)。本メソッド自体は時間方向のみを扱いReverseを関知しない。
    /// ユーザーによる明示的な移動のため、同時押し判定の直前入力記録はリセットする。</summary>
    public void MoveCursor(bool forward)
    {
        var engine = _doc.Project.CreateTimingEngine();
        long cur = CurrentCursorTick(engine);
        long next;
        if (_doc.Snap.Enabled)
        {
            long step = _doc.Snap.GridTicks;
            long gridNext = forward ? cur + step : Math.Max(0, cur - step);
            // 2026-08-01要望対応: 現在位置と移動先グリッドの間に、グリッド上には無いノート
            // (例: 12分で入力後に16分グリッドへ戻した場合等)が存在する場合は、グリッド線ではなく
            // そのノートのタイミングへ移動する(グリッド外ノートへ辿り着く手段が無かった不便さの解消)。
            next = FindOffGridNoteBetween(cur, gridNext, forward) ?? gridNext;
        }
        else
        {
            // 2026-08-01不具合修正: スナップOFF時はグリッドという概念が無意味なため、
            // マウス操作時のOFF時挙動(SmartToolController.SnappedTickAt)に合わせて
            // 最寄りの整数フレーム単位で1段階だけ前後させる。
            next = StepByOneFrame(engine, cur, forward);
        }
        ApplyExplicitCursorMove(engine, next);
    }

    /// <summary>MoveCursor専用(2026-08-01): fromExclusive〜toExclusiveの開区間(順不同で渡してよい)に
    /// ある全レーンの通常ノート/フリーズ端点のうち、移動方向(forward)側から見て最も近いtickを返す。
    /// 区間内に見つかった時点でそれは必然的にグリッド外ノート(区間内には他のグリッド線が存在しない
    /// 1グリッド分の幅のため)。無ければnull(=通常通りグリッド線へ移動)。</summary>
    private long? FindOffGridNoteBetween(long fromExclusive, long toExclusive, bool forward)
    {
        long lo = Math.Min(fromExclusive, toExclusive);
        long hi = Math.Max(fromExclusive, toExclusive);
        long? best = null;

        void Consider(long t)
        {
            if (t <= lo || t >= hi) return;
            if (best is null || (forward ? t < best.Value : t > best.Value)) best = t;
        }

        foreach (var lane in _doc.CurrentTab.Lanes)
        {
            foreach (var t in lane.Notes) Consider(t);
            foreach (var f in lane.Freezes)
            {
                Consider(f.StartTick);
                Consider(f.EndTick);
            }
        }
        return best;
    }

    /// <summary>カーソルを小節単位で移動する(2026-07-21追加: →/Ctrl+←→/Shift+Ctrl+←→ショートカット)。
    /// measureDeltaは符号付きの小節数(2小節/4小節移動、および→の1小節移動もこちらを使う)。
    /// カーソルが小節の途中にあっても、必ず「現在の小節index + measureDelta」の小節の頭へ着地する
    /// (2026-07-22確定: 移動先の頭に合わせるのが正、起点側のオフセットは考慮しない)。
    /// 移動先は現在の小節を基準にTimingEngine.MeasureStartTickで求めた小節の頭(拍子変化を跨いでも正確)。
    /// 小節0より前には移動しない。ユーザーによる明示的な移動のため、同時押し判定の直前入力記録はリセットする。</summary>
    public void MoveCursorByMeasure(int measureDelta)
    {
        var engine = _doc.Project.CreateTimingEngine();
        long cur = CurrentCursorTick(engine);
        var (measure, _) = engine.TickToMeasurePosition(cur);
        int targetMeasure = Math.Max(0, measure + measureDelta);
        ApplyExplicitCursorMove(engine, engine.MeasureStartTick(targetMeasure));
    }

    /// <summary>←(1小節・上方向)専用の移動(2026-07-22確定仕様、SKBに無い独自挙動)。
    /// 単純に「1つ前の小節の頭」へ飛ぶのではなく、DAW等の「戻る」操作に倣った2段階の挙動にする。
    /// - カーソルが小節の途中にある場合: まず現在の小節の頭へ戻る(1つ前の小節へは進まない)。
    /// - カーソルが既に小節の頭にある場合: 1つ前の小節の頭へ移動する(小節0より前には行かない)。
    /// →(1小節・下方向)や2小節/4小節移動(<see cref="MoveCursorByMeasure"/>)はこの特別扱いの対象外で、
    /// 常に「移動先の小節の頭」へ無条件に合わせる。</summary>
    public void MoveCursorToPreviousMeasureOrCurrentStart()
    {
        var engine = _doc.Project.CreateTimingEngine();
        long cur = CurrentCursorTick(engine);
        var (measure, tickInMeasure) = engine.TickToMeasurePosition(cur);
        long target = tickInMeasure != 0
            ? engine.MeasureStartTick(measure)              // 小節途中 → まず現在の小節の頭へ
            : engine.MeasureStartTick(Math.Max(0, measure - 1)); // 既に小節頭 → 1つ前の小節の頭へ
        ApplyExplicitCursorMove(engine, target);
    }

    /// <summary>ノート入力キー押下(トグル)。lane範囲外はfalseを返す。</summary>
    public bool ToggleNoteAtCursor(int lane, DateTime now)
    {
        if (lane < 0 || lane >= _doc.CurrentTab.Lanes.Count) return false;
        var (tick, shouldAdvance) = ResolvePressTick(now);
        bool existed = _doc.CurrentTab.Lanes[lane].Notes.Contains(tick);
        _doc.Execute(existed ? new DeleteNoteAction(lane, tick) : new PlaceNoteAction(lane, tick));
        if (shouldAdvance) AdvanceCursorAfterInput();
        return true;
    }

    /// <summary>Shift+ノート入力キー押下(フリーズ開始/完了)。lane範囲外はfalseを返す。
    /// 1回目=開始点登録のみ(Undo対象外、非破壊)、2回目=区間確定してPlaceFreezeAction実行。
    /// 開始と完了が同一tickの場合はフリーズ化せずキャンセル扱いとする(0長フリーズを防ぐための仕様判断)。</summary>
    public bool ToggleFreezeAtCursor(int lane, DateTime now)
    {
        if (lane < 0 || lane >= _doc.CurrentTab.Lanes.Count) return false;
        var (tick, shouldAdvance) = ResolvePressTick(now);

        if (_pendingFreezeStart.TryGetValue(lane, out var startTick))
        {
            _pendingFreezeStart.Remove(lane);
            if (tick != startTick)
            {
                long s = Math.Min(startTick, tick);
                long e = Math.Max(startTick, tick);
                _doc.Execute(new PlaceFreezeAction(lane, s, e));
            }
            else
            {
                _doc.NotifyChanged(markModified: false); // 開始点キャンセルのみでも状態変化として通知
            }
        }
        else
        {
            _pendingFreezeStart[lane] = tick;
            _doc.NotifyChanged(markModified: false);
        }

        if (shouldAdvance) AdvanceCursorAfterInput();
        return true;
    }

    /// <summary>Backspace(キーボードモード中、SKB準拠)。カーソル位置にある全レーンのノート/フリーズ始点、
    /// および未完了フリーズ開始点を削除する。何も無ければfalse。</summary>
    public bool DeleteAtCursor()
    {
        var engine = _doc.Project.CreateTimingEngine();
        long tick = CurrentCursorTick(engine);
        var tab = _doc.CurrentTab;
        var actions = new List<IEditAction>();
        bool clearedPending = false;

        for (int lane = 0; lane < tab.Lanes.Count; lane++)
        {
            if (tab.Lanes[lane].Notes.Contains(tick))
                actions.Add(new DeleteNoteAction(lane, tick));
            if (tab.Lanes[lane].Freezes.Any(f => f.StartTick == tick))
                actions.Add(new DeleteFreezeAction(lane, tick));
            if (_pendingFreezeStart.TryGetValue(lane, out var pending) && pending == tick)
            {
                _pendingFreezeStart.Remove(lane);
                clearedPending = true;
            }
        }

        if (actions.Count > 0)
        {
            _doc.Execute(new CompositeEditAction(actions, "カーソル位置削除(キーボードモード)"));
            return true;
        }
        if (clearedPending)
        {
            _doc.NotifyChanged(markModified: false);
            return true;
        }
        return false;
    }

    // =====================================================================
    // 内部ヘルパ
    // =====================================================================

    private long CurrentCursorTick(TimingEngine engine) =>
        _doc.Project.PlaybackStartFrame is { } f ? (long)Math.Round(engine.FrameToTick(f)) : 0;

    /// <summary>ユーザーによる明示的なカーソル移動の共通処理(MoveCursor/MoveCursorByMeasure/
    /// MoveCursorToPreviousMeasureOrCurrentStartで共用)。同時押し判定の直前入力記録をリセットする。</summary>
    private void ApplyExplicitCursorMove(TimingEngine engine, long tick)
    {
        _doc.Project.PlaybackStartFrame = engine.TickToFrame(tick);
        _lastPressAt = null;
        _doc.NotifyChanged(markModified: false);
    }

    /// <summary>同時押し判定込みで、今回の入力に使うtickを決定する。
    /// 閾値内(直前の押下からThresholdMs以内)なら直前チョードのtickを再利用してカーソルを進めない。
    /// 閾値超えなら現在のカーソル位置を新規チョードのtickとして記録し、カーソルを進める対象とする。</summary>
    private (long Tick, bool ShouldAdvance) ResolvePressTick(DateTime now)
    {
        var engine = _doc.Project.CreateTimingEngine();
        bool simultaneous = _lastPressAt is { } last && (now - last).TotalMilliseconds <= ThresholdMs;
        long tick = simultaneous ? _lastChordTick : CurrentCursorTick(engine);
        _lastPressAt = now;
        if (!simultaneous) _lastChordTick = tick;
        return (tick, !simultaneous);
    }

    /// <summary>ノート/フリーズ入力(同時押しでない新規チョード)の後にカーソルを1グリッド進める。
    /// MoveCursorと異なり、同時押し判定の直前入力記録(_lastPressAt/_lastChordTick)はリセットしない。</summary>
    private void AdvanceCursorAfterInput()
    {
        var engine = _doc.Project.CreateTimingEngine();
        long cur = CurrentCursorTick(engine);
        // 2026-08-01不具合修正: MoveCursorと同様、スナップOFF時はGridTicks固定ではなく
        // 最寄りの整数フレーム単位で進める。
        long next = _doc.Snap.Enabled ? cur + _doc.Snap.GridTicks : StepByOneFrame(engine, cur, forward: true);
        _doc.Project.PlaybackStartFrame = engine.TickToFrame(next);
        _doc.NotifyChanged(markModified: false);
    }

    /// <summary>スナップOFF時のカーソル1段階移動量。グリッドという概念が意味を持たないため、
    /// マウス操作時のOFF時挙動(SmartToolController.SnappedTickAt)に合わせ、最寄りの整数フレーム単位で
    /// 前後させる(2026-08-01: スナップをオフにしてもキーボードモードのカーソル移動だけはグリッド単位の
    /// ままだった不具合の修正)。</summary>
    private static long StepByOneFrame(TimingEngine engine, long currentTick, bool forward)
    {
        double frame = Math.Round(engine.TickToFrame(currentTick));
        double nextFrame = forward ? frame + 1 : frame - 1;
        long nextTick = (long)Math.Round(engine.FrameToTick(nextFrame));
        return Math.Max(0, nextTick);
    }
}
