using System.Linq;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Editing;

/// <summary>統計情報(2026-07-26、環境設定 > 統計情報)向けの操作カテゴリ。
/// EditorDocument.StatRecordedイベントの引数として使う。</summary>
public enum EditorStatKind
{
    /// <summary>新規配置したオブジェクトの数(単発配置=1、貼り付け/Ctrl+ドラッグ複製は複製個数分)</summary>
    ObjectsPlaced,
    /// <summary>削除したオブジェクトの数</summary>
    ObjectsDeleted,
    /// <summary>Ctrl+C(コピー)操作の回数(常に1)</summary>
    Copy,
    /// <summary>Ctrl+X(切り取り)操作の回数(常に1。内部でObjectsDeletedも別途記録される)</summary>
    Cut,
    /// <summary>Ctrl+V(貼り付け)操作の回数(常に1。貼り付けた個々のオブジェクト数はObjectsPlacedで別途記録される)</summary>
    Paste,
}

/// <summary>
/// エディタが扱う「開いているプロジェクト」1つ分の可変状態(仕様書6章全体の裏側)。
/// ChartProjectそのもの(モデル)に加えて、編集セッション固有の状態
/// (どのタブを見ているか・何が選択されているか・Undo履歴・スナップ設定)を束ねる。
/// WPF非依存。ChartCanvas(WPF側)はChanged購読でInvalidateVisualするだけの薄い層になる想定。
/// </summary>
public sealed class EditorDocument
{
    public ChartProject Project { get; }
    public TemplateRepository Templates { get; }

    private int _currentTabIndex;
    /// <summary>現在編集中の難易度タブindex(範囲外指定は無視)</summary>
    public int CurrentTabIndex
    {
        get => _currentTabIndex;
        set
        {
            if (value < 0 || value >= Project.Tabs.Count || value == _currentTabIndex) return;
            _currentTabIndex = value;
            MarkTabActivated(value); // 2026-08-04: 再生開始ライン旧形式移行(下記コメント参照)
            Selection.Clear(); // タブ切替で選択状態は引き継がない(異なるレーン構成のため)
            _layoutCache = null;
            NotifyChanged();
        }
    }

    /// <summary>【旧・互換用】旧形式プロジェクト(再生開始フレームをプロジェクト全体で1つ共有していた
    /// 頃、ChartProject.PlaybackStartFrame参照)を読み込んだ直後の値(2026-08-04不具合修正)。
    /// 新形式(DifficultyTab.PlaybackStartFrameがタブごとに独立)のプロジェクトではnull。
    /// 各タブが初めて表示された時点でMarkTabActivatedにより順次そのタブへ振り分けられ、
    /// PrepareForSaveで未表示タブぶんの振り分けが完了するとnullへ戻る。</summary>
    private double? _pendingLegacyPlaybackStart;

    /// <summary>プロジェクトを開いてから現在まで(保存を含む)一度でも表示された難易度タブのindex集合
    /// (2026-08-04不具合修正)。旧形式プロジェクトの再生開始フレーム移行でのみ使う。</summary>
    private readonly HashSet<int> _activatedTabIndices = [];

    /// <summary>指定タブが「表示された」ことを記録する(2026-08-04不具合修正、CurrentTabIndexのsetter・
    /// NotifyTabsChanged・コンストラクタから呼ぶ)。旧形式プロジェクトからの移行待ち値が残っていて、
    /// かつそのタブがまだ自分自身のPlaybackStartFrameを持たない場合、このタイミングで旧値を
    /// そのタブ自身の値として確定させる(ユーザー確定仕様: 「一度でもアクティブになったタブ」は
    /// 旧値を引き継ぐ)。以後はそのタブ固有の値として独立して編集・クリアできる
    /// (BackSpaceでのクリアが正しく効くよう、フォールバック参照ではなくこの時点で実体をコピーする)。</summary>
    private void MarkTabActivated(int index)
    {
        if (!_activatedTabIndices.Add(index)) return;
        if (_pendingLegacyPlaybackStart is { } legacy && Project.Tabs[index].PlaybackStartFrame is null)
            Project.Tabs[index].PlaybackStartFrame = legacy;
    }

    /// <summary>現在選択中のオブジェクト集合(仕様書6.3.1のグループ選択/移動の基礎)</summary>
    public HashSet<ObjectRef> Selection { get; } = [];

