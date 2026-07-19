namespace DanoniEditor.Editing;

/// <summary>
/// 1操作(ジェスチャ)分のUndo/Redo単位(仕様書13章: 1ジェスチャ=1Undoアクション)。
/// Do/Undoはべき等であること(同じEditorDocumentに対して繰り返し適用しても状態が一致する)。
/// </summary>
public interface IEditAction
{
    void Do(EditorDocument doc);
    void Undo(EditorDocument doc);
    string Label { get; }
}

/// <summary>
/// Undo/Redoスタック(仕様書13章: Ctrl+Z/Y、既定容量30・アプリ設定で変更可、仕様書14章)。
/// 新規Push時はRedo側を破棄する(通常のエディタ挙動)。容量超過時は最古の履歴を破棄する
/// (Undo方向には積み上げられなくなるが、Redoスタックには影響しない)。
/// </summary>
public sealed class UndoStack
{
    public const int DefaultCapacity = 30;

    private readonly LinkedList<IEditAction> _undo = new();
    private readonly Stack<IEditAction> _redo = new();

    public int Capacity { get; set; } = DefaultCapacity;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>直近にDoされたアクションのラベル(UI表示用、例: "Undo: ノート配置")</summary>
    public string? PeekUndoLabel => _undo.Last?.Value.Label;
    public string? PeekRedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>アクションを実行してスタックに積む(新規操作。Redo履歴は破棄)。</summary>
    public void Push(EditorDocument doc, IEditAction action)
    {
        action.Do(doc);
        _undo.AddLast(action);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
        _redo.Clear();
    }

    public bool Undo(EditorDocument doc)
    {
        if (_undo.Last is null) return false;
        var action = _undo.Last.Value;
        _undo.RemoveLast();
        action.Undo(doc);
        _redo.Push(action);
        return true;
    }

    public bool Redo(EditorDocument doc)
    {
        if (_redo.Count == 0) return false;
        var action = _redo.Pop();
        action.Do(doc);
        _undo.AddLast(action);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
        return true;
    }

    /// <summary>現在のUndo履歴の深さ(テスト・UI表示用)</summary>
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
