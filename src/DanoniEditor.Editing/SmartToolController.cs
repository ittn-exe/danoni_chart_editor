using DanoniEditor.Core.Models;
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

/// <summary>Shift+Ctrl+A(全選択)の対象種別(2026-07-21、環境設定で個別ON/OFF可能)</summary>
public readonly record struct SelectAllOptions(
    bool Note, bool Freeze, bool Speed, bool Boost, bool Bpm, bool TimeSignature, bool Marker);

/// <summary>マウスボタン種別(セッション中の識別用)</summary>
public enum PointerButton { Left, Right }

/// <summary>ドラッグ確定後のジェスチャ種別(内部状態)</summary>
internal enum DragGesture { None, ResizeFreeze, MoveObjects, DragDelete, RectSelect }

/// <summary>色編集モードのサブモード(2026-07-24、「モード内モード」)</summary>
public enum ColorEditSubMode { Normal, FrzHit, Shadow }

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

    /// <summary>色編集モードON/OFF(2026-07-23、TBD「ncolor_data」)。ON中はマーカーレーン以外の
    /// 新規配置・移動・通常削除を一切受け付けず、左クリック=着色、右クリック=着色解除に置き換わる。</summary>
    public bool ColorEditModeEnabled { get; set; }

    /// <summary>色編集モード中に塗る色(右パネルの色/グラデーション設定から都度セットされる)。
    /// nullまたは空文字の間はクリックしても何も起きない。</summary>
    public string? PaintColorCode { get; set; }

    /// <summary>色編集モード中の「即時適用(全体色変化)にする」チェックボックスの状態(2026-07-24)。
    /// trueの間に塗った/一括塗りつぶしたncolor_dataは、本家仕様上「指定フレーム時点で既に
    /// 出現済みの矢印/フリーズも含めて即座に塗り替える」全体色変化として出力される。</summary>
    public bool PaintAllFlag { get; set; }

    /// <summary>色編集モードのサブモード(2026-07-24)。Normal=端点/帯(従来のクリック挙動)、
    /// FrzHit=フリーズのヒット時色(Hit/HitBar/HitShadow)、Shadow=塗りつぶし色(ArrowShadow/
    /// NormalShadow)。ColorEditModeEnabledがtrueの間だけ意味を持つ。</summary>
    public ColorEditSubMode SubMode { get; set; } = ColorEditSubMode.Normal;

    /// <summary>Shadowサブモードで通常ノートに塗る色(ArrowShadow)</summary>
    public string? PaintArrowShadowColor { get; set; }
    /// <summary>Shadowサブモードでフリーズに塗る色(NormalShadow)</summary>
    public string? PaintNormalShadowColor { get; set; }

    /// <summary>FrzHitサブモードでHit(ヒット時端点)を対象に含めるか</summary>
    public bool HitEnabled { get; set; }
    /// <summary>FrzHitサブモードでHitBar(ヒット時帯)を対象に含めるか</summary>
    public bool HitBarEnabled { get; set; }
    /// <summary>FrzHitサブモードでHitShadow(ヒット時塗りつぶし)を対象に含めるか</summary>
    public bool HitShadowEnabled { get; set; }
    /// <summary>FrzHitサブモードで塗るHit色</summary>
    public string? PaintHitColor { get; set; }
    /// <summary>FrzHitサブモードで塗るHitBar色</summary>
    public string? PaintHitBarColor { get; set; }
    /// <summary>FrzHitサブモードで塗るHitShadow色</summary>
    public string? PaintHitShadowColor { get; set; }

    // --- セッション状態(Begin〜Endの間だけ有効) ---
    private PointerButton _button;
    private PointerModifiers _modifiers;
    /// <summary>左ボタンを離した瞬間の修飾キー状態(2026-07-26)。Ctrl+ドラッグ=複製の判定はこちらを
    /// 使う(押下時のCtrl状態=_modifiersではなく、離した時点の状態を見る。ドラッグ中に気が変わって
    /// Ctrlを離しても最終判断に反映されるようにするため)。既定値はBegin時の_modifiersと同じにしておき、
    /// End()が(WPF側の都合等で)呼ばれない特殊ケースでも未初期化のPointerModifiers.Noneにならないようにする。</summary>
    private PointerModifiers _endModifiers;
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
    /// DragDelete確定中のみ非null。Endで確定削除されるまではモデルは一切変更されない。
    /// 2026-07-30要望対応: 色編集モード中はこのジェスチャを流用し、Endで削除ではなく色解除
    /// (FinishColorClearDrag)を行う。プレビュー自体は同じ集合をそのまま使う(WPF側の表示は
    /// 「削除」の見た目のままだが、色編集モード中は実際には色クリアの対象を示すことになる)。</summary>
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
        // 2026-07-23: 色編集モード中はフリーズ即時配置を行わず、Shift+クリック相当(端点・帯を
        // 同時に着色)に置き換える。対象が無ければ何もしない。
        if (ColorEditModeEnabled)
        {
            if (HitAt(pos, hitScale: 1.0) is { } hit) PaintAt(hit, both: true);
            return;
        }

        if (!SmartToolEnabled) return;
        var col = _doc.CurrentLayout.ColumnAt(pos.X);
        if (col is not { Kind: ColumnKind.Note }) return;
        if (HitAt(pos, hitScale: 1.0) is not null) return; // 既存オブジェクト上では何もしない(誤操作防止)
        long tick = SnappedTickAt(pos);
        _doc.Execute(new PlaceFreezeAction(col.NoteLaneIndex, tick, tick + _doc.Snap.GridTicks));
        _doc.RecordStat(EditorStatKind.ObjectsPlaced, 1);
    }

    private void Begin(PointerButton button, PointerPos pos, PointerModifiers modifiers)
    {
        _button = button;
        _modifiers = modifiers;
        _endModifiers = modifiers; // End()が呼ばれるまでの既定値(押下時と同じ状態にしておく)
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
        // 2026-07-25: 「空セルか」の判定はIsEmptyForPlacement参照(ノートレーンは広い当たり判定
        // ではなく厳密tick一致を使う、ノート画像が密集して重なっている場合の配置不能対策)。
        if (button == PointerButton.Left && IsEmptyForPlacement(pos))
            _clickHandledOnDown = TryHandleEmptyLeftPress();
    }

    /// <summary>「ここへ配置してよい空セルか」の判定(2026-07-25)。
    /// ノートレーン(ColumnKind.Note)は、NoteSize基準の広いピクセル当たり判定(HitAt)ではなく、
    /// クリック位置のスナップ後tickに実際のノート/フリーズ端点が存在するかを厳密に見る。
    /// ノート画像は密集すると見た目上で隣接ノートの分まで当たり判定が重なってしまい、実際には
    /// 空いているグリッドマスへ配置できなくなる不具合があったための対応(選択・掴みの当たり判定
    /// である_startHit/HitAt自体はここでは変更しない、既存オブジェクトの掴みやすさは維持する)。
    /// ノートレーン以外の列(Speed/Boost/Bpm/Marker/Word等)は従来通り広い当たり判定で判定する。</summary>
    private bool IsEmptyForPlacement(PointerPos pos)
    {
        if (_startColumn is { Kind: ColumnKind.Note } col)
            return !NoteExistsAtExactTick(col.NoteLaneIndex, SnappedTickAt(pos));
        return _startHit is null;
    }

    /// <summary>指定レーンの指定tickに、通常ノート・フリーズの端点(始点/終点)・フリーズの帯範囲内
    /// (始点〜終点、両端含む)のいずれかが実際に存在するか。IsEmptyForPlacement専用の厳密判定
    /// (2026-07-25)。HitAtと異なりピクセル距離を一切見ない。
    /// 2026-07-26修正: 帯範囲チェックが無く端点ぴったりのtickしか「占有」と判定していなかったため、
    /// フリーズの帯中央付近をクリックすると「空セル」と誤判定され、選択/掴み移動より先に新規ノート
    /// 配置(TryHandleEmptyLeftPress)が押下時点で即実行されてしまっていた(帯でのクリック選択・
    /// ドラッグ移動が機能しなくなる副作用)。通常ノートはフリーズと重ねて置けない仕様のため、
    /// 帯の範囲全体を占有域として扱うのが安全かつ正しい。</summary>
    private bool NoteExistsAtExactTick(int laneIndex, long tick)
    {
        var lane = _doc.CurrentTab.Lanes[laneIndex];
        if (lane.Notes.Contains(tick)) return true;
        foreach (var f in lane.Freezes)
            if (tick >= f.StartTick && tick <= f.EndTick) return true;
        return false;
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
            // 2026-07-26: CurrentTick機能(シングルクリックでの位置記録)は撤去。参照先が無くなった
            // (Pasteの基準点は既に再生開始フレームへ移行済み)ため、Shift+クリックのマーカー配置のみ残す。
            // Shift無しの単純クリックは何もしない(空振り、ダブルクリックの再生開始フレーム設定と競合しない)。
            if (shift)
            {
                _doc.Execute(new PlaceMarkerAction(SnappedTickAt(_startPos)));
                _doc.RecordStat(EditorStatKind.ObjectsPlaced, 1);
                return true;
            }
            return false;
        }

        // 2026-07-23: 色編集モード中はマーカーレーン以外への新規配置を一切受け付けない(誤操作防止)。
        if (ColorEditModeEnabled) return false;

        if (!SmartToolEnabled) return false;
        var action = BuildPlaceAction(col, SnappedTickAt(_startPos), shift);
        if (action is null) return false;
        _doc.Execute(action);
        _doc.RecordStat(EditorStatKind.ObjectsPlaced, 1);
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
        // 2026-07-25: 押下時に既に配置処理を終えている(_clickHandledOnDown)場合、常にNone。
        // IsEmptyForPlacementの厳密tick判定により、ノートレーンでは「_startHit(広い当たり判定)は
        // 隣接ノートを指しているが、実際のtickは空だったので配置した」というケースが起こり得る。
        // ここでガードしないと、そのままドラッグ閾値を超えた際に_startHitの隣接ノートを掴んで
        // 移動を始めてしまい、配置と移動が二重に発生してしまう。
        if (_clickHandledOnDown) return DragGesture.None;

        // 2026-07-23: 色編集モード中は左ドラッグ(移動・リサイズ)を一切無効化する。
        // 2026-07-30要望対応: 右ドラッグは、オブジェクトを始点にした場合のみ通常モードの
        // ドラッグ削除(DragDelete)と同じ「連続して触れたオブジェクトを対象にする」ジェスチャへ
        // 切り替え、確定時(FinishDragDelete)で削除ではなく色情報のクリアを行う。空セルからの
        // 右ドラッグは従来通り範囲選択のまま(一括塗りつぶし用の複数選択構築に必要なため)。
        if (ColorEditModeEnabled)
        {
            if (_button != PointerButton.Right) return DragGesture.None;
            return _startHit is not null ? DragGesture.DragDelete : DragGesture.RectSelect;
        }

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

    /// <summary>endModifiers=ボタンを離した瞬間の修飾キー状態(2026-07-26、省略時は押下時の状態を維持)。
    /// Ctrl+ドラッグ=複製(FinishMove参照)の判定に使う。それ以外の判定(範囲選択への追加等)は
    /// 従来通り押下時の_modifiersを使う(この引数は複製判定専用)。</summary>
    public void End(PointerPos pos, PointerModifiers? endModifiers = null)
    {
        if (!_sessionActive) return;
        _lastPos = pos;
        if (endModifiers is { } em) _endModifiers = em;
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
        // ここに残るのは「既存オブジェクト上のクリック」のみ。
        if (_clickHandledOnDown) return;
        if (_startHit is not { } existing) return;
        // 押下時に処理されなかった空セル(レーン外・スマートツールOFF・色編集モード・tick0のBPM等)は何もしない

        // 2026-07-23: Ctrl+クリックは色編集モードの有無に関わらず「選択への追加/解除」を優先する
        // (色編集モードで複数選択→一括塗りつぶしを組み立てるために必要)。
        // 2026-07-26要望対応: 既に選択済みのオブジェクトをCtrl+クリックした場合は、そのオブジェクトだけ
        // 選択解除する(トグル方式)。
        if (_modifiers.HasFlag(PointerModifiers.Ctrl))
        {
            ToggleSelection(existing);
            return;
        }

        if (ColorEditModeEnabled)
        {
            PaintAt(existing, both: _modifiers.HasFlag(PointerModifiers.Shift));
            return;
        }

        SelectSingle(existing);
    }

    private void HandleRightClick()
    {
        if (_startHit is not { } hit) return;

        if (ColorEditModeEnabled)
        {
            ResetColorAt(hit);
            return;
        }

        if (!SmartToolEnabled) return;
        var action = BuildDeleteAction(hit);
        if (action is null) return;
        _doc.Execute(action);
        _doc.RecordStat(EditorStatKind.ObjectsDeleted, 1);
        RemoveFromSelection(hit);
    }

    // =====================================================================
    // 色編集モード(ncolor_data、2026-07-23)
    // =====================================================================

    /// <summary>ヒットした部位に応じてColor(端点/ノート本体)・BandColor(帯)のどちらを塗るか決める。
    /// both=true(Shift+クリック/ホイールクリック)の場合はフリーズの端点・帯を同時に同じ値で塗る。
    /// SubModeがNormal以外の場合はShadow/FrzHitの塗り分けへ委譲する(2026-07-24、部位の区別は
    /// 使わず対象実体1つに対して1アクション)。</summary>
    private void PaintAt(ObjectRef hit, bool both)
    {
        switch (SubMode)
        {
            case ColorEditSubMode.Shadow:
                PaintShadowAt(hit);
                return;
            case ColorEditSubMode.FrzHit:
                PaintFrzHitAt(hit);
                return;
        }

        if (PaintColorCode is not { Length: > 0 } color) return;
        switch (hit.Kind)
        {
            case ObjectKind.Note:
                _doc.Execute(new SetNoteColorAction(hit.Lane, hit.Tick, color, setColor: true, setBand: false, PaintAllFlag));
                break;
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
                _doc.Execute(new SetNoteColorAction(hit.Lane, hit.Tick, color, setColor: true, setBand: both, PaintAllFlag));
                break;
            case ObjectKind.FreezeBody:
                _doc.Execute(new SetNoteColorAction(hit.Lane, hit.Tick, color, setColor: both, setBand: true, PaintAllFlag));
                break;
        }
    }

    /// <summary>Shadowサブモードの塗り(2026-07-24)。通常ノートはPaintArrowShadowColor、
    /// フリーズ(部位を問わず)はPaintNormalShadowColorを使う。</summary>
    private void PaintShadowAt(ObjectRef hit)
    {
        switch (hit.Kind)
        {
            case ObjectKind.Note:
                if (PaintArrowShadowColor is { Length: > 0 } ac)
                    _doc.Execute(new SetShadowColorAction(hit.Lane, hit.Tick, ac));
                break;
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                if (PaintNormalShadowColor is { Length: > 0 } nc)
                    _doc.Execute(new SetShadowColorAction(hit.Lane, hit.Tick, nc));
                break;
        }
    }

    /// <summary>FrzHitサブモードの塗り(2026-07-24)。フリーズのみ対象(ノートは無視)。
    /// チェックが入っている項目だけをまとめて1アクションで設定する。</summary>
    private void PaintFrzHitAt(ObjectRef hit)
    {
        if (hit.Kind is not (ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)) return;
        bool setHit = HitEnabled && PaintHitColor is { Length: > 0 };
        bool setHitBar = HitBarEnabled && PaintHitBarColor is { Length: > 0 };
        bool setHitShadow = HitShadowEnabled && PaintHitShadowColor is { Length: > 0 };
        if (!setHit && !setHitBar && !setHitShadow) return;
        _doc.Execute(new SetFrzHitColorsAction(hit.Lane, hit.Tick,
            setHit, PaintHitColor, setHitBar, PaintHitBarColor, setHitShadow, PaintHitShadowColor));
    }

    /// <summary>右クリックでヒットした部位の色指定のみを解除する(左クリックの塗り分けと対称、
    /// 2026-07-23ユーザー確定仕様)。対象部位に色が設定されていなければ何もしない。</summary>
    private void ResetColorAt(ObjectRef hit)
    {
        bool resetColor = hit.Kind is ObjectKind.Note or ObjectKind.FreezeStart or ObjectKind.FreezeEnd;
        bool resetBand = hit.Kind == ObjectKind.FreezeBody;
        var list = _doc.CurrentTab.Lanes[hit.Lane].ColorOverrides;
        var entry = list.FirstOrDefault(e => e.Tick == hit.Tick);
        if (entry is null) return;
        if ((resetColor && entry.Color is null) || (resetBand && entry.BandColor is null)) return;
        _doc.Execute(new ResetNoteColorAction(hit.Lane, hit.Tick, resetColor, resetBand));
    }

    /// <summary>色編集モード中の右ドラッグ連続操作(2026-07-30要望対応)。ドラッグパスが触れた
    /// オブジェクトのうち、実際に色が設定されているものだけをまとめて色解除する(通常モードの
    /// ドラッグ削除=DragDeleteと同じ操作感)。判定基準はResetColorAt(単発右クリック)と同じ。</summary>
    private void FinishColorClearDrag()
    {
        var actions = new List<IEditAction>();
        foreach (var hit in _dragDeleteTouched)
        {
            bool resetColor = hit.Kind is ObjectKind.Note or ObjectKind.FreezeStart or ObjectKind.FreezeEnd;
            bool resetBand = hit.Kind == ObjectKind.FreezeBody;
            if (!resetColor && !resetBand) continue;
            var entry = _doc.CurrentTab.Lanes[hit.Lane].ColorOverrides.FirstOrDefault(e => e.Tick == hit.Tick);
            if (entry is null) continue;
            if ((resetColor && entry.Color is null) || (resetBand && entry.BandColor is null)) continue;
            actions.Add(new ResetNoteColorAction(hit.Lane, hit.Tick, resetColor, resetBand));
        }
        if (actions.Count == 0) return;
        _doc.Execute(new CompositeEditAction(actions, "ドラッグ色解除"));
    }

    /// <summary>選択中のノート/フリーズのうち色が設定されているものだけ色を解除する
    /// (色編集モード中のDeleteキー、2026-07-23)。フリーズは端点・帯どちらも設定されていれば
    /// まとめて解除する(選択は個々の部位ではなく実体単位のため)。ノート以外・色未設定のものは無視する。
    /// 対象が1つも無ければfalseを返す。</summary>
    public bool ResetSelectionColors()
    {
        if (_doc.Selection.Count == 0) return false;
        var unique = new List<ObjectRef>();
        foreach (var r in _doc.Selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var actions = new List<IEditAction>();
        foreach (var r in unique)
        {
            if (r.Kind is not (ObjectKind.Note or ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)) continue;
            var entry = _doc.CurrentTab.Lanes[r.Lane].ColorOverrides.FirstOrDefault(e => e.Tick == r.Tick);
            if (entry is null) continue;
            actions.Add(new ResetNoteColorAction(r.Lane, r.Tick, entry.Color is not null, entry.BandColor is not null));
        }
        if (actions.Count == 0) return false;
        _doc.Execute(new CompositeEditAction(actions, "選択色解除"));
        return true;
    }

    /// <summary>選択中のノート/フリーズすべてを指定色で塗る(右パネル「一括塗りつぶし」、2026-07-23)。
    /// フリーズは端点・帯まとめて同色にする。ノート以外(speed/boost/BPM/マーカー/拍子)は無視する
    /// (誤って巻き込んで選択していても無視するだけでエラーにしない、ユーザー確定仕様)。</summary>
    public bool BulkFillSelection(string color)
    {
        if (string.IsNullOrEmpty(color) || _doc.Selection.Count == 0) return false;
        var unique = new List<ObjectRef>();
        foreach (var r in _doc.Selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var actions = new List<IEditAction>();
        foreach (var r in unique)
        {
            switch (r.Kind)
            {
                case ObjectKind.Note:
                    actions.Add(new SetNoteColorAction(r.Lane, r.Tick, color, setColor: true, setBand: false, PaintAllFlag));
                    break;
                case ObjectKind.FreezeStart:
                case ObjectKind.FreezeEnd:
                case ObjectKind.FreezeBody:
                    actions.Add(new SetNoteColorAction(r.Lane, r.Tick, color, setColor: true, setBand: true, PaintAllFlag));
                    break;
            }
        }
        if (actions.Count == 0) return false;
        _doc.Execute(new CompositeEditAction(actions, "一括塗りつぶし"));
        return true;
    }

    /// <summary>選択中のノート/フリーズをShadowサブモードの色で一括塗りつぶす(2026-07-24)。
    /// 対象ごとに種別(ノート/フリーズ)を判定し、PaintArrowShadowColor/PaintNormalShadowColorの
    /// 適切な方を使う。ノート/フリーズ以外は無視する。</summary>
    public bool BulkFillShadowSelection()
    {
        if (_doc.Selection.Count == 0) return false;
        var unique = new List<ObjectRef>();
        foreach (var r in _doc.Selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var actions = new List<IEditAction>();
        foreach (var r in unique)
        {
            switch (r.Kind)
            {
                case ObjectKind.Note when PaintArrowShadowColor is { Length: > 0 } ac:
                    actions.Add(new SetShadowColorAction(r.Lane, r.Tick, ac));
                    break;
                case ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody
                    when PaintNormalShadowColor is { Length: > 0 } nc:
                    actions.Add(new SetShadowColorAction(r.Lane, r.Tick, nc));
                    break;
            }
        }
        if (actions.Count == 0) return false;
        _doc.Execute(new CompositeEditAction(actions, "一括塗りつぶし(塗りつぶし色)"));
        return true;
    }

    /// <summary>選択中のフリーズをFrzHitサブモードの色で一括塗りつぶす(2026-07-24)。
    /// フリーズのみ対象(ノート等は無視)。チェックが入っている項目だけを設定する。</summary>
    public bool BulkFillFrzHitSelection()
    {
        if (_doc.Selection.Count == 0) return false;
        bool setHit = HitEnabled && PaintHitColor is { Length: > 0 };
        bool setHitBar = HitBarEnabled && PaintHitBarColor is { Length: > 0 };
        bool setHitShadow = HitShadowEnabled && PaintHitShadowColor is { Length: > 0 };
        if (!setHit && !setHitBar && !setHitShadow) return false;

        var unique = new List<ObjectRef>();
        foreach (var r in _doc.Selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var actions = new List<IEditAction>();
        foreach (var r in unique)
        {
            if (r.Kind is not (ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)) continue;
            actions.Add(new SetFrzHitColorsAction(r.Lane, r.Tick,
                setHit, PaintHitColor, setHitBar, PaintHitBarColor, setHitShadow, PaintHitShadowColor));
        }
        if (actions.Count == 0) return false;
        _doc.Execute(new CompositeEditAction(actions, "一括塗りつぶし(ヒット時色)"));
        return true;
    }

    /// <summary>現在のタブの全ncolor_data指定を削除する(右パネル「ncolor_dataを全て削除」)。</summary>
    public bool ClearAllNoteColors()
    {
        if (_doc.CurrentTab.Lanes.All(l => l.ColorOverrides.Count == 0)) return false;
        _doc.Execute(new ClearAllNoteColorsAction());
        return true;
    }

    /// <summary>Ctrl+クリックのトグル選択(2026-07-26要望対応)。未選択のオブジェクトなら選択に追加し、
    /// 既に選択済みのオブジェクトなら、そのオブジェクトだけを選択解除する(他の選択は維持)。</summary>
    private void ToggleSelection(ObjectRef r)
    {
        if (IsInSelection(r)) _doc.Selection.RemoveWhere(s => s.SameEntity(r));
        else _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false);
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

        // 2026-07-26要望対応: 左ボタンを離した瞬間にCtrlが押されていれば、移動ではなく
        // 移動先への複製として扱う(ドラッグ開始時ではなく終了時のCtrl状態で判定=ドラッグ中に
        // 気が変わった場合に対応できるようにするため、_endModifiersを見る)。
        if (_endModifiers.HasFlag(PointerModifiers.Ctrl))
        {
            _doc.Execute(new CopyObjectsAction(targets, laneDelta, tickDelta));
            _doc.RecordStat(EditorStatKind.ObjectsPlaced, targets.Count);
        }
        else
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

        // 2026-07-30要望対応: 色編集モード中は「削除」ではなく「触れたオブジェクトの色情報クリア」
        // に置き換える(オブジェクト削除時と同じ操作感の挙動)。
        if (ColorEditModeEnabled)
        {
            FinishColorClearDrag();
            return;
        }

        var actions = _dragDeleteTouched
            .Select(BuildDeleteAction)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
        if (actions.Count == 0) return;
        _doc.Execute(new CompositeEditAction(actions, "ドラッグ削除"));
        _doc.RecordStat(EditorStatKind.ObjectsDeleted, actions.Count);

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
        // 2026-07-23: Ctrl+右ドラッグは既存選択を消さず追加する。それ以外は従来通り置き換え。
        if (!_modifiers.HasFlag(PointerModifiers.Ctrl)) _doc.Selection.Clear();
        foreach (var r in refs) if (!IsInSelection(r)) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false); // 選択変更のみ(2026-07-19b)
    }

    // =====================================================================
    // 外部トリガ操作(ダブルクリック/キーボード)
    // =====================================================================

    /// <summary>マーカーレーン(および時間情報表示レーン、2026-07-23)のダブルクリックで
    /// 「再生開始フレーム」を設定する(2026-07-17f、未解決事項§2-2)。
    /// 処理した場合true。それ以外の列ではfalseを返し、呼び出し側は通常のクリックとして扱う。
    /// 1回目のクリック(押下即処理)はカレントtick設定で冪等なため、シングルクリックの取り消しは不要。
    /// 譜面内容ではなく再生設定のためUndo対象外。</summary>
    public bool DoubleLeft(PointerPos pos)
    {
        var col = _doc.CurrentLayout.ColumnAt(pos.X);
        if (col is not { Kind: ColumnKind.Marker or ColumnKind.TimeInfo }) return false;
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

        // 2026-07-23: 色編集モード中のDeleteは通常削除ではなく、選択中ノート/フリーズの色解除に置き換わる
        // (誤操作防止、モード中は「配置済みオブジェクトのプロパティ」に触れられない仕様のため)。
        if (ColorEditModeEnabled) return ResetSelectionColors();

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
        _doc.RecordStat(EditorStatKind.ObjectsDeleted, actions.Count);
        _doc.Selection.Clear();
        _doc.NotifyChanged(markModified: false); // Execute側で変更済み、こちらは選択解除の通知のみ
        return true;
    }

    /// <summary>選択状態を解除する(Escapeキー、2026-07-26要望対応)。スマートツールのマウス操作
    /// だけでは選択を解除する手段が無かった(空セルクリックは配置、既存オブジェクトクリックは
    /// 選択の置き換えになり「何もない状態に戻す」操作が存在しなかった)ための新設。
    /// 選択が既に空ならfalse(データを変えない=Undo対象外)。</summary>
    public bool ClearSelection()
    {
        if (_doc.Selection.Count == 0) return false;
        _doc.Selection.Clear();
        _doc.NotifyChanged(markModified: false);
        return true;
    }

    /// <summary>キーボードモード中のShift+前進後退でのレンジ選択(2026-07-26要望対応)。指定tick範囲
    /// [tickA,tickB](順不同)にある全レーンのノート・フリーズ始点を選択する(既存選択は置き換え)。
    /// Shift+移動のたびに呼び直される想定で、常に選択状態を範囲どおりに作り直す(範囲内が0件でも
    /// 「選択をクリアして範囲を示す」操作として扱いtrueを返す)。</summary>
    public bool SelectRangeAllLanes(long tickA, long tickB)
    {
        long tMin = Math.Min(tickA, tickB), tMax = Math.Max(tickA, tickB);
        var tab = _doc.CurrentTab;
        var refs = new List<ObjectRef>();
        for (int lane = 0; lane < tab.Lanes.Count; lane++)
        {
            foreach (var t in tab.Lanes[lane].Notes)
                if (t >= tMin && t <= tMax) refs.Add(new ObjectRef(ObjectKind.Note, lane, t));
            foreach (var f in tab.Lanes[lane].Freezes)
                if (f.StartTick >= tMin && f.StartTick <= tMax) refs.Add(new ObjectRef(ObjectKind.FreezeStart, lane, f.StartTick));
        }

        _doc.Selection.Clear();
        foreach (var r in refs) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false);
        return true;
    }

    // =====================================================================
    // 全選択(仕様書13章TBD: Ctrl+A/Shift+Ctrl+A、2026-07-21)
    // =====================================================================

    /// <summary>Ctrl+A: 現在の難易度のノートレーンにあるノート・フリーズを全選択する(対象固定)。
    /// 1件も無ければ何もせずfalseを返す。</summary>
    public bool SelectAllNotes()
    {
        var tab = _doc.CurrentTab;
        var refs = new List<ObjectRef>();
        for (int lane = 0; lane < tab.Lanes.Count; lane++)
        {
            foreach (var t in tab.Lanes[lane].Notes) refs.Add(new ObjectRef(ObjectKind.Note, lane, t));
            foreach (var f in tab.Lanes[lane].Freezes) refs.Add(new ObjectRef(ObjectKind.FreezeStart, lane, f.StartTick));
        }
        if (refs.Count == 0) return false;

        _doc.Selection.Clear();
        foreach (var r in refs) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false);
        return true;
    }

    /// <summary>Shift+Ctrl+A: 環境設定でONにした種別すべてを全選択する。tick0のBPM(不変条件)は
    /// Bpmを対象にしていても除外する。1件も無ければ何もせずfalseを返す。</summary>
    public bool SelectAllTargets(SelectAllOptions options)
    {
        var tab = _doc.CurrentTab;
        var project = _doc.Project;
        var refs = new List<ObjectRef>();

        if (options.Note || options.Freeze)
        {
            for (int lane = 0; lane < tab.Lanes.Count; lane++)
            {
                if (options.Note)
                    foreach (var t in tab.Lanes[lane].Notes) refs.Add(new ObjectRef(ObjectKind.Note, lane, t));
                if (options.Freeze)
                    foreach (var f in tab.Lanes[lane].Freezes) refs.Add(new ObjectRef(ObjectKind.FreezeStart, lane, f.StartTick));
            }
        }
        if (options.Speed)
            foreach (var e in tab.SpeedEvents) refs.Add(new ObjectRef(ObjectKind.Speed, -1, e.Tick));
        if (options.Boost)
            foreach (var e in tab.BoostEvents) refs.Add(new ObjectRef(ObjectKind.Boost, -1, e.Tick));
        if (options.Bpm)
            foreach (var e in project.BpmEvents)
                if (e.Tick != 0) refs.Add(new ObjectRef(ObjectKind.Bpm, -1, e.Tick));
        if (options.TimeSignature)
            // TimeSignatureはtickではなく物理小節番号で識別する(既存のChartLayout.HitTest等と同じ規約)
            foreach (var e in project.TimeSignatures) refs.Add(new ObjectRef(ObjectKind.TimeSignature, -1, e.MeasureIndex));
        if (options.Marker)
            foreach (var m in project.Markers) refs.Add(new ObjectRef(ObjectKind.Marker, -1, m.Tick));

        if (refs.Count == 0) return false;

        _doc.Selection.Clear();
        foreach (var r in refs) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false);
        return true;
    }

    // =====================================================================
    // クリップボード(仕様書13章: Ctrl+X/C/V、6.3上段「クリップボード系」)
    // =====================================================================

    /// <summary>選択中オブジェクトをクリップボードへコピーする(Ctrl+C)。
    /// TimeSignature(7.5: 物理小節頭固定のため貼り付け先での意味が保証できない)と
    /// tick0のBPM(不変条件、常に存在する固定点)はコピー対象から除外する。
    /// コピー可能な対象が1つも無ければ何もせず(既存クリップボードも保持したまま)falseを返す。</summary>
    public bool CopySelection()
    {
        if (!TrySetClipboard()) return false;
        _doc.RecordStat(EditorStatKind.Copy);
        return true;
    }

    /// <summary>選択中オブジェクトを切り取る(Ctrl+X = コピー + 選択削除、13章)。
    /// コピー自体はUndo対象外(クリップボードはドキュメント状態ではない)だが、
    /// 削除は既存のDeleteSelection(1ジェスチャ=1Undoアクション)がそのまま使われる。
    /// 2026-07-26: 統計情報(Copy/Cut)を別カウントにするため、CopySelection()は呼ばず
    /// TrySetClipboard()を直接使う(CutはCopy統計にカウントしない)。</summary>
    public bool CutSelection()
    {
        if (!TrySetClipboard()) return false;
        DeleteSelection();
        _doc.RecordStat(EditorStatKind.Cut);
        return true;
    }

    /// <summary>選択中オブジェクトからクリップボードエントリを組み立てて設定する(CopySelection/
    /// CutSelection共通の内部処理、統計カウントは含まない)。</summary>
    private bool TrySetClipboard()
    {
        var entries = BuildClipboardEntries(_doc.Selection);
        if (entries.Count == 0) return false;
        EditorClipboard.Set(entries);
        return true;
    }

    /// <summary>クリップボードの内容を貼り付ける(Ctrl+V)。
    /// tick基準点は再生開始フレーム(Project.PlaybackStartFrame、マーカー/時間情報レーンのダブルクリックで
    /// 設定される「現在の再生開始位置」、仕様書7.4)。2026-07-26: 従来はCurrentTick(シングルクリックで
    /// 設定される位置)基準だったが、プレイテストの開始位置と揃えたいという要望により変更した。
    /// 一度も設定されていなければtick0を基準にする。laneはコピー時の元レーンをそのまま使い、
    /// 現在のテンプレートのレーン数に収まらない対象はスキップする(キー種違いのプロジェクトへ
    /// 貼り付けた場合など)。貼り付け後は新規オブジェクトを選択状態にし、そのままグループ移動
    /// (6.3.2)で位置調整できるようにする。2026-07-26: 「frame情報以外は全て保持してコピペしたい」
    /// との要望対応で、通常ノート/フリーズのColorOverrides(ncolor_data個別色)・Annotations
    /// (コメント・警告)もコピー元のClipboardEntryから貼り付け先へ複製する(AddSidecarActions参照)。
    /// 1回の呼び出し=1Undoアクション。</summary>
    public bool Paste()
    {
        var entries = EditorClipboard.Entries;
        if (entries is null || entries.Count == 0) return false;

        long anchorTick = 0;
        if (_doc.Project.PlaybackStartFrame is { } startFrame)
        {
            var engine = _doc.Project.CreateTimingEngine();
            anchorTick = (long)Math.Round(engine.FrameToTick(startFrame));
        }
        int laneCount = _doc.CurrentTemplate.KeyCount;

        var actions = new List<IEditAction>();
        var pasted = new List<ObjectRef>();

        foreach (var e in entries)
        {
            long tick = anchorTick + e.TickOffset;
            if (tick < 0) continue;

            switch (e.Kind)
            {
                case ObjectKind.Note:
                    if (e.Lane < 0 || e.Lane >= laneCount) break;
                    actions.Add(new PlaceNoteAction(e.Lane, tick));
                    AddSidecarActions(actions, e, e.Lane, tick);
                    pasted.Add(new ObjectRef(ObjectKind.Note, e.Lane, tick));
                    break;

                case ObjectKind.FreezeStart:
                    if (e.Lane < 0 || e.Lane >= laneCount) break;
                    actions.Add(new PlaceFreezeAction(e.Lane, tick, tick + e.DurationTicks));
                    AddSidecarActions(actions, e, e.Lane, tick);
                    pasted.Add(new ObjectRef(ObjectKind.FreezeStart, e.Lane, tick));
                    break;

                case ObjectKind.Speed:
                    actions.Add(new PlaceValueEventAction(ValueEventKind.Speed, tick, e.Value));
                    pasted.Add(new ObjectRef(ObjectKind.Speed, -1, tick));
                    break;

                case ObjectKind.Boost:
                    actions.Add(new PlaceValueEventAction(ValueEventKind.Boost, tick, e.Value));
                    pasted.Add(new ObjectRef(ObjectKind.Boost, -1, tick));
                    break;

                case ObjectKind.Bpm:
                    if (tick == 0) break; // tick0は不変条件(既存イベントが常に存在)
                    actions.Add(new PlaceValueEventAction(ValueEventKind.Bpm, tick, e.Value));
                    pasted.Add(new ObjectRef(ObjectKind.Bpm, -1, tick));
                    break;

                case ObjectKind.Marker:
                    actions.Add(new PlaceMarkerAction(tick, e.Comment));
                    pasted.Add(new ObjectRef(ObjectKind.Marker, -1, tick));
                    break;
            }
        }

        if (actions.Count == 0) return false;

        _doc.Execute(new CompositeEditAction(actions, "貼り付け"));
        _doc.RecordStat(EditorStatKind.ObjectsPlaced, pasted.Count);
        _doc.RecordStat(EditorStatKind.Paste);
        _doc.Selection.Clear();
        foreach (var r in pasted) _doc.Selection.Add(r);
        _doc.NotifyChanged(markModified: false); // Execute側で変更済み、こちらは選択更新の通知のみ
        return true;
    }

    /// <summary>選択集合からクリップボードエントリ一覧を組み立てる。SameEntityで重複除去した上で、
    /// コピー対象外(TimeSignature・tick0のBPM)を除き、コピー範囲内の最小tick/最小laneを基準に
    /// 相対tickへ変換する(laneは相対化せず元の値をそのまま保持、Paste側のコメント参照)。</summary>
    private List<ClipboardEntry> BuildClipboardEntries(IEnumerable<ObjectRef> selection)
    {
        var unique = new List<ObjectRef>();
        foreach (var r in selection)
            if (!unique.Any(u => u.SameEntity(r))) unique.Add(r);

        var copyable = unique
            .Where(r => r.Kind != ObjectKind.TimeSignature && !(r.Kind == ObjectKind.Bpm && r.Tick == 0))
            .ToList();
        if (copyable.Count == 0) return [];

        long minTick = copyable.Min(r => r.Tick);

        var result = new List<ClipboardEntry>();
        foreach (var r in copyable)
        {
            switch (r.Kind)
            {
                case ObjectKind.Note:
                    {
                        var (color, annotation) = FindColorAndAnnotation(r.Lane, r.Tick);
                        result.Add(new ClipboardEntry(ObjectKind.Note, r.Lane, r.Tick - minTick, 0, 0, "", color, annotation));
                        break;
                    }

                case ObjectKind.FreezeStart:
                case ObjectKind.FreezeEnd:
                case ObjectKind.FreezeBody:
                    {
                        var freeze = _doc.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(f => f.StartTick == r.Tick);
                        if (freeze is null) break;
                        var (color, annotation) = FindColorAndAnnotation(r.Lane, freeze.StartTick);
                        result.Add(new ClipboardEntry(ObjectKind.FreezeStart, r.Lane, r.Tick - minTick, freeze.EndTick - freeze.StartTick, 0, "", color, annotation));
                        break;
                    }

                case ObjectKind.Speed:
                    {
                        var e = _doc.CurrentTab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                        if (e is null) break;
                        result.Add(new ClipboardEntry(ObjectKind.Speed, 0, r.Tick - minTick, 0, e.Value, ""));
                        break;
                    }

                case ObjectKind.Boost:
                    {
                        var e = _doc.CurrentTab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                        if (e is null) break;
                        result.Add(new ClipboardEntry(ObjectKind.Boost, 0, r.Tick - minTick, 0, e.Value, ""));
                        break;
                    }

                case ObjectKind.Bpm:
                    {
                        var e = _doc.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                        if (e is null) break;
                        result.Add(new ClipboardEntry(ObjectKind.Bpm, 0, r.Tick - minTick, 0, e.Bpm, ""));
                        break;
                    }

                case ObjectKind.Marker:
                    {
                        var m = _doc.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                        if (m is null) break;
                        result.Add(new ClipboardEntry(ObjectKind.Marker, 0, r.Tick - minTick, 0, 0, m.Comment));
                        break;
                    }
            }
        }
        return result;
    }

    /// <summary>ClipboardEntryが持つColorOverride/Annotationのスナップショットを、貼り付け先の
    /// lane/tickへ複製するアクションをactionsへ追記する(2026-07-26、Paste専用)。どちらも無ければ何もしない。</summary>
    private static void AddSidecarActions(List<IEditAction> actions, ClipboardEntry e, int lane, long tick)
    {
        if (e.ColorOverride is { } color) actions.Add(new AddColorOverrideAction(lane, tick, color));
        if (e.Annotation is { } a) actions.Add(new SetAnnotationAction(lane, tick, a.Comment, a.Warning, a.ShowIcon));
    }

    /// <summary>指定レーン・tickの通常ノート/フリーズ(始点tickで同定)が持つColorOverrides/Annotationsを
    /// クリップボード用のスナップショット(tick等の同定情報を除いた値のみ)へ変換する(2026-07-26)。
    /// どちらも無ければ両方null。</summary>
    private (ClipboardColor? Color, ClipboardAnnotation? Annotation) FindColorAndAnnotation(int lane, long tick)
    {
        var laneData = _doc.CurrentTab.Lanes[lane];
        var c = laneData.ColorOverrides.FirstOrDefault(x => x.Tick == tick);
        ClipboardColor? color = c is null ? null
            : new ClipboardColor(c.Color, c.BandColor, c.AllFlag, c.ShadowColor, c.HitColor, c.HitBarColor, c.HitShadowColor);

        var a = laneData.Annotations.FirstOrDefault(x => x.Tick == tick);
        ClipboardAnnotation? annotation = a is null ? null : new ClipboardAnnotation(a.Comment, a.Warning, a.ShowIcon);

        return (color, annotation);
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
                // 2026-07-30要望対応: リンク(自動スムージング)状態のマーカー間には新規配置不可
                // (ロック。リンク解除まではその範囲を保護する)。
                if (IsWithinLinkedRange(_doc.CurrentTab.SpeedEvents, tick)) return null;
                return new PlaceValueEventAction(ValueEventKind.Speed, tick, 1.0);

            case ColumnKind.Boost:
                if (IsWithinLinkedRange(_doc.CurrentTab.BoostEvents, tick)) return null;
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

            case ColumnKind.Word:
                // 2026-07-23(TBD 4): 既定値(Position=0, 通常歌詞、本文空)で配置し、詳細は右パネルで編集する
                // (マーカーのコメント編集と同じ流れ)。
                return new PlaceWordEntryAction(col.NoteLaneIndex, tick);

            default:
                return null;
        }
    }

    /// <summary>2026-07-30要望対応: 指定tickが、リンク(自動スムージング)設定済みのイベント対と
    /// その直後の同種イベントとの間(両端は含まない)に位置するかどうかを判定する。</summary>
    private static bool IsWithinLinkedRange(List<ValueEvent> events, long tick)
    {
        var sorted = events.OrderBy(e => e.Tick).ToList();
        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            if (sorted[i].LinkGridDivision is not null && tick > sorted[i].Tick && tick < sorted[i + 1].Tick)
                return true;
        }
        return false;
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
        ObjectKind.Word => new DeleteWordEntryAction(r.Lane, r.Tick),
        _ => null,
    };

    // =====================================================================
    // 補助
    // =====================================================================

    private ObjectRef? HitAt(PointerPos pos, double hitScale) =>
        _doc.CurrentLayout.HitTest(_doc.CurrentTab, _doc.Project, pos.X, pos.Y, hitScale);

    /// <summary>
    /// スナップON時は通常のグリッドスナップ、OFF時は「最寄りの整数フレーム」に丸めたtickを返す
    /// (2026-07-17: OFF時のフリー移動が小数フレーム単位になり扱いづらいとの要望対応。
    /// BPMによっては1tickが1frame未満になるため、tick単位で丸めるだけでは不十分)。
    /// 2026-07-25: 「今クリックすると実際にどこへ配置されるか」をChartCanvas側のカーソルライン
    /// (マウスホバー中の最寄りグリッド表示)が実際の配置ロジックと必ず一致するよう、この既存の
    /// スナップ解決ロジックをそのまま外部公開する(表示用に別ロジックを複製しない)。</summary>
    public long SnappedTickAt(PointerPos pos)
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