    // =====================================================================
    // Undo/Redo履歴(2026-08-06不具合修正: 難易度タブごとに分離)
    //
    // 【背景】IEditActionの各実装(EditActions.cs)は、Do/Undoの実行「時点」のdoc.CurrentTabを
    // 対象に操作する設計になっている。従来はUndoStackがドキュメント(=プロジェクト)全体で1本しか
    // 無く、タブ切替でもクリアされなかったため、「タブAでノートを削除 → タブBへ切替 → Ctrl+Z」で
    // DeleteNoteAction.Undoが『タブBに存在しなかったノートを追加する』など、エラーを出さずに
    // 別タブの譜面を書き換えてしまう不具合があった(タブBのレーン数が少ない場合は
    // IndexOutOfRangeExceptionにもなり得た)。
    //
    // 【対処】履歴をタブごと(キー=TabId)に分離し、Undo/Redoはカレントタブのスタックだけを操作する。
    // TabIdは並び替えでも変わらずそのタブ自身を指し続けるため、D&D並び替えで履歴が入れ替わることは
    // ない(複製タブは新しいTabIdを採番するので、複製直後は空の履歴から始まる)。
    //
    // 【既知の制限】BPM変化点・拍子・マーカーのようなプロジェクト全体(ChartProject)のデータを
    // 変更するアクションも、実行時のカレントタブの履歴へ積まれる。そのため「タブAでBPMを変更 →
    // タブBでCtrl+Z」ではそのBPM変更は取り消されない(タブAへ戻ればUndoできる)。誤った譜面破壊を
    // 防ぐことを優先した仕様上のトレードオフ。
    // =====================================================================

    private readonly Dictionary<string, UndoStack> _undoStacks = [];
    private int _undoCapacity = UndoStack.DefaultCapacity;

    /// <summary>カレントタブのUndo/Redo履歴(2026-08-06: タブごとに分離。上記コメント参照)。</summary>
    public UndoStack UndoStack => UndoStackFor(CurrentTab.TabId);

    /// <summary>指定タブの履歴を取得する(未作成なら現在の容量設定で作る)。</summary>
    private UndoStack UndoStackFor(string tabId)
    {
        if (!_undoStacks.TryGetValue(tabId, out var stack))
        {
            stack = new UndoStack { Capacity = _undoCapacity };
            _undoStacks[tabId] = stack;
        }
        return stack;
    }

    /// <summary>Undo履歴の保持件数(仕様書14章、環境設定)。全タブの履歴へ一括で適用する
    /// (2026-08-06: タブごとに履歴を分離したため、個別のUndoStack.Capacityではなくこちらを使う)。</summary>
    public int UndoCapacity
    {
        get => _undoCapacity;
        set
        {
            _undoCapacity = Math.Max(1, value);
            foreach (var stack in _undoStacks.Values) stack.Capacity = _undoCapacity;
        }
    }

    /// <summary>Project.Tabsから消えたタブの履歴を破棄する(2026-08-06、タブ削除時のリーク防止)。
    /// タブを閉じる操作自体はUndo対象外(ユーザー確定仕様、MainWindow.CloseTabAtの確認ダイアログ参照)の
    /// ため、閉じたタブの履歴は復元せず捨ててよい。</summary>
    private void PruneUndoStacks()
    {
        if (_undoStacks.Count == 0) return;
        var alive = Project.Tabs.Select(t => t.TabId).ToHashSet();
        foreach (var id in _undoStacks.Keys.Where(id => !alive.Contains(id)).ToList())
            _undoStacks.Remove(id);
    }

    public SnapService Snap { get; } = new();

    /// <summary>状態変化通知(WPF側はこれをInvalidateVisualのトリガにする)</summary>
    public event Action? Changed;

    /// <summary>未保存の変更があるか(2026-07-19b、未解決事項§2-6のタイトルバー`*`と保存確認用)。
    /// 編集アクション・Undo/Redo・直接のタイミング変更でtrueになり、保存でクリアされる。
    /// 選択変更などデータを変えない通知はNotifyChanged(markModified: false)を使うこと。</summary>
    public bool IsModified { get; private set; }

    /// <summary>保存完了時に呼ぶ(未保存フラグのクリア)</summary>
    public void MarkSaved()
    {
        IsModified = false;
        Changed?.Invoke();
    }

    public DifficultyTab CurrentTab => Project.Tabs[CurrentTabIndex];
    public KeyTemplate CurrentTemplate => Templates.Get(CurrentTab.KeyTypeId);

