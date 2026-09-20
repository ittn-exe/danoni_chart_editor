using System.Net.Sockets;
using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Transport;

namespace DanoniEditor.Collab.Session;

/// <summary>
/// ゲスト側のセッション管理(設計メモ3.1〜3.3節)。ホストへ接続し、挨拶(Hello)を送って
/// Welcome+スナップショットを受け取るところまでを担当する。以降のメッセージ送受信は
/// このクラスを通じて行うが、受信したメッセージの実際の適用は<see cref="MessageReceived"/>の
/// 購読側(EditorDocument連携層)に委ねる(CollabHostと対称的な疎結合設計)。
/// </summary>
public sealed class CollabGuestClient : IAsyncDisposable
{
    private TcpClient? _client;
    private CollabConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>ホストから発行された自分自身の参加者ID(ConnectAsync完了後に設定される)。</summary>
    public string? MyParticipantId { get; private set; }

    /// <summary>ホストが割り当てた自分自身の識別色(ConnectAsync完了後に設定される)。</summary>
    public string? MyColor { get; private set; }

    /// <summary>参加時点で既に居た参加者一覧(自分自身は含まない)。</summary>
    public IReadOnlyList<ParticipantInfo> InitialRoster { get; private set; } = [];

    public event Action<CollabMessage>? MessageReceived;

    /// <summary>受信ループが終了した(=ホストとの接続が切れた)ことを通知する(設計メモ3.4節)。</summary>
    public event Action? Disconnected;

    /// <summary>
    /// ホストへ接続し、参加フロー(設計メモ3.2節)の挨拶〜Welcome受信までを行う。
    /// この呼び出しが正常に完了した時点で<see cref="MyParticipantId"/>等が確定する。
    /// なお、直後に届くはずのSnapshotMessageは通常の受信ループ側(<see cref="MessageReceived"/>)で
    /// 受け取る(呼び出し側がSnapshotMessageの型でハンドリングすること)。
    /// </summary>
    public async Task ConnectAsync(string hostAddress, int port, string displayName, string? preferredColor, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(hostAddress, port, ct).ConfigureAwait(false);
        await CompleteHandshakeAsync(client, displayName, preferredColor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 仲介ヘルパー経由のTCP同時オープン(設計メモ4.2節)で確立済みの接続を使って参加する。
    /// ConnectAsyncとの違いは「自分から新規にTCP接続を張るか、既に確立済みの接続を使うか」だけで、
    /// 参加フロー(挨拶〜Welcome受信)自体は共通のCompleteHandshakeAsyncへ合流する。
    /// </summary>
    public async Task ConnectViaExistingSocketAsync(TcpClient client, string displayName, string? preferredColor, CancellationToken ct = default)
    {
        client.NoDelay = true;
        await CompleteHandshakeAsync(client, displayName, preferredColor, ct).ConfigureAwait(false);
    }

    private async Task CompleteHandshakeAsync(TcpClient client, string displayName, string? preferredColor, CancellationToken ct)
    {
        _client = client;
        _connection = new CollabConnection(_client.GetStream());

        await _connection.SendAsync(new HelloMessage(displayName, preferredColor, CollabProtocol.Version), ct).ConfigureAwait(false);

        var response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
        if (response is not WelcomeMessage welcome)
            throw new InvalidOperationException("ホストからの応答が想定と異なります(Welcomeメッセージが届きませんでした)。");

        MyParticipantId = welcome.ParticipantId;
        MyColor = welcome.Color;
        InitialRoster = welcome.Roster;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _connection!.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null) break;
                MessageReceived?.Invoke(message);
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    public Task SendAsync(CollabMessage message, CancellationToken ct = default)
    {
        if (_connection is null)
            throw new InvalidOperationException("ホストへ接続する前に送信しようとしました。");
        return _connection.SendAsync(message, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* キャンセルによる例外は無視 */ }
        }
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _client?.Dispose();
        _cts?.Dispose();
    }
}
