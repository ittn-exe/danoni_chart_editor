using DanoniEditor.Collab.Protocol;

namespace DanoniEditor.Collab.Sync;

/// <summary>
/// 「どのセルを誰が置いたか」をセッション中のみ追跡する(設計メモ6.4節、ノート所有者アイコン。
/// 2026-09-20実装)。<see cref="Core.Models.ChartProject"/>には一切触れず、プロジェクトファイルへも
/// 保存されないメモリ上限定の付随情報(セッション終了・再接続で消える)。CollabSessionController
/// (App層)がセル差分メッセージ(<see cref="NoteCellChangedMessage"/>/<see cref="FreezeChangedMessage"/>)
/// を送受信・適用するのと並行してこのクラスへも反映し、ChartCanvas側の描画時にここへ問い合わせて
/// 所有者の識別色を取得する想定。
///
/// 制約: 参加より前に置かれていたノート/フリーズの所有者は分からない(このセッションで実際に
/// 変更イベントを観測したセルのみ追跡対象になる)。また、離脱した参加者の識別色は空くと
/// 別の新規参加者へ再割当されうるため(CollabHost.ResolveColor)、古い所有者表示が新しい参加者の
/// 色と同じに見えることがある(いずれもephemeral/簡易表示という前提でのユーザー確定仕様上、
/// 許容される限界として扱う)。
/// </summary>
public sealed class NoteOwnershipTracker
{
    private readonly Dictionary<(int TabIndex, int LaneIndex, long Tick), string> _noteOwners = new();
    private readonly Dictionary<(int TabIndex, int LaneIndex, long StartTick), string> _freezeOwners = new();

    /// <summary>通常ノートのセル差分メッセージを反映する(State=Noteなら所有者を記録、Emptyなら削除)。</summary>
    public void Apply(NoteCellChangedMessage message)
    {
        var key = (message.TabIndex, message.LaneIndex, message.Tick);
        if (message.State == NoteCellState.Note)
            _noteOwners[key] = message.AuthorParticipantId;
        else
            _noteOwners.Remove(key);
    }

    /// <summary>フリーズのセル差分メッセージを反映する(EndTickがあれば所有者を記録、nullなら削除)。</summary>
    public void Apply(FreezeChangedMessage message)
    {
        var key = (message.TabIndex, message.LaneIndex, message.StartTick);
        if (message.EndTick is not null)
            _freezeOwners[key] = message.AuthorParticipantId;
        else
            _freezeOwners.Remove(key);
    }

    /// <summary>指定セルの通常ノートを最後に置いた参加者IDを返す(未追跡なら null)。</summary>
    public string? GetNoteOwner(int tabIndex, int laneIndex, long tick) =>
        _noteOwners.TryGetValue((tabIndex, laneIndex, tick), out var id) ? id : null;

    /// <summary>指定StartTickのフリーズを最後に置いた参加者IDを返す(未追跡なら null)。</summary>
    public string? GetFreezeOwner(int tabIndex, int laneIndex, long startTick) =>
        _freezeOwners.TryGetValue((tabIndex, laneIndex, startTick), out var id) ? id : null;

    /// <summary>セッション切断・新規参加時など、追跡内容を全てリセットする。</summary>
    public void Clear()
    {
        _noteOwners.Clear();
        _freezeOwners.Clear();
    }
}