    /// <summary>タブの追加・削除・並び替えなど、Project.Tabsの中身の対応関係が変わった直後に呼ぶ
    /// (2026-07-24: 難易度タブを閉じるとクラッシュする不具合の修正)。
    /// CurrentTabIndexのsetterは「値そのものが変わらない限り何もしない」ため、タブを1件削除して
    /// 同じ数値のインデックスへ設定し直しても(例: 3件中の2番目を閉じてインデックスは変わらず2→2のまま)
    /// 早期returnしてしまい、_layoutCacheが閉じられる前のタブのテンプレートを指したまま残ってしまう。
    /// 実際のCurrentTabは繰り上がった別のタブ(キー種が異なりレーン数が違う場合がある)になっているため、
    /// 描画側がtab.Lanes[i]をlayout.Template.Lanes[i]の数だけ回してIndexOutOfRangeExceptionになる。
    /// このメソッドは値の異同に関わらず無条件でキャッシュを破棄するため、上記の経路をすべて避けられる。</summary>
    public void NotifyTabsChanged(int? indexOverride = null)
    {
        int target = indexOverride ?? _currentTabIndex;
        _currentTabIndex = Math.Clamp(target, 0, Math.Max(0, Project.Tabs.Count - 1));
        MarkTabActivated(_currentTabIndex); // 2026-08-04: CurrentTabIndexのsetter同様に記録する
        PruneUndoStacks(); // 2026-08-06: 閉じられたタブのUndo履歴を破棄する
        Selection.Clear();
        _layoutCache = null;
        NotifyChanged();
    }

    private ChartLayout? _layoutCache;
    /// <summary>現在タブのレイアウト(テンプレート変更・タブ切替まではキャッシュ)。
    /// 歌詞レーン本数(2026-07-23、TBD 4)はタブごとに可変のため、取得の都度SyncWordLaneCountで
    /// 最新化する(変化が無ければ何もしない軽量な呼び出し)。</summary>
    public ChartLayout CurrentLayout
    {
        get
        {
            if (_layoutCache is null)
            {
                _layoutCache = new ChartLayout(CurrentTemplate);
                // 2026-07-26: プロジェクトファイルに保存された縦横ズームを復元する(ユーザー要望)。
                // タブ切替等でキャッシュが作り直される都度ここを通るため、常に同じ値へ揃う。
                if (Project.EditorZoomPxPerTick is { } px)
                    _layoutCache.PxPerTick = Math.Clamp(px, ChartLayout.MinPxPerTick, ChartLayout.MaxPxPerTick);
                if (Project.EditorZoomScale is { } zs)
                    _layoutCache.SetZoom(zs);
            }
            _layoutCache.SyncWordLaneCount(CurrentTab.WordLanes.Count);
            return _layoutCache;
        }
    }

    public EditorDocument(ChartProject project, TemplateRepository templates)
    {
        Project = project;
        Templates = templates;
        if (project.Tabs.Count == 0)
            throw new ArgumentException("プロジェクトに難易度タブが1つも存在しません", nameof(project));
        // 2026-08-04: 旧形式プロジェクト(ChartProject.PlaybackStartFrameが値を持つ)を読み込んだ場合のみ
        // 移行待ち状態になる。新形式(常にnull)では以下は何もしない。
        _pendingLegacyPlaybackStart = project.PlaybackStartFrame;
        MarkTabActivated(_currentTabIndex); // 開いた直後に表示されているタブも「表示された」扱い(旧値を即座に引き継ぐ)
    }

    /// <summary>保存直前に呼ぶ(2026-08-04不具合修正)。旧形式プロジェクト(再生開始フレームをタブ横断で
    /// 共有していた形式)から移行する場合のみ意味を持つ。表示済みタブへの旧値の振り分けは
    /// MarkTabActivatedにより表示のたびに即座に行われているため、ここでは残る問題
    /// ―「プロジェクトを開いてから保存するまでの間に一度も表示されなかったタブ」―だけを扱う
    /// (ユーザー確定仕様): 既に値が入っている既存タブの中で最もインデックスが若いタブの値を使う
    /// (該当が無ければ旧値そのものを使う)。移行が完了したらChartProject.PlaybackStartFrame
    /// (旧フィールド)をnullへクリアし、以後は何もしない(冪等)。新規プロジェクト(旧値が最初から
    /// 無い)では何もしない。</summary>
    public void PrepareForSave()
    {
        if (_pendingLegacyPlaybackStart is not { } legacy) return;

        var fallback = Project.Tabs.Select(t => t.PlaybackStartFrame).FirstOrDefault(v => v.HasValue) ?? legacy;
        foreach (var tab in Project.Tabs)
            tab.PlaybackStartFrame ??= fallback;

        Project.PlaybackStartFrame = null;
        _pendingLegacyPlaybackStart = null;
    }

