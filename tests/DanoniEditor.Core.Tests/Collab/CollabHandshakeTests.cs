using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Session;
using DanoniEditor.Collab.Sync;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Collab;

/// <summary>
/// 共同編集: ホスト/ゲスト間の実際のTCP通信(ループバック)を用いた結合テスト
/// (共同編集 設計メモ 2026-09-20、3.1〜3.3節)。
/// </summary>
public class CollabHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ChartProject CreateSampleProject()
    {
        var tab = new DifficultyTab();
        tab.Lanes.Add(new LaneNotes());
        return new ChartProject { ProjectName = "test", Tabs = { tab } };
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout));
        if (completed != tcs.Task)
            throw new TimeoutException("期待したイベントがタイムアウト内に発生しませんでした。");
        return await tcs.Task;
    }

    [Fact]
    public async Task Connect_AssignsParticipantIdAndDeliversSnapshot()
    {
        await using var host = new CollabHost(port: 0);
        host.SnapshotProvider = () => SnapshotSync.CreateSnapshot(CreateSampleProject());
        host.Start();

        var joined = new TaskCompletionSource<ParticipantInfo>();
        host.ParticipantJoined += info => joined.TrySetResult(info);

        await using var guest = new CollabGuestClient();
        var snapshotReceived = new TaskCompletionSource<SnapshotMessage>();
        guest.MessageReceived += message =>
        {
            if (message is SnapshotMessage snapshot) snapshotReceived.TrySetResult(snapshot);
        };

        await guest.ConnectAsync("127.0.0.1", host.Port, "イトトン", preferredColor: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(guest.MyParticipantId));
        Assert.False(string.IsNullOrEmpty(guest.MyColor));
        Assert.Empty(guest.InitialRoster); // 最初の参加者なので、自分以外に参加者はいない

        var joinedInfo = await WaitAsync(joined);
        Assert.Equal(guest.MyParticipantId, joinedInfo.Id);
        Assert.Contains(host.Roster, p => p.Id == guest.MyParticipantId);

        var snapshot = await WaitAsync(snapshotReceived);
        var restored = SnapshotSync.ApplySnapshot(snapshot);
        Assert.Equal("test", restored.ProjectName);
    }

    [Fact]
    public async Task GuestMessage_ReachesHost_WithCorrectParticipantId()
    {
        await using var host = new CollabHost(port: 0);
        host.SnapshotProvider = () => SnapshotSync.CreateSnapshot(CreateSampleProject());
        host.Start();

        await using var guest = new CollabGuestClient();
        await guest.ConnectAsync("127.0.0.1", host.Port, "参加者A", preferredColor: null, CancellationToken.None);

        var received = new TaskCompletionSource<(string ParticipantId, CollabMessage Message)>();
        host.MessageReceived += (id, message) => received.TrySetResult((id, message));

        var sent = CellDiffApplier.CreateNoteCellMessage(tabIndex: 0, laneIndex: 0, tick: 192, present: true);
        await guest.SendAsync(sent);

        var (participantId, message) = await WaitAsync(received);
        Assert.Equal(guest.MyParticipantId, participantId);
        Assert.Equal(sent, message);
    }

    [Fact]
    public async Task HostBroadcast_ExcludingSender_OnlyReachesOtherParticipant()
    {
        await using var host = new CollabHost(port: 0);
        host.SnapshotProvider = () => SnapshotSync.CreateSnapshot(CreateSampleProject());
        host.Start();

        await using var guestA = new CollabGuestClient();
        await guestA.ConnectAsync("127.0.0.1", host.Port, "参加者A", preferredColor: null, CancellationToken.None);
        await using var guestB = new CollabGuestClient();
        await guestB.ConnectAsync("127.0.0.1", host.Port, "参加者B", preferredColor: null, CancellationToken.None);

        var receivedByB = new TaskCompletionSource<CollabMessage>();
        guestB.MessageReceived += message =>
        {
            if (message is NoteCellChangedMessage) receivedByB.TrySetResult(message);
        };
        var receivedByA = new TaskCompletionSource<CollabMessage>();
        guestA.MessageReceived += message =>
        {
            if (message is NoteCellChangedMessage) receivedByA.TrySetResult(message);
        };

        var broadcast = CellDiffApplier.CreateNoteCellMessage(0, 0, 192, present: true);
        await host.BroadcastAsync(broadcast, excludeParticipantId: guestA.MyParticipantId);

        var messageAtB = await WaitAsync(receivedByB);
        Assert.Equal(broadcast, messageAtB);

        // 除外されたAには届かないはず(短い猶予時間の範囲で確認する簡易的な否定チェック)
        var raced = await Task.WhenAny(receivedByA.Task, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotEqual(receivedByA.Task, raced);
    }

    [Fact]
    public async Task ParticipantLeft_IsBroadcastToRemainingGuests()
    {
        await using var host = new CollabHost(port: 0);
        host.SnapshotProvider = () => SnapshotSync.CreateSnapshot(CreateSampleProject());
        host.Start();

        var guestA = new CollabGuestClient();
        await guestA.ConnectAsync("127.0.0.1", host.Port, "参加者A", preferredColor: null, CancellationToken.None);
        await using var guestB = new CollabGuestClient();
        await guestB.ConnectAsync("127.0.0.1", host.Port, "参加者B", preferredColor: null, CancellationToken.None);

        var leftNotice = new TaskCompletionSource<ParticipantLeftMessage>();
        guestB.MessageReceived += message =>
        {
            if (message is ParticipantLeftMessage left) leftNotice.TrySetResult(left);
        };

        var participantIdA = guestA.MyParticipantId;
        await guestA.DisposeAsync();

        var notice = await WaitAsync(leftNotice);
        Assert.Equal(participantIdA, notice.ParticipantId);
    }
}
