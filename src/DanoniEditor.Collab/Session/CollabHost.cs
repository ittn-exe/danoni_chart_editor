using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Transport;

namespace DanoniEditor.Collab.Session;

/// <summary>
/// ホスト側のセッション管理(設計メモ2.1/3.1〜3.3節)。TCP待受、参加者ごとの接続保持、
/// 全員への配信を担当する。共同タイムラインの実データ(ChartProject)には一切関知しない
/// 疎結合な作りとし、参加時に送るスナップショットは<see cref="SnapshotProvider"/>から取得し、
/// 受信したメッセージの実際の適用(2.2節のセル差分の反映等)は<see cref="MessageReceived"/>の
/// 購読側(EditorDocument連携層)に委ねる。
/// </summary>
public sealed class CollabHost : IAsyncDisposable
{
    // 参加者の識別色パレット(設計メモ6.3節)。希望色が埋まっている場合、ここから空きを割り当てる。
    private static readonly string[] ColorPalette =
    [
        "#e6194b", "#3cb44b", "#4363d8", "#f58231",
        "#911eb4", "#42d4f4", "#f032e6", "#bfef45",
    ];

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, CollabConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ParticipantInfo> _roster = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>参加者からメッセージを受信した際に発火する(第1引数=参加者ID)。</summary>
    public event Action<string, CollabMessage>? MessageReceived;

    /// <summary>新規参加者が確定した際に発火する(Welcome送信・スナップショット送信後)。</summary>
    public event Action<ParticipantInfo>? ParticipantJoined;

    /// <summary>参加者が離脱した際に発火する。</summary>
    public event Action<string>? ParticipantLeft;

    /// <summary>新規参加者へ送るスナップショットメッセージを都度生成する処理。呼び出し元
    /// (EditorDocument連携層)が現在のChartProjectをシリアライズして返す想定。</summary>
    public Func<SnapshotMessage>? SnapshotProvider { get; set; }

    public CollabHost(int port = CollabProtocol.DefaultPort)
    {
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>実際に割り当てられた待受ポート(port=0で起動した場合の実ポート確認・テスト用)。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyCollection<ParticipantInfo> Roster => _roster.Values.ToList();

    /// <summary>ホスト自身の参加者情報(<see cref="SetSelfIdentity"/>を呼ぶまではnull)。
    /// ゲストはHelloMessage/WelcomeMessageのやり取りで自動的に参加者IDと識別色を得るが、
    /// ホスト自身にはその往復が無いため専用に用意した(設計メモ6.4節、ノート所有者アイコン用)。</summary>
    public ParticipantInfo? Self { get; private set; }

    /// <summary>
    /// ホスト自身を参加者の1人として登録する(表示名・識別色を確定し、以後の
    /// <see cref="Roster"/>・新規参加者へのWelcomeMessage.Rosterに含まれるようになる)。
    /// 呼び出しは<see cref="Start"/>より前(参加者が接続してくる前)に行うこと。
    /// 呼ばなくても既存の挙動(ホストが参加者一覧に現れない)は変わらないため、下位のテストは
    /// このメソッドを呼ばない限り従来通りの挙動のままである。
    /// </summary>
    public void SetSelfIdentity(string displayName, string? preferredColor = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var color = ResolveColor(preferredColor);
        var info = new ParticipantInfo(id, displayName, color);
        Self = info;
        _roster[id] = info;
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// 仲介ヘルパー経由のTCP同時オープン(設計メモ4.2節手順3〜4)で確立済みの接続を、通常の
    /// Accept経由の接続と全く同じ扱いで受け入れる(挨拶(Hello)待ち受け〜以降のハンドシェイクは
    /// 共通のHandleClientAsyncへそのまま合流させる。TCP接続が確立してしまえば、それがAcceptで
    /// 得られたものか同時オープンで得られたものかの区別はプロトコル上一切不要なため)。
    /// </summary>
    public void AcceptExternalClient(TcpClient client)
    {
        if (_cts is null)
            throw new InvalidOperationException("Start()を呼ぶ前に外部接続を受け付けることはできません。");
        _ = HandleClientAsync(client, _cts.Token);
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var connection = new CollabConnection(client.GetStream());
        string? participantId = null;
        try
        {
            var first = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            if (first is not HelloMessage hello)
                return; // 挨拶以外が最初に届いた場合は不正な接続として静かに切る

            participantId = Guid.NewGuid().ToString("N");
            var color = ResolveColor(hello.PreferredColor);
            var info = new ParticipantInfo(participantId, hello.DisplayName, color);

            var rosterBeforeJoin = _roster.Values.ToList();
            _roster[participantId] = info;
            _connections[participantId] = connection;

            await connection.SendAsync(new WelcomeMessage(participantId, color, rosterBeforeJoin), ct).ConfigureAwait(false);
            if (SnapshotProvider is not null)
                await connection.SendAsync(SnapshotProvider(), ct).ConfigureAwait(false);

            await BroadcastAsync(new ParticipantJoinedMessage(info), excludeParticipantId: participantId, ct).ConfigureAwait(false);
            ParticipantJoined?.Invoke(info);

            while (!ct.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null) break;
                MessageReceived?.Invoke(participantId, message);
            }
        }
        catch (IOException)
        {
            // 通常の切断(相手が急に落ちた等)。ParticipantLeftの通知はfinallyで行う。
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (participantId is not null)
            {
                _connections.TryRemove(participantId, out _);
                _roster.TryRemove(participantId, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
                await BroadcastAsync(new ParticipantLeftMessage(participantId)).ConfigureAwait(false);
                ParticipantLeft?.Invoke(participantId);
            }
            else
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string ResolveColor(string? preferred)
    {
        var used = _roster.Values.Select(p => p.Color).ToHashSet();
        if (!string.IsNullOrWhiteSpace(preferred) && !used.Contains(preferred))
            return preferred;

        var free = ColorPalette.FirstOrDefault(c => !used.Contains(c));
        return free ?? ColorPalette[Random.Shared.Next(ColorPalette.Length)];
    }

    /// <summary>接続中の全参加者(除外指定があればそれ以外)へメッセージを配信する。
    /// 個別の送信失敗(切断済み等)は無視する(受信ループ側の切断処理に任せる)。</summary>
    public async Task BroadcastAsync(CollabMessage message, string? excludeParticipantId = null, CancellationToken ct = default)
    {
        foreach (var (id, connection) in _connections)
        {
            if (id == excludeParticipantId) continue;
            try
            {
                await connection.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
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
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        _connections.Clear();
        _roster.Clear();
        _cts?.Dispose();
    }
}
