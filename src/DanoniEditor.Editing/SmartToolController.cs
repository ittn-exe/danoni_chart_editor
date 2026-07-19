using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>WPF非依存の座標点(px単位、ChartLayoutと同じ座標系)</summary>
public readonly record struct PointerPos(double X, double Y)
{
    public double DistanceTo(PointerPos other) =>
        Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

[Flags]
public enum PointerModifiers { None = 0, Shift = 1, Ctrl = 2 }

/// <summary>マウスボタン種別(セッション中の識別用)</summary>
public enum PointerButton { Left, Right }

/// <summary>ドラッグ確定後のジェスチャ種別(内部状態)</summary>
internal enum DragGesture { None, ResizeFreeze, MoveObjects, DragDelete, RectSelect }

/// <summary>
/// 譜面編集エリアのスマートツール操作ステートマシン(仕様書6.3.1/6.3.2/7.4/7.5)。
/// WPF側はマウスイベントをBeginLeft/BeginRight → Move(複数回) → End に変換して渡すだけでよい。
/// 1回のBegin→Endが1ジェスチャ=1Undoアクション(仕様書13章)。
/// クリックとドラッグの判定はDragThreshold(既定3px)で行う。
/// </summary>
public sealed class SmartToolController
{
    public const double DragThreshold = 3.0;

    private readonly EditorDocument _doc;

    /// <summary>スマートツールON/OFF(仕様書6.3上段パネル)。OFF時は選択済みオブジェクトのグループ移動のみ有効。</summary>
    public bool SmartToolEnabled { get; set; } = true;

    /// <summary>マーカーレーンクリックで設定される「現在フレーム」相当のtick位置(仕様書7.4)</summary>
    public long? CurrentTick { get; private set; }

    /// <summary>CurrentTickが変化した時に発火(WPF側でプレイヘッド表示更新用)</summary>
    public event Action? CurrentTickChanged;

    // --- セッション状態(Begin〜Endの間だけ有効) ---
    private PointerButton _button;
    private PointerModifiers _modifiers;
    private PointerPos _startPos;
    private PointerPos _lastPos;
    private ColumnInfo? _startColumn;
    private ObjectRef? _startHit;
    private DragGesture _gesture;
    private bool _sessionActive;
    private bool _dragConfirmed;
    /// <summary>この押下セッションのクリック処理を既に押下時(Begin)に実行済みか(2026-07-17e)</summary>
    private bool _clickHandledOnDown;
    private readonly HashSet<ObjectRef> _dragDeleteTouched = [];

    public SmartToolController(EditorDocument doc) => _doc = doc;

    /// <summary>現在ドラッグ中(閾値超過済み)かどうか(WPF側のプレビュー描画判定用)</summary>
    public bool IsDragging => _sessionActive && _dragConfirmed;

    /// <summary>矩形選択中のプレビュー範囲(RectSelect中のみ非null、WPF側の破線描画用)</summary>
    public (PointerPos Start, PointerPos Current)? RectSelectPreview =>
        _sessionActive && _dragConfirmed && _gesture == DragGesture.RectSelect ? (_startPos, _lastPos) : null;

    /// <summary>右ドラッグ連続削除(DragDelete)中、これまでにドラッグパスが触れた=削除対象になった
    /// オブジェクトの集合(WPF側で「削除対象であるとわかる」持続的なマーキング描画用、2026-07-16j追加)。
    /// DragDelete確定中のみ非null。Endで確定削除されるまではモデルは一切変更されない。</summary>
    public IReadOnlyCollection<ObjectRef>? DragDeleteTouchedPreview =>
        _sessionActive && _dragConfirmed && _gesture == DragGesture.DragDelete ? _dragDeleteTouched : null;

    /// <summary>
    /// MoveObjectsドラッグ中のプレビュー情報(WPF側でカーソル追従のゴースト描画に使う)。
    /// モデルは一切変更しない(実際の移動は従来通りEndで確定、1ジェスチャ=1Undoアクション)。
    /// ドラッグ確定前・別ジェスチャ中はnull。2026-07-16h追加: 「掴んでいるオブジェクトが離すまで
    /// マウスに追従して見えない」という指摘への対応(以前はEnd時に一度だけ位置が飛んでいた)。
    /// </summary>
    public (IReadOnlyList<ObjectRef> Targets, int LaneDelta, long TickDelta)? MoveObjectsPreview
    {
        get
        {
            if (!(_sessionActive && _dragConfirmed && _gesture == DragGesture.MoveObjects)) return null;
            if (_startHit is not { } hit) return null;
            var targets = IsInSelection(hit) && _doc.Selection.Count > 0
                ? (IReadOnlyList<ObjectRef>)[.. _doc.Selection]
                : [hit];
            int laneDelta = LaneDeltaFor(hit.Kind);
            long tickDelta = SnappedTickAt(_lastPos) - SnappedTickAt(_startPos);
            return (targets, laneDelta, tickDelta);
        }
    }

    /// <summary>フリーズリサイズドラッグ中のプレビュー情報(同上、モデル未変更)。</summary>
    public (int Lane, long StartTick, long EndTick)? ResizeFreezePreview
    {
        get
        {
            if (!(_sessionActive && _dragConfirmed && _gesture == DragGesture.ResizeFreeze)) return null;
            if (_startHit is not { } hit || _startColumn is not { Kind: ColumnKind.Note } col) return null;
            var lane = _doc.CurrentTab.Lanes[col.NoteLaneIndex];
            var old = lane.Freezes.FirstOrDefault(f => f.StartTick == hit.Tick);
            if (old is null) return null;
            var newTick = SnappedTickAt(_lastPos);
            var (rawStart, rawEnd) = hit.Kind == ObjectKind.FreezeStart ? (newTick, old.EndTick) : (old.StartTick, newTick);
            // 2026-07-16i クラッシュ修正: 始点を終点より後ろへ(またはその逆に)ドラッグすると
            // end<start の負の範囲になり、ChartCanvas側でRect(height<0)を作ろうとして
            // ArgumentExceptionで落ちていた。コミット時のResizeFreezeActionと同じClampResizeを
            // プレビュー段階にも適用し、常にstart<endを保証する。
            var (s, e) = FreezeRules.ClampResize(rawStart, rawEnd);
            return (col.NoteLaneIndex, s, e);
        }
    }

    // =====================================================================
    // セッション開始
    // =====================================================================

    public void BeginLeft(PointerPos pos, PointerModifiers modifiers) => Begin(PointerButton.Left, pos, modifiers);
    public void BeginRight(PointerPos pos, PointerModifiers modifiers) => Begin(PointerButton.Right, pos, modifiers);

    /// <summary>ホイールクリック(中ボタン)でフリーズアローを配置する(2026-07-17: FUJIエディタに
    /// 同機能があるとの要望対応)。ドラッグは伴わない単発操作。ノートレーン上・既存オブジェクトが
    /// 無い位置でのみ、既定長(現在のスナップ間隔)のフリーズを1件配置する。</summary>
    public void MiddleClick(PointerPos pos)
    {
        if (!SmartToolEnabled) return;
        var col = _doc.CurrentLayout.ColumnAt(pos.X);
        if (col is not { Kind: ColumnKind.Note }) return;
        if (HitAt(pos, hitScale: 1.0) is not null) return; // 既存オブジェクト上では何もしない(誤操作防止)
        long tick = SnappedTickAt(pos);
        _doc.Execute(new PlaceFreezeAction(col.NoteLaneIndex, tick, tick + _doc.Snap.GridTicks));
    }

    private void Begin(PointerButton button, PointerPos pos, PointerModifiers modifiers)
    {
        _button = button;
        _modifiers = modifiers;
        _startPos = _lastPos = pos;
        _startColumn = _doc.CurrentLayout.ColumnAt(pos.X);
        _startHit = HitAt(pos, hitScale: 1.0);
        _gesture = DragGesture.None;
        _sessionActive = true;
        _dragConfirmed = false;
        _dragDeleteTouched.Clear();
        _clickHandledOnDown = false;

        // 2026-07-17e: クリック応答改善(「1つ置いてから次を置けるまでが長い」との指摘対応)。
        // 空セルへの左クリック配置(および空マーカーレーンのカレントtick設定/Shift+マーカー配置)は、
        // ボタンを離すまで待たず押下の瞬間に確定させる。空セルからの左ドラッグは元々ジェスチャ無し
        // (DetermineGestureがDragGesture.Noneを返す)ため、押下時に確定しても既存のドラッグ操作と
        // 一切競合しない。既存オブジェクト上の押下は従来通りEndまで保留する(クリック=選択/
        // ドラッグ=移動・リサイズの判別がボタンアップまで確定しないため)。
        if (button == PointerButton.Left && _startHit is null)
            _clickHandledOnDown = TryHandleEmptyLeftPress();
    }

    /// <summary>空セル(既存オブジェクト無し)への左ボタン押下を即時処理する(2026-07-17e)。
    /// 処理を実行した場合はtrueを返し、End側のHandleClickは何も行わない。</summary>
    private bool TryHandleEmptyLeftPress()
    {
        var col = _startColumn;
        if (col is null) return false;
        bool shift = _modifiers.HasFlag(PointerModifiers.Shift);

        if (col.Kind == ColumnKind.Marker)
        {
            if (shift) { _doc.Execute(new PlaceMarkerAction(SnappedTickAt(_startPos))); return true; }
            SetCurrentTick(TickAt(_startPos));
            return true;
        }

        if (!SmartToolEnabled) return false;
        var action = BuildPlaceAction(col, SnappedTickAt(_startPos), shift);
        if (action is null) return false;
        _doc.Execute(action);
        return true;
    }

    // =====================================================================
    // ドラッグ中
    // =====================================================================

    public void Move(PointerPos pos)
    {
        if (!_sessionActive) return;
        _lastPos = pos;

        if (!_dragConfirmed)
        {
            if (_startPos.DistanceTo(pos) < DragThreshold) return;
            _dragConfirmed = true;
            _gesture = DetermineGesture();

            if (_gesture == DragGesture.DragDelete)
            {
                // ドラッグ開始点(押下位置)もパスの一部として削除対象に含める
                var startHit = HitAt(_startPos, hitScale: 0.5);
                if (startHit is { } sh) _dragDeleteTouched.Add(sh);
            }
        }

        if (_gesture == DragGesture.DragDelete)
        {
            var hit = HitAt(pos, hitScale: 0.5);
            if (hit is { } h) _dragDeleteTouched.Add(h);
        }
    }

    private DragGesture DetermineGesture()
    {
        if (_button == PointerButton.Left)
        {
            if (!SmartToolEnabled)
                // OFF時は「選択済みオブジェクトのドラッグ=グループ移動」のみ有効(仕様書6.3.2)
                return _startHit is { } h0 && IsInSelection(h0) ? DragGesture.MoveObjects : DragGesture.None;

            if (_startHit is not { } h) return DragGesture.None; // 空からの左ドラッグは何もしない
            return h.Kind is ObjectKind.FreezeStart or ObjectKind.FreezeEnd
                ? DragGesture.ResizeFreeze
                : DragGesture.MoveObjects;
        }
        else
        {
            if (!SmartToolEnabled) return DragGesture.None;
            return _startHit is not null ? DragGesture.DragDelete : DragGesture.RectSelect;
        }
    }

    private bool IsInSelection(ObjectRef r) => _doc.Selection.Any(s => s.SameEntity(r)) || _doc.Selection.Contains(r);

    // =====================================================================
    // セッション終了
    // =====================================================================

    public void End(PointerPos pos)
    {
        if (!_sessionActive) return;
        _lastPos = pos;
        var wasDrag = _dragConfirmed || _startPos.DistanceTo(pos) >= DragThreshold;

        if (!wasDrag)
            HandleClick();
        else
            HandleDragEnd();

        _sessionActive = false;
        _dragConfirmed = false;
        _gesture = DragGesture.None;
        _dragDeleteTouched.Clear();
        _startHit = null;
        _startColumn = null;
    }

    // --- クリック(ドラッグ閾値未満) ---

    private void HandleClick()
    {
        if (_button == PointerButton.Left) HandleLeftClick();
        else HandleRightClick();
    }

    private void HandleLeftClick()
    {
        // 空セルへの配置・カレントtick設定は押下時(Begin)に処理済み(2026-07-17e)。
        // ここに残るのは「既存オブジェクト上のクリック=選択」のみ。
        if (_clickHandledOnDown) return;
        if (_startHit is { } existing) SelectSingle(existing);
        // 押下時に処理されなかった空セル(レーン外・スマートツールOFF・tick0のBPM等)は何もしない
    }

    private void HandleRightClick()
    {
        if (_startHit is not { } hit) return;
        if (!SmartToolEnabled) return;
        var action = BuildDeleteAction(hit);
        if (action is null) return;
        _doc.Execute(action);
        RemoveFromSelection(hit);
    }

    // --- ドラッグ確定後 ---

    private void HandleDragEnd()
    {
        switch (_gesture)
        {
            case DragGesture.ResizeFreeze: FinishResize(); break;
            case DragGesture.MoveObjects: FinishMove(); break;
            case DragGesture.DragDelete: FinishDragDelete(); break;
            case DragGesture.RectSelect: FinishRectSelect(); break;
        }
    }

    private void FinishResize()
    {
        if (_startHit is not { } hit || _startColumn is not { Kind: ColumnKind.Note } col) return;
        var lane = _doc.CurrentTab.Lanes[col.NoteLaneIndex];
        var old = lane.Freezes.FirstOrDefault(f => f.StartTick == hit.Tick);
        if (old is null) return;

        var newTick = SnappedTickAt(_lastPos);
        var (newStart, newEnd) = hit.Kind == ObjectKind.FreezeStart
            ? (newTick, old.EndTick)
            : (old.StartTick, newTick);

        _doc.Execute(new ResizeFreezeAction(col.NoteLaneIndex, old, newStart, newEnd));
    }

    private void FinishMove()
    {
        if (_startHit is not { } hit) return;

        var targets = IsInSelection(hit) && _doc.Selection.Count > 0
            ? (IReadOnlyList<ObjectRef>)[.. _doc.Selection]
            : [hit];

        int laneDelta = LaneDeltaFor(hit.Kind);
        long tickDelta = SnappedTickAt(_lastPos) - SnappedTickAt(_startPos);
        if (laneDelta == 0 && tickDelta == 0) return;

        _doc.Execute(new MoveObjectsAction(targets, laneDelta, tickDelta));
    }

    private int LaneDeltaFor(ObjectKind kind)
    {
        if (kind is not (ObjectKind.Note or ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody))
            return 0; // イベント系(speed/boost/BPM/marker)はtickのみ(仕様書6.3.2)
        var startCol = _doc.CurrentLayout.ColumnAt(_startPos.X);
        var endCol = _doc.CurrentLayout.ColumnAt(_lastPos.X);
        if (startCol is not { Kind: ColumnKind.Note } || endCol is not { Kind: ColumnKind.Note }) return 0;
        return endCol.NoteLaneIndex - startCol.NoteLaneIndex;
    }

    private void FinishDragDelete()
    {
        if (_dragDeleteTouched.Count == 0) return;
        var actions = _dragDeleteTouched
            .Select(BuildDeleteAction)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
        if (actions.Count == 0) return;
        _doc.Execute(new CompositeEditAction(actions, "ドラッグ削除"));

        // 2026-07-17: 削除したオブジェクトが選択中だった場合、選択枠(黄色い縁取り)が
        // 実体の消えた位置に残り続ける不具合の対応。削除対象と同一エンティティの選択を解除する。
        foreach (var touched in _dragDeleteTouched) RemoveFromSelection(touched);
    }

    /// <summary>削除されたオブジェクトと同一エンティティの選択を解く(2026-07-17)。
    /// フリーズは始点/終点/胴体のどのRefで選択されていてもSameEntityで拾う。</summary>
    private void RemoveFromSelection(ObjectRef deleted)
    {
        if (_doc.Selection.Count == 0) return;
        int removed = _doc.Selection.RemoveWhere(s => s.SameEntity(deleted));
        if (removed > 0) _doc.NotifyChanged(markModified: false); // 選択変更のみ(2026-07-19b)
    }

    private void FinishRectSelect()
    {
        var tab = _doc.CurrentTab;
        var refs = _doc.CurrentLayout.ObjectsInRect(tab, _doc.Project, _startPos.X, _startPos.Y, _lastPos.X, _lastPos.Y);
        _doc.Selection.Clear();
        foreach (var r in refs) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false); // 選択変更のみ(2026-07-19b)
    }

    // =====================================================================
    // 外部トリガ操作(ダブルクリック/キーボード)
    // =====================================================================

    /// <summary>マーカーレーンのダブルクリックで「再生開始フレーム」を設定する(2026-07-17f、未解決事項§2-2)。
    /// 処理した場合true。それ以外の列ではfalseを返し、呼び出し側は通常のクリックとして扱う。
    /// 1回目のクリック(押下即処理)はカレントtick設定で冪等なため、シングルクリックの取り消しは不要。
    /// 譜面内容ではなく再生設定のためUndo対象外。</summary>
    public bool DoubleLeft(PointerPos pos)
    {
        var col = _doc.CurrentLayout.ColumnAt(pos.X);
        if (col is not { Kind: ColumnKind.Marker }) return false;
        long tick = SnappedTickAt(pos);
        var engine = _doc.Project.CreateTimingEngine();
        _doc.Project.PlaybackStartFrame = engine.TickToFrame(tick);
        _doc.NotifyChanged();
        return true;
    }

    /// <summary>選択中の全オブジェクトを削除する(Deleteキー、2026-07-17f、未解決事項§2-3)。
    /// 1回の呼び出し=1Undoアクション。同一エンティティの重複参照(フリーズの始点と終点が
    /// 両方選択されている等)はSameEntityで除去してから削除する。削除対象が無ければfalse。</summary>
    public bool DeleteSelection()
    {
        if (_doc.Selection.Count == 0) return false;

        var unique = new List<ObjectRef>();
        foreach (var r in _doc.Selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var actions = unique
            .Select(BuildDeleteAction)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
        if (actions.Count == 0) return false;

        _doc.Execute(new CompositeEditAction(actions, "選択削除"));
        _doc.Selection.Clear();
        _doc.NotifyChanged(markModified: false); // Execute側で変更済み、こちらは選択解除の通知のみ
        return true;
    }

    // =====================================================================
    // アクション組み立て
    // =====================================================================

    private IEditAction? BuildPlaceAction(ColumnInfo col, long tick, bool shift)
    {
        switch (col.Kind)
        {
            case ColumnKind.Note:
                return shift
                    ? new PlaceFreezeAction(col.NoteLaneIndex, tick, tick + _doc.Snap.GridTicks)
                    : new PlaceNoteAction(col.NoteLaneIndex, tick);

            case ColumnKind.Speed:
                return new PlaceValueEventAction(ValueEventKind.Speed, tick, 1.0);

            case ColumnKind.Boost:
                return new PlaceValueEventAction(ValueEventKind.Boost, tick, 1.0);

            case ColumnKind.Bpm:
                if (tick == 0) return null; // tick0は既存イベントが不変条件で常に存在
                return new PlaceValueEventAction(ValueEventKind.Bpm, tick, EffectiveBpmAt(tick));

            case ColumnKind.Measure:
                {
                    var engine = _doc.Project.CreateTimingEngine();
                    int measureIndex = NearestMeasureIndex(engine, tick);
                    var sig = engine.SignatureAt(engine.MeasureStartTick(measureIndex));
                    return new PlaceTimeSignatureAction(measureIndex, sig.Numerator, sig.Denominator);
                }

            default:
                return null;
        }
    }

    private IEditAction? BuildDeleteAction(ObjectRef r) => r.Kind switch
    {
        ObjectKind.Note => new DeleteNoteAction(r.Lane, r.Tick),
        ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody => new DeleteFreezeAction(r.Lane, r.Tick),
        ObjectKind.Speed => new DeleteValueEventAction(ValueEventKind.Speed, r.Tick),
        ObjectKind.Boost => new DeleteValueEventAction(ValueEventKind.Boost, r.Tick),
        ObjectKind.Bpm => r.Tick == 0 ? null : new DeleteValueEventAction(ValueEventKind.Bpm, r.Tick),
        ObjectKind.Marker => new DeleteMarkerAction(r.Tick),
        ObjectKind.TimeSignature => new DeleteTimeSignatureAction((int)r.Tick),
        _ => null,
    };

    // =====================================================================
    // 補助
    // =====================================================================

    private ObjectRef? HitAt(PointerPos pos, double hitScale) =>
        _doc.CurrentLayout.HitTest(_doc.CurrentTab, _doc.Project, pos.X, pos.Y, hitScale);

    private long TickAt(PointerPos pos) => (long)Math.Round(_doc.CurrentLayout.YToTick(pos.Y));
    /// <summary>
    /// スナップON時は通常のグリッドスナップ、OFF時は「最寄りの整数フレーム」に丸めたtickを返す
    /// (2026-07-17: OFF時のフリー移動が小数フレーム単位になり扱いづらいとの要望対応。
    /// BPMによっては1tickが1frame未満になるため、tick単位で丸めるだけでは不十分)。
    /// </summary>
    private long SnappedTickAt(PointerPos pos)
    {
        double rawTick = _doc.CurrentLayout.YToTick(pos.Y);
        // フレーム情報モード中は常にフレーム単位スナップ(2026-07-17i、仕様書7.6)。
        // 拍グリッドはBPM当て込み中の「動く側」なので、拍スナップは意味を持たない。
        if (!_doc.IsFrameEditMode && _doc.Snap.Enabled) return _doc.Snap.Snap(rawTick);

        var engine = _doc.Project.CreateTimingEngine();
        long roughTick = Math.Max(0, (long)Math.Round(rawTick));
        double frame = Math.Round(engine.TickToFrame(roughTick));
        double tick = engine.FrameToTick(frame);
        return Math.Max(0, (long)Math.Round(tick));
    }

    private void SelectSingle(ObjectRef r)
    {
        _doc.Selection.Clear();
        _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false); // 選択変更のみ(2026-07-19b)
    }

    private void SetCurrentTick(long tick)
    {
        if (CurrentTick == tick) return;
        CurrentTick = tick;
        CurrentTickChanged?.Invoke();
    }

    private double EffectiveBpmAt(long tick)
    {
        var events = _doc.Project.BpmEvents.OrderBy(e => e.Tick).ToList();
        var current = events[0];
        foreach (var e in events)
        {
            if (e.Tick > tick) break;
            current = e;
        }
        return current.Bpm;
    }

    /// <summary>物理小節頭にのみ拍子オブジェクトを置けるため、最も近い小節頭のindexへクランプする(仕様書7.5)</summary>
    private static int NearestMeasureIndex(TimingEngine engine, long tick)
    {
        var (measure, tickInMeasure) = engine.TickToMeasurePosition(tick);
        if (tickInMeasure == 0) return measure;
        var thisHead = engine.MeasureStartTick(measure);
        var nextHead = engine.MeasureStartTick(measure + 1);
        return (tick - thisHead) <= (nextHead - tick) ? measure : measure + 1;
    }
}