    /// <summary>フレーム情報モード状態(仕様書7.6、2026-07-17i)。null=拍情報モード(通常)</summary>
    public FrameEditState? FrameEdit { get; private set; }

    /// <summary>フレーム情報モード中か</summary>
    public bool IsFrameEditMode => FrameEdit is not null;

    /// <summary>フレーム情報モードへ入る(全オブジェクトの絶対フレームをスナップショット固定)。
    /// 切替自体はUndo対象外(ユーザー確定仕様)。</summary>
    public void EnterFrameEditMode()
    {
        if (FrameEdit is not null) return;
        FrameEdit = FrameEditState.Capture(this);
        NotifyChanged(markModified: false); // モード切替はデータ変更ではない
    }

    /// <summary>モードOFF前の衝突検査(丸め込みによる同一tick重複)。空=衝突なし。</summary>
    public IReadOnlyList<string> FindFrameEditCollisions() =>
        FrameEdit is null ? [] : FrameEditState.FindCollisions(Project);

    /// <summary>フレーム情報モードを終了する。mergeDuplicates=trueなら重複を統合(統合は1Undoアクション)。
    /// tickは逐次確定済みのため、終了処理は実質この統合のみ。</summary>
    public void ExitFrameEditMode(bool mergeDuplicates)
    {
        if (FrameEdit is null) return;
        FrameEdit = null;
        if (mergeDuplicates) Execute(new MergeDuplicateTicksAction());
        else NotifyChanged(markModified: false); // 統合なし終了はデータ変更ではない
    }

    /// <summary>Undo履歴を通らない直接のタイミング変更(tick0のBPM値・StartNumberの右パネル編集)の後に
    /// 呼ぶ。フレーム情報モード中なら全オブジェクトをスナップショットから逆算し直す(2026-07-17i)。
    /// 元の変更がUndo対象外なので、このretickも対称的にUndo対象外とする。</summary>
    public void OnTimingChangedDirectly()
    {
        FrameEdit?.RetickAll(this);
        NotifyChanged();
    }

    /// <summary>
    /// 編集操作(Execute/Undo/Redo)の直前・直後に、対象タブを引数として発火する(2026-09-20、
    /// 共同編集のセル差分検出用)。EditorDocument自身は共同編集の存在を一切知らない
    /// (WPF非依存と同じ理由で疎結合を保つ)。BeforeEdit時点のタブの状態と、AfterEdit時点の
    /// タブの状態を購読側(Collab連携層)が比較することで、「実際にどのセルが変わったか」を
    /// EditActions.cs側の改修無しに検出できる。</summary>
    public event Action<DifficultyTab>? BeforeEdit;
    public event Action<DifficultyTab>? AfterEdit;

    /// <summary>編集アクションを実行してUndo履歴に積む(1ジェスチャ=1呼び出しが原則)。
    /// フレーム情報モード中はFrameModeActionで包み、BPM変更に伴う逆算再配置まで含めて1Undo単位にする。</summary>
    public void Execute(IEditAction action)
    {
        var tab = CurrentTab;
        BeforeEdit?.Invoke(tab);
        UndoStack.Push(this, FrameEdit is { } fe ? new FrameModeAction(action, fe) : action);
        NotifyChanged();
        AfterEdit?.Invoke(tab);
    }

    /// <summary>2026-07-26: 統計情報(環境設定 > 統計情報)向けの操作カウント通知。
    /// SmartToolControllerが該当する操作を行うたびに呼ぶ。永続化(AppSettings)はApp層の責務
    /// (このイベントを購読して加算・保存する)。Editing層はAppSettingsを参照しないための橋渡し。</summary>
    public event Action<EditorStatKind, int>? StatRecorded;

    public void RecordStat(EditorStatKind kind, int count = 1)
    {
        if (count > 0) StatRecorded?.Invoke(kind, count);
    }

    public bool Undo()
    {
        var tab = CurrentTab;
        BeforeEdit?.Invoke(tab);
        var ok = UndoStack.Undo(this);
        if (ok) NotifyChanged();
        AfterEdit?.Invoke(tab);
        return ok;
    }

    public bool Redo()
    {
        var tab = CurrentTab;
        BeforeEdit?.Invoke(tab);
        var ok = UndoStack.Redo(this);
        if (ok) NotifyChanged();
        AfterEdit?.Invoke(tab);
        return ok;
    }

    /// <summary>状態変化を通知する。markModified=falseは「データを変えない変化」(選択変更・モード切替等)用</summary>
    public void NotifyChanged(bool markModified = true)
    {
        if (markModified) IsModified = true;
        Changed?.Invoke();
    }
}
