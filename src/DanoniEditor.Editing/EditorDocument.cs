using DanoniEditor.Core.Models;

namespace DanoniEditor.Editing;

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
            Selection.Clear(); // タブ切替で選択状態は引き継がない(異なるレーン構成のため)
            _layoutCache = null;
            NotifyChanged();
        }
    }

    /// <summary>現在選択中のオブジェクト集合(仕様書6.3.1のグループ選択/移動の基礎)</summary>
    public HashSet<ObjectRef> Selection { get; } = [];

    public UndoStack UndoStack { get; } = new();
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
            _layoutCache ??= new ChartLayout(CurrentTemplate);
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

    /// <summary>編集アクションを実行してUndo履歴に積む(1ジェスチャ=1呼び出しが原則)。
    /// フレーム情報モード中はFrameModeActionで包み、BPM変更に伴う逆算再配置まで含めて1Undo単位にする。</summary>
    public void Execute(IEditAction action)
    {
        UndoStack.Push(this, FrameEdit is { } fe ? new FrameModeAction(action, fe) : action);
        NotifyChanged();
    }

    public bool Undo()
    {
        var ok = UndoStack.Undo(this);
        if (ok) NotifyChanged();
        return ok;
    }

    public bool Redo()
    {
        var ok = UndoStack.Redo(this);
        if (ok) NotifyChanged();
        return ok;
    }

    /// <summary>状態変化を通知する。markModified=falseは「データを変えない変化」(選択変更・モード切替等)用</summary>
    public void NotifyChanged(bool markModified = true)
    {
        if (markModified) IsModified = true;
        Changed?.Invoke();
    }
}
