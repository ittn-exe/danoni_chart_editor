using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Rendezvous;
using DanoniEditor.Collab.Session;
using DanoniEditor.Collab.Transport;
using System.Net.Sockets;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>
/// 仲介ヘルパー(共同編集 設計メモ 2026-09-20、4.2節、CGNAT対応)のループバック結合テスト。
/// 実際のCGNAT環境での検証は別途必要(RendezvousClient.cs冒頭コメント参照)だが、ヘルパーによる
/// ペアリング・タイムアウト処理・TCP同時オープン自体の成立(ループバック上でRFC793の
/// simultaneous openが起きること)・確立した接続をCollabHost/CollabGuestClientへそのまま
/// 引き渡せることは、相手役の人間を必要とせずこの結合テストだけで検証できる。
/// </summary>
public class RendezvousTests
{
    [Fact]
    public async Task EstablishAsync_BothSides_ConnectToEachOtherViaHelper()
    {
        await using var helper = new RendezvousHelperServer(port: 0);
        helper.Start();

        var client1 = new RendezvousClient();
        var client2 = new RendezvousClient();

        var task1 = client1.EstablishAsync("127.0.0.1", helper.Port, "shared-code-1");
        var task2 = client2.EstablishAsync("127.0.0.1", helper.Port, "shared-code-1");

        await Task.WhenAll(task1, task2);
        using var tcp1 = await task1;
        using var tcp2 = await task2;

        Assert.True(tcp1.Connected);
        Assert.True(tcp2.Connected);

        // 実際にデータが流れることまで確認する(単に接続できただけでなく、双方向に通信できることの確認)。
        var stream1 = tcp1.GetStream();
        var stream2 = tcp2.GetStream();

        var payload = new byte[] { 1, 2, 3, 4 };
        await stream1.WriteAsync(payload);
        await stream1.FlushAsync();

        var received = new byte[4];
        var offset = 0;
        while (offset < received.Length)
        {
            var read = await stream2.ReadAsync(received.AsMemory(offset));
            Assert.True(read > 0);
            offset += read;
        }
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task EstablishAsync_NoPeerWithinTimeout_ThrowsFailure()
    {
        // 相手が現れないまま待ち続ける上限(設計メモ4.2節)に達した場合、RendezvousFailedMessageを
        // 受け取って例外になることを確認する(短縮したタイムアウトをテスト用に指定)。
        await using var helper = new RendezvousHelperServer(port: 0, waitForPeerTimeout: TimeSpan.FromMilliseconds(400));
        helper.Start();

        var client1 = new RendezvousClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client1.EstablishAsync("127.0.0.1", helper.Port, "lone-code"));
        Assert.Contains("失敗", ex.Message);
    }

    [Fact]
    public async Task HelperPairing_ReportsCorrectPeerEndpoints_BeforeSimultaneousOpen()
    {
        // RendezvousClient全体(実際のTCP同時オープンまで)ではなく、ヘルパーとの待ち合わせ部分
        // (挨拶〜相手アドレス通知〜同期合図)だけを、RendezvousConnectionを直接使って検証する。
        await using var helper = new RendezvousHelperServer(port: 0);
        helper.Start();

        using var raw1 = new TcpClient();
        await raw1.ConnectAsync("127.0.0.1", helper.Port);
        await using var conn1 = new RendezvousConnection(raw1.GetStream());

        using var raw2 = new TcpClient();
        await raw2.ConnectAsync("127.0.0.1", helper.Port);
        await using var conn2 = new RendezvousConnection(raw2.GetStream());

        await conn1.SendAsync(new RendezvousHelloMessage("pair-code"));
        await conn2.SendAsync(new RendezvousHelloMessage("pair-code"));

        var addr1 = await conn1.ReceiveAsync();
        var addr2 = await conn2.ReceiveAsync();
        var go1 = await conn1.ReceiveAsync();
        var go2 = await conn2.ReceiveAsync();

        var peerAddr1 = Assert.IsType<RendezvousPeerAddressMessage>(addr1);
        var peerAddr2 = Assert.IsType<RendezvousPeerAddressMessage>(addr2);
        Assert.IsType<RendezvousGoMessage>(go1);
        Assert.IsType<RendezvousGoMessage>(go2);

        // それぞれが「相手の」ローカルポート(ループバックなので送信元ポート=raw1/raw2自身のポート)を
        // 正しく教え合えていることを確認する。
        var myPort1 = ((System.Net.IPEndPoint)raw1.Client.LocalEndPoint!).Port;
        var myPort2 = ((System.Net.IPEndPoint)raw2.Client.LocalEndPoint!).Port;
        Assert.Equal(myPort2, peerAddr1.PeerPort);
        Assert.Equal(myPort1, peerAddr2.PeerPort);
    }

    [Fact]
    public async Task EstablishedConnection_CanBeHandedToCollabHostAndGuestClient()
    {
        // 仲介ヘルパー経由で確立した接続を、CollabHost.AcceptExternalClient /
        // CollabGuestClient.ConnectViaExistingSocketAsyncへそのまま渡せることを確認する
        // (設計メモ4.2節手順4: 「接続が確立すれば、以降は2〜3章のプロトコルがそのまま動作する」)。
        await using var helper = new RendezvousHelperServer(port: 0);
        helper.Start();

        var rendezvousHostSide = new RendezvousClient();
        var rendezvousGuestSide = new RendezvousClient();

        var hostSideTask = rendezvousHostSide.EstablishAsync("127.0.0.1", helper.Port, "handoff-code");
        var guestSideTask = rendezvousGuestSide.EstablishAsync("127.0.0.1", helper.Port, "handoff-code");
        await Task.WhenAll(hostSideTask, guestSideTask);

        var hostSideTcpClient = await hostSideTask;
        var guestSideTcpClient = await guestSideTask;

        await using var collabHost = new CollabHost(port: 0);
        collabHost.SnapshotProvider = () => new SnapshotMessage("{}");
        var joined = new TaskCompletionSource<ParticipantInfo>();
        collabHost.ParticipantJoined += info => joined.TrySetResult(info);
        collabHost.Start();

        collabHost.AcceptExternalClient(hostSideTcpClient);

        await using var guest = new CollabGuestClient();
        await guest.ConnectViaExistingSocketAsync(guestSideTcpClient, "テスト参加者", preferredColor: null);

        var joinedInfo = await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("テスト参加者", joinedInfo.DisplayName);
        Assert.Equal(guest.MyParticipantId, joinedInfo.Id);
    }
}
