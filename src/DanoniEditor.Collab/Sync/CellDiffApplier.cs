using DanoniEditor.Collab.Protocol;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Collab.Sync;

/// <summary>
/// セル差分メッセージ(<see cref="NoteCellChangedMessage"/>/<see cref="FreezeChangedMessage"/>)を
/// <see cref="ChartProject"/>へ適用する処理、および現在の状態からメッセージを組み立てる処理
/// (設計メモ2.2節)。EditorDocument/UndoStackとは意図的に一切結び付けない
/// (受信側はUndo履歴を汚さず直接データへ反映する、送信側はEditorDocument連携層が
/// IEditActionの結果からこのクラスの生成メソッドを呼ぶ想定、設計メモ2.1節)。
/// </summary>
public static class CellDiffApplier
{
    /// <summary>指定セルへ通常ノートの有無を反映する。同一tickに既にノートがあれば一旦消してから
    /// 置き直す(重複防止)。</summary>
    public static void ApplyNoteCell(ChartProject project, NoteCellChangedMessage message)
    {
        var lane = GetLane(project, message.TabIndex, message.LaneIndex);
        lane.Notes.RemoveAll(tick => tick == message.Tick);
        if (message.State == NoteCellState.Note)
            lane.Notes.Add(message.Tick);
    }

    /// <summary>フリーズノートを追加・更新・削除する。StartTickが同一のものを一旦取り除いてから
    /// (EndTickが指定されていれば)新しい内容で置き直す(設計メモ2.2節、移動は削除+追加の2件で表現)。</summary>
    public static void ApplyFreeze(ChartProject project, FreezeChangedMessage message)
    {
        var lane = GetLane(project, message.TabIndex, message.LaneIndex);
        lane.Freezes.RemoveAll(f => f.StartTick == message.StartTick);
        if (message.EndTick is { } endTick)
            lane.Freezes.Add(new FreezeNote(message.StartTick, endTick));
    }

    /// <summary>ノート配置/解除を表すメッセージを組み立てる(送信側用)。authorParticipantIdは
    /// ノート所有者アイコン(設計メモ6.4節)用の送信者ID(省略時は空文字。ホスト側で実際の接続に
    /// 一致するよう上書きされるため、テスト等で省略しても実害は無い)。</summary>
    public static NoteCellChangedMessage CreateNoteCellMessage(int tabIndex, int laneIndex, long tick, bool present, string authorParticipantId = "") =>
        new(tabIndex, laneIndex, tick, present ? NoteCellState.Note : NoteCellState.Empty, authorParticipantId);

    /// <summary>フリーズの追加/更新を表すメッセージを組み立てる(送信側用)。</summary>
    public static FreezeChangedMessage CreateFreezeSetMessage(int tabIndex, int laneIndex, long startTick, long endTick, string authorParticipantId = "") =>
        new(tabIndex, laneIndex, startTick, endTick, authorParticipantId);

    /// <summary>フリーズの削除を表すメッセージを組み立てる(送信側用)。</summary>
    public static FreezeChangedMessage CreateFreezeRemoveMessage(int tabIndex, int laneIndex, long startTick, string authorParticipantId = "") =>
        new(tabIndex, laneIndex, startTick, EndTick: null, authorParticipantId);

    private static LaneNotes GetLane(ChartProject project, int tabIndex, int laneIndex)
    {
        if (tabIndex < 0 || tabIndex >= project.Tabs.Count)
            throw new ArgumentOutOfRangeException(nameof(tabIndex), tabIndex, $"タブ番号が範囲外です(タブ数: {project.Tabs.Count})。");

        var tab = project.Tabs[tabIndex];
        if (laneIndex < 0 || laneIndex >= tab.Lanes.Count)
            throw new ArgumentOutOfRangeException(nameof(laneIndex), laneIndex, $"レーン番号が範囲外です(レーン数: {tab.Lanes.Count})。");

        return tab.Lanes[laneIndex];
    }
}
