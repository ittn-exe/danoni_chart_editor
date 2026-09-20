using System.Net;
using System.Net.Sockets;
using DanoniEditor.Collab.Transport;

namespace DanoniEditor.Collab.Rendezvous;

/// <summary>
/// 「仲介ヘルパー」を介してCGNAT配下同士でもTCP接続を確立する側の実装(設計メモ4.2節)。
/// 手順:
/// 1. まずヘルパーへ接続し(この接続の送信元ローカルポートを覚えておく)、SessionCodeで挨拶する。
/// 2. ヘルパーから相手の外向きIP:ポートと同期合図を受け取る。
/// 3. ヘルパーとの接続を閉じたうえで、①と同じローカルポートから相手の外向きアドレスへ接続を試みる
///    (TCP同時オープン。相手側も同じタイミングでこちらへ接続を試みているため、NATのマッピングが
///    まだ生きていれば、互いのSYNパケットが経路上で行き交うことで、Listen/Acceptを介さずに
///    接続が確立する)。
///
/// 【検証状況(2026-09-20)】手順1〜2(ヘルパーとの待ち合わせ)はループバック環境で結合テスト済み
/// (Collab/RendezvousTests.cs)。手順3のTCP同時オープン自体もループバック上では動作を確認できたが、
/// これは同一マシン内での確認に過ぎず、実際のCGNAT環境(特にNATの種類がsymmetric NATの場合は
/// 原理的に成立しない、設計メモ4.2節「失敗時の扱い」参照)での動作は未検証。実機での確認は別途必要。
/// </summary>
public sealed class RendezvousClient
{
    /// <summary>TCP同時オープンの試行を諦めるまでの時間(設計メモ4.2節TBD「同時オープンのタイムアウト」
    /// への回答、2026-09-20確定)。</summary>
    public static readonly TimeSpan SimultaneousOpenTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 仲介ヘルパーを介して、同じSessionCodeで待ち合わせている相手とのTCP接続を確立する。
    /// 戻り値の<see cref="TcpClient"/>は、確立後はCollabHost.AcceptExternalClientまたは
    /// CollabGuestClient.ConnectViaExistingSocketAsyncへそのまま渡せる、通常の(Listen/Acceptを
    /// 経由した場合と区別のつかない)TCP接続。
    /// </summary>
    public async Task<TcpClient> EstablishAsync(string helperAddress, int helperPort, string sessionCode, CancellationToken ct = default)
    {
        var (peerAddress, localPort) = await ExchangeAddressesAsync(helperAddress, helperPort, sessionCode, ct).ConfigureAwait(false);
        return await SimultaneousOpenAsync(peerAddress, localPort, ct).ConfigureAwait(false);
    }

    /// <summary>設計メモ4.2節手順1〜2: ヘルパーと待ち合わせ、相手の外向きアドレスを受け取る。
    /// 戻り値のポート番号は、後続の同時オープンで使い回すローカルポート(このメソッド内で
    /// ヘルパーへ接続する際にOSへ割り当てさせたもの)。</summary>
    private static async Task<(IPEndPoint PeerAddress, int LocalPort)> ExchangeAddressesAsync(
        string helperAddress, int helperPort, string sessionCode, CancellationToken ct)
    {
        using var echoSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        echoSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        echoSocket.Bind(new IPEndPoint(IPAddress.Any, 0)); // 後段の同時オープンでも使い回すローカルポートをここで確定させる
        var localPort = ((IPEndPoint)echoSocket.LocalEndPoint!).Port;

        await echoSocket.ConnectAsync(helperAddress, helperPort, ct).ConfigureAwait(false);
        await using var echoConnection = new RendezvousConnection(new NetworkStream(echoSocket, ownsSocket: false));
        await echoConnection.SendAsync(new RendezvousHelloMessage(sessionCode), ct).ConfigureAwait(false);

        var peerMsg = await echoConnection.ReceiveAsync(ct).ConfigureAwait(false);
        if (peerMsg is RendezvousFailedMessage failed)
            throw new InvalidOperationException($"仲介ヘルパーでの待ち合わせに失敗しました: {failed.Reason}");
        if (peerMsg is not RendezvousPeerAddressMessage peerAddressMsg)
            throw new InvalidOperationException("仲介ヘルパーからの応答が想定と異なります(相手の接続先情報が届きませんでした)。");

        var goMsg = await echoConnection.ReceiveAsync(ct).ConfigureAwait(false);
        if (goMsg is not RendezvousGoMessage)
            throw new InvalidOperationException("仲介ヘルパーからの同期合図が届きませんでした。");

        return (new IPEndPoint(IPAddress.Parse(peerAddressMsg.PeerAddress), peerAddressMsg.PeerPort), localPort);
        // echoSocket/echoConnectionはusing/await usingでここを抜ける際に破棄される(ownsSocket:falseのため、
        // echoSocket自体の破棄も明示的に行われる)。
    }

    /// <summary>接続の試行と試行の間隔(設計メモ4.2節「TCP同時オープン」)。ヘルパーまでの往復遅延が
    /// 双方で異なるため、1回目のSYN送信タイミングだけでは相手のSYNとすれ違わないことがある
    /// (実測: ループバック環境でも1回だけの試行では高確率でConnection refusedになることを確認済み、
    /// 2026-09-20)。タイムアウトまで短い間隔で繰り返し試みることで、双方のSYN送信タイミングが
    /// 噛み合う機会を稼ぐ(NATの種類によっては何度試みても成立しないケースがあるのは想定通り、
    /// 設計メモ4.2節「失敗時の扱い」参照)。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>設計メモ4.2節手順3: ①で使ったのと同じローカルポートから、相手の外向きアドレスへ
    /// ほぼ同時に接続を試みる(TCP同時オープン)。SimultaneousOpenTimeoutに達するまでRetryIntervalの
    /// 間隔で繰り返し試行する。</summary>
    private static async Task<TcpClient> SimultaneousOpenAsync(IPEndPoint peerAddress, int localPort, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SimultaneousOpenTimeout);

        SocketException? lastSocketError = null;
        try
        {
            while (true)
            {
                var openSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    openSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    openSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));
                    await openSocket.ConnectAsync(peerAddress, timeoutCts.Token).ConfigureAwait(false);
                    // ここから先、openSocketの所有権はTcpClientへ移る(TcpClient.Dispose()がソケットも破棄する)。
                    return new TcpClient { Client = openSocket };
                }
                catch (SocketException ex)
                {
                    // 相手側がまだSYN送信前(タイミングがすれ違った)等の一時的な失敗。少し待って再試行する。
                    openSocket.Dispose();
                    lastSocketError = ex;
                    await Task.Delay(RetryInterval, timeoutCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    openSocket.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException(
                "TCP同時オープンに失敗しました(制限時間内に接続できませんでした)。symmetric NAT配下同士等、" +
                "この方式では原理的に繋がらない組み合わせの可能性があります(設計メモ4.2節「失敗時の扱い」参照)。" +
                "別の参加者に仲介ヘルパーを頼むか、既知の制約として受け入れてください。",
                (Exception?)lastSocketError ?? ex);
        }
    }
}
