using System.Text.Json.Serialization;

namespace DanoniEditor.Collab.Protocol;

/// <summary>
/// 共同編集プロトコル全体で共有する定数(共同編集 設計メモ 2026-09-20、3.1節)。
/// </summary>
public static class CollabProtocol
{
    /// <summary>プロトコルバージョン。互換性の無い変更を行う場合はインクリメントする。</summary>
    public const int Version = 1;

    /// <summary>1メッセージあたりの最大バイト数(壊れた長さプレフィックスによる巨大確保を防ぐ安全弁)。</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>既定の待受ポート(設計メモ3.1節、環境設定で変更可能にする想定)。</summary>
    public const int DefaultPort = 47621;
}

/// <summary>
/// 通常ノート1セルの状態(設計メモ2.2節)。フリーズの始点/終点は別種(<see cref="FreezeChangedMessage"/>)で
/// 扱うため、ここでは「無い/通常ノートがある」の2値のみを持つ(始点だけ届いて終点が届かない、という
/// 半端な状態を避けるため、フリーズは常に開始tick・終了tickをセットで1メッセージにまとめる方針)。
/// </summary>
public enum NoteCellState
{
    Empty = 0,
    Note = 1,
}

/// <summary>参加者1名分の情報(表示名+識別色)。</summary>
public sealed record ParticipantInfo(string Id, string DisplayName, string Color);

/// <summary>
/// 共同編集セッションでやり取りするメッセージの基底型。System.Text.Jsonのポリモーフィックシリアライズ
/// (.NET 7以降)を使い、"type"というプロパティにメッセージ種別を書き出す。新しいメッセージ種を追加した
/// 場合は、ここへ<see cref="JsonDerivedTypeAttribute"/>を追記すること。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(ParticipantJoinedMessage), "participantJoined")]
[JsonDerivedType(typeof(ParticipantLeftMessage), "participantLeft")]
[JsonDerivedType(typeof(NoteCellChangedMessage), "noteCell")]
[JsonDerivedType(typeof(FreezeChangedMessage), "freeze")]
public abstract record CollabMessage;

/// <summary>参加フロー手順1: ゲスト→ホストの挨拶(設計メモ3.2節)。</summary>
public sealed record HelloMessage(string DisplayName, string? PreferredColor, int ProtocolVersion) : CollabMessage;

/// <summary>参加フロー手順2: ホスト→ゲストへの参加者ID発行+現在の参加者一覧(設計メモ3.2節)。
/// Colorはホストが(希望色の重複調整を経て)実際に割り当てた色。</summary>
public sealed record WelcomeMessage(string ParticipantId, string Color, IReadOnlyList<ParticipantInfo> Roster) : CollabMessage;

/// <summary>参加フロー手順3〜4: 現在のプロジェクト全体(設計メモ3.2節)。ProjectJsonは
/// DanoniEditor.Core.Persistence.ProjectSerializer.Serializeの出力をそのまま格納する。</summary>
public sealed record SnapshotMessage(string ProjectJson) : CollabMessage;

/// <summary>新規参加者が現れたことをホストが他の参加者へ知らせる(設計メモ3.2節)。</summary>
public sealed record ParticipantJoinedMessage(ParticipantInfo Participant) : CollabMessage;

/// <summary>参加者が離脱したことをホストが他の参加者へ知らせる(設計メモ3.3節)。</summary>
public sealed record ParticipantLeftMessage(string ParticipantId) : CollabMessage;

/// <summary>
/// 通常ノート1セルの状態変化(設計メモ2.2節)。移動は「旧位置をEmpty、新位置をNote」という
/// 2件のメッセージとして表現し、専用の「移動」メッセージは設けない。
/// AuthorParticipantIdは「誰がこのセルを変更したか」(ノート所有者アイコン、設計メモ6.4節、
/// セッション中のみのephemeralな情報。プロジェクトファイルへは一切保存しない)。ホスト側で
/// 実際の送信元接続と一致するよう上書きされるため(CollabSessionController.OnHostMessageReceived)、
/// 生成時点では空文字のままでも構わない。</summary>
public sealed record NoteCellChangedMessage(int TabIndex, int LaneIndex, long Tick, NoteCellState State, string AuthorParticipantId = "") : CollabMessage;

/// <summary>
/// フリーズノート1件の追加・更新・削除(設計メモ2.2節)。StartTickが同一レーン内でのフリーズの
/// 識別キー(本体側の「フリーズはFreezes中のStartTickで同定する」規約に合わせる)。
/// EndTickがnullなら「StartTickに一致するフリーズを削除」、値があれば「StartTickのフリーズを
/// (無ければ新規に、あれば置き換えて)EndTickの位置まで設置する」ことを意味する。
/// フリーズの移動は「旧StartTickをEndTick=nullで削除」+「新しい位置を追加」の2件のメッセージで表現する
/// (通常ノートの移動表現(Empty/Note)と対称的な扱い)。
/// </summary>
public sealed record FreezeChangedMessage(int TabIndex, int LaneIndex, long StartTick, long? EndTick, string AuthorParticipantId = "") : CollabMessage;
