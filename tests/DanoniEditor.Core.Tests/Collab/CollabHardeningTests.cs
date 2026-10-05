using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Session;
using DanoniEditor.Collab.Transport;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>共同編集ホストの最低限の堅牢化(2026-10-05)のテスト。不正・過大な入力でホストが落ちたり、
/// 他の参加者に影響が出たりしないことを、実際のTCP(ループバック)で確認する。</summary>
public class CollabHardeningTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<TcpClient> RawConnectAsync(int port)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync("127.0.0.1", port);
        return c;
    }

    private static async Task SendRawFrameAsync(NetworkStream s, byte[] body)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await s.WriteAsync(header);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    /// <summary>接続が相手(ホスト)により閉じられるのを待つ。閉じられたらtrue。</summary>
    private static async Task<bool> WaitUntilClosedAsync(NetworkStream s)
    {
        var buf = new byte[1024];
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            while (true)
            {
                var n = await s.ReadAsync(buf, cts.Token);
                if (n == 0) return true;
            }
        }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task AssertHostStillAcceptsGuestAsync(CollabHost host)
    {
        await using var guest = new CollabGuestClient();
        await guest.ConnectAsync("127.0.0.1", host.Port, "ok", null, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(guest.MyParticipantId));
    }

    [Fact]
    public async Task Hello_WithMismatchedProtocolVersion_IsRejected()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        using var raw = await RawConnectAsync(host.Port);
        var conn = new CollabConnection(raw.GetStream());
        await conn.SendAsync(new HelloMessage("x", null, CollabProtocol.Version + 99));

        Assert.True(await WaitUntilClosedAsync(raw.GetStream()));
        Assert.Empty(host.Roster);
        await AssertHostStillAcceptsGuestAsync(host);
    }

    [Fact]
    public async Task OversizedFirstMessage_IsRejectedWithoutAffectingHost()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        using var raw = await RawConnectAsync(host.Port);
        var stream = raw.GetStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, 32 * 1024 * 1024); // 挨拶としては過大(巨大確保の試み)
        await stream.WriteAsync(header);
        await stream.FlushAsync();

        Assert.True(await WaitUntilClosedAsync(stream));
        await AssertHostStillAcceptsGuestAsync(host);
    }

    [Fact]
    public async Task GarbageJson_IsRejectedWithoutAffectingHost()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        using var raw = await RawConnectAsync(host.Port);
        await SendRawFrameAsync(raw.GetStream(), Encoding.UTF8.GetBytes("{ not json"));

        Assert.True(await WaitUntilClosedAsync(raw.GetStream()));
        await AssertHostStillAcceptsGuestAsync(host);
    }

    [Fact]
    public async Task NullJsonBody_IsNotTreatedAsCleanClose_AndIsRejected()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        using var raw = await RawConnectAsync(host.Port);
        await SendRawFrameAsync(raw.GetStream(), Encoding.UTF8.GetBytes("null"));

        Assert.True(await WaitUntilClosedAsync(raw.GetStream()));
        await AssertHostStillAcceptsGuestAsync(host);
    }

    [Fact]
    public async Task DisplayName_IsSanitized_AndColorIsValidated()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        var longName = new string('a', 200) + "\n";
        await using var guest = new CollabGuestClient();
        await guest.ConnectAsync("127.0.0.1", host.Port, longName, "not-a-color", CancellationToken.None);

        var info = host.Roster.Single();
        Assert.True(info.DisplayName.Length <= 32);
        Assert.DoesNotContain('\n', info.DisplayName);
        Assert.Matches("^#[0-9a-fA-F]{6}$", info.Color);
    }

    [Fact]
    public async Task GuestCount_IsCapped()
    {
        await using var host = new CollabHost(port: 0);
        host.Start();

        var guests = new List<CollabGuestClient>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var g = new CollabGuestClient();
                guests.Add(g);
                await g.ConnectAsync("127.0.0.1", host.Port, $"g{i}", null, CancellationToken.None);
            }

            // 9人目は参加できない(接続は閉じられ、Welcomeが届かない)
            var extra = new CollabGuestClient();
            guests.Add(extra);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                extra.ConnectAsync("127.0.0.1", host.Port, "extra", null, CancellationToken.None));
            Assert.Equal(8, host.Roster.Count);
        }
        finally
        {
            foreach (var g in guests) await g.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_WithConnectedGuests_CompletesPromptly()
    {
        var host = new CollabHost(port: 0);
        host.Start();
        await using var guest = new CollabGuestClient();
        await guest.ConnectAsync("127.0.0.1", host.Port, "g", null, CancellationToken.None);

        var dispose = host.DisposeAsync().AsTask();
        var done = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dispose, done);
    }

    [Fact]
    public void CellDiffApplier_RejectsOutOfRangeTick_AndBadLaneIndex()
    {
        var tab = new DanoniEditor.Core.Models.DifficultyTab();
        tab.Lanes.Add(new DanoniEditor.Core.Models.LaneNotes());
        var project = new DanoniEditor.Core.Models.ChartProject { Tabs = { tab } };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DanoniEditor.Collab.Sync.CellDiffApplier.ApplyNoteCell(project,
                new NoteCellChangedMessage(0, 0, long.MaxValue - 1, NoteCellState.Note)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DanoniEditor.Collab.Sync.CellDiffApplier.ApplyFreeze(project,
                new FreezeChangedMessage(0, 0, 10, long.MaxValue - 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DanoniEditor.Collab.Sync.CellDiffApplier.ApplyNoteCell(project,
                new NoteCellChangedMessage(0, 5, 10, NoteCellState.Note)));
        Assert.Empty(project.Tabs[0].Lanes[0].Notes);
    }
}
