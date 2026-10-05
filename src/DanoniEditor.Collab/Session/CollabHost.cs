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

    // 2026-10-05: 未認証の接続に対する最低限の資源上限(共同編集は実験的機能。認証・暗号化は未実装のため、
    // 信頼できる相手にだけポートを教える運用が前提)。
    private const int MaxGuests = 8;
    private const int MaxHelloBytes = 4096;
    private const int MaxDisplayNameLength = 32;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private static readonly System.Text.RegularExpressions.Regex ColorPattern = new("^#[0-9a-fA-F]{6}$");

    private readonly object _joinGate = new();
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();

    /// <summary>参加者1人ぶんの処理中に予期しない例外が起きた(その参加者の接続は切断される)。ログ用。</summary>
    public event Action<Exception>? ClientError;

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
        TrackClient(client, _cts.Token);
    }

    private void TrackClient(TcpClient client, CancellationToken ct)
    {
        var task = HandleClientAsync(client, ct);
        _clientTasks[task] = 0;
        _ = task.ContinueWith(t => _clientTasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    private static string SanitizeDisplayName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxDisplayNameLength) cleaned = cleaned[..MaxDisplayNameLength];
        return cleaned.Length == 0 ? "Guest" : cleaned;
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

            TrackClient(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch { /* 設定できない環境では無視 */ }
        var connection = new CollabConnection(client.GetStream());
        string? participantId = null;
        try
        {
            // 挨拶は小さなサイズ・短い制限時間で受け取る(未認証の接続が巨大確保や居座りをしないように)
            CollabMessage? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloCts.CancelAfter(HelloTimeout);
                first = await connection.ReceiveAsync(helloCts.Token, MaxHelloBytes).ConfigureAwait(false);
            }
            if (first is not HelloMessage hello)
                return; // 挨拶以外が最初に届いた場合は不正な接続として静かに切る
            if (hello.ProtocolVersion != CollabProtocol.Version)
                return; // プロトコルバージョン不一致(互換性の無い相手)は参加させない

            var displayName = SanitizeDisplayName(hello.DisplayName);
            var preferredColor = hello.PreferredColor is { } pc && ColorPattern.IsMatch(pc) ? pc : null;

            ParticipantInfo info;
            List<ParticipantInfo> rosterBeforeJoin;
            var newId = Guid.NewGuid().ToString("N");
            lock (_joinGate)
            {
                if (_connections.Count >= MaxGuests)
                    return; // 参加者数の上限
                var color = ResolveColor(preferredColor);
                info = new ParticipantInfo(newId, displayName, color);
                rosterBeforeJoin = _roster.Values.ToList();
                _roster[newId] = info;
                _connections[newId] = connection;
                participantId = newId;
            }
            var color2 = info.Color;

            await SendWithTimeoutAsync(connection, new WelcomeMessage(participantId, color2, rosterBeforeJoin), ct).ConfigureAwait(false);
            if (SnapshotProvider is not null)
                await SendWithTimeoutAsync(connection, SnapshotProvider(), ct).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            // 不正なデータ・購読側(エディタ本体)の例外など。この参加者の接続だけを切断し、
            // 未観測のタスク例外としてプロセスへ波及させない。
            try { ClientError?.Invoke(ex); } catch { /* ログ処理の失敗は無視 */ }
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
                await SendWithTimeoutAsync(connection, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 送信が制限時間内に終わらない(受信が極端に遅い/止まっている)参加者は切断する。
                // 1人の滞留で全員への配信が止まり続けないようにするための措置。
                _ = connection.DisposeAsync().AsTask();
            }
        }
    }

    private static async Task SendWithTimeoutAsync(CollabConnection connection, CollabMessage message, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SendTimeout);
        await connection.SendAsync(message, cts.Token).ConfigureAwait(false);
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
        // 2026-10-05: 参加者ごとの処理タスクの完了を(上限付きで)待つ。接続破棄とキャンセルで受信ループは
        // 終了へ向かうが、終了処理中のイベント通知がDispose後に走り続けないようにする。
        try { await Task.WhenAll(_clientTasks.Keys.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* 終了処理。個々の失敗・待ちタイムアウトは無視 */ }
        _cts?.Dispose();
    }
}
