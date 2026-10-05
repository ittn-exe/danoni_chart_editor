using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DanoniEditor.Collab.Transport;

namespace DanoniEditor.Collab.Rendezvous;

/// <summary>
/// 「仲介ヘルパー」役の実装(設計メモ4.2節)。普通の回線で外部から到達可能な参加者が一時的に
/// 引き受ける役割で、共同編集セッション本体(CollabHost/CollabGuestClient)とは完全に独立した
/// 待受ポートで動く(同一プロセス内で両方の役を兼ねることも、ホストが仲介ヘルパーを兼ねる場合も
/// 想定しているため、あえて疎結合にしてある)。
///
/// 同じSessionCodeを持つ2つの接続が揃うと、双方の外向きIP:ポートを教え合ったうえで同期合図を送り、
/// 役目を終える(設計メモ4.2節手順1〜4)。3人目以降が同じSessionCodeで来た場合は「先着1名が既に
/// 待機中」を優先し、後から来た方とペアリングして先着分の待機枠を明け渡す(つまり同時に3人以上が
/// 同じコードで待つことは想定しない、1対1待ち合わせ専用)。
/// </summary>
public sealed class RendezvousHelperServer : IAsyncDisposable
{
    /// <summary>相手が現れないまま待ち続ける上限の既定値(設計メモ4.2節TBD「同時オープンのタイムアウト」
    /// への回答、2026-09-20確定)。この間にDiscord等で相手にもエディタからの参加操作をしてもらう想定。
    /// テストでは短縮したタイムアウトをコンストラクタで指定できる。</summary>
    public static readonly TimeSpan DefaultWaitForPeerTimeout = TimeSpan.FromMinutes(3);

    private sealed record Waiting(TcpClient Client, RendezvousConnection Connection);

    // 2026-10-05: 公開ポートに対する最低限の資源上限(未認証の接続が無制限に待機枠・タスクを占有しないように)。
    private const int MaxWaiting = 256;
    private const int MaxSessionCodeLength = 64;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();

    private readonly TcpListener _listener;
    private readonly TimeSpan _waitForPeerTimeout;
    private readonly ConcurrentDictionary<string, Waiting> _waiting = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public RendezvousHelperServer(int port, TimeSpan? waitForPeerTimeout = null)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _waitForPeerTimeout = waitForPeerTimeout ?? DefaultWaitForPeerTimeout;
    }

    /// <summary>実際に割り当てられた待受ポート(port=0で起動した場合の実ポート確認・テスト用)。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            var task = HandleClientAsync(client, ct);
            _clientTasks[task] = 0;
            _ = task.ContinueWith(t => _clientTasks.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var connection = new RendezvousConnection(client.GetStream());
        try
        {
            RendezvousMessage? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloCts.CancelAfter(HelloTimeout); // 挨拶を送らず居座る接続を放置しない
                first = await connection.ReceiveAsync(helloCts.Token).ConfigureAwait(false);
            }
            if (first is not RendezvousHelloMessage hello
                || string.IsNullOrEmpty(hello.SessionCode) || hello.SessionCode.Length > MaxSessionCodeLength
                || (_waiting.Count >= MaxWaiting && !_waiting.ContainsKey(hello.SessionCode)))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (_waiting.TryRemove(hello.SessionCode, out var peer))
            {
                await PairAsync(client, connection, peer, ct).ConfigureAwait(false);
            }
            else
            {
                _waiting[hello.SessionCode] = new Waiting(client, connection);
                _ = ExpireWaitingAsync(hello.SessionCode, client, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException or SocketException)
        {
            // 挨拶を送る前に切断された/不正なデータ/挨拶タイムアウト等。待機枠には登録していないので
            // 後始末は接続の破棄のみ(待機枠へ登録済みの接続はExpireWaitingAsyncが面倒を見る)。
            if (!_waiting.Values.Any(w => ReferenceEquals(w.Client, client)))
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* 破棄失敗は無視 */ }
            }
        }
    }

    /// <summary>2人揃った時点での本処理(設計メモ4.2節手順2〜4): 互いの外向きアドレスを通知し、
    /// ほぼ同時に同期合図を送ってヘルパーの役目を終える。</summary>
    private async Task PairAsync(TcpClient client, RendezvousConnection connection, Waiting peer, CancellationToken ct)
    {
        try
        {
            var myRemote = (IPEndPoint)client.Client.RemoteEndPoint!;
            var peerRemote = (IPEndPoint)peer.Client.Client.RemoteEndPoint!;

            await connection.SendAsync(new RendezvousPeerAddressMessage(peerRemote.Address.ToString(), peerRemote.Port), ct).ConfigureAwait(false);
            await peer.Connection.SendAsync(new RendezvousPeerAddressMessage(myRemote.Address.ToString(), myRemote.Port), ct).ConfigureAwait(false);

            // 設計メモ4.2節手順3: 双方がほぼ同時に相手へ接続を試みられるよう、合図はTask.WhenAllで並行送信する。
            await Task.WhenAll(
                connection.SendAsync(new RendezvousGoMessage(), ct),
                peer.Connection.SendAsync(new RendezvousGoMessage(), ct)
            ).ConfigureAwait(false);
        }
        finally
        {
            // 設計メモ4.2節手順4: ヘルパーの役目はここで終わる。以降のTCP接続はクライアント同士が直接確立する。
            await connection.DisposeAsync().ConfigureAwait(false);
            await peer.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExpireWaitingAsync(string sessionCode, TcpClient client, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_waitForPeerTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_waiting.TryRemove(sessionCode, out var stillWaiting) && ReferenceEquals(stillWaiting.Client, client))
        {
            try
            {
                await stillWaiting.Connection.SendAsync(new RendezvousFailedMessage("相手が制限時間内に現れませんでした。"), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            await stillWaiting.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* キャンセルによる例外は無視 */ }
        }
        foreach (var waiting in _waiting.Values)
        {
            await waiting.Connection.DisposeAsync().ConfigureAwait(false);
        }
        _waiting.Clear();
        try { await Task.WhenAll(_clientTasks.Keys.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* 終了処理。個々の失敗・待ちタイムアウトは無視 */ }
        _cts?.Dispose();
    }
}
