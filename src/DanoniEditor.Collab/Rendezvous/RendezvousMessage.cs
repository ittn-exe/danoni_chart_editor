using System.Text.Json.Serialization;

namespace DanoniEditor.Collab.Rendezvous;

/// <summary>
/// 仲介ヘルパー(設計メモ4.2節、CGNAT対応)とのやり取り専用のメッセージ。共同編集セッション本体の
/// <see cref="DanoniEditor.Collab.Protocol.CollabMessage"/>とは意図的に別系統とする
/// (仲介役は共同編集セッションの中身を一切解釈しない、単なる「お互いの外向きアドレスを教え合う
/// 待ち合わせ場所」に徹するため)。フレーミングは共同編集本体と同じ
/// [4バイト長(ビッグエンディアン)][UTF-8 JSON]形式(<see cref="DanoniEditor.Collab.Transport.RendezvousConnection"/>)。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RendezvousHelloMessage), "hello")]
[JsonDerivedType(typeof(RendezvousPeerAddressMessage), "peerAddress")]
[JsonDerivedType(typeof(RendezvousGoMessage), "go")]
[JsonDerivedType(typeof(RendezvousFailedMessage), "failed")]
public abstract record RendezvousMessage;

/// <summary>
/// 待ち合わせ手順1: クライアント→ヘルパーの参加要求(設計メモ4.2節手順1、TBDだった
/// 「セッションコードの受け渡し方法」への回答、2026-09-20確定)。SessionCodeは接続したい参加者同士が
/// 事前に(Discordのボイスチャット等、口頭で)合わせる短い合言葉。同じSessionCodeを持つクライアントが
/// 2人揃った時点でヘルパーがペアリングする(3人目以降は弾かれる、1対1待ち合わせのみ対応)。
/// </summary>
public sealed record RendezvousHelloMessage(string SessionCode) : RendezvousMessage;

/// <summary>
/// 待ち合わせ手順2: ヘルパー→双方への、相手の外向きIP:ポート通知(設計メモ4.2節手順1〜2)。
/// PeerAddress/PeerPortはヘルパーが「このTCP接続の送信元がどう見えているか」をそのまま報告したもの
/// (TCP接続の確立自体がSTUNのエコー応答を兼ねるため、UDPのような専用エコーパケットは不要)。
/// </summary>
public sealed record RendezvousPeerAddressMessage(string PeerAddress, int PeerPort) : RendezvousMessage;

/// <summary>
/// 待ち合わせ手順3: ヘルパー→双方への同期合図(設計メモ4.2節手順3)。これを受け取った直後、
/// 双方がほぼ同時に相手の外向きアドレスへTCP同時オープンを試みる。
/// </summary>
public sealed record RendezvousGoMessage : RendezvousMessage;

/// <summary>
/// ペアリングが成立しない場合(相手が制限時間内に現れない等)にヘルパーから送る失敗通知
/// (設計メモ4.2節「失敗時の扱い」)。
/// </summary>
public sealed record RendezvousFailedMessage(string Reason) : RendezvousMessage;
