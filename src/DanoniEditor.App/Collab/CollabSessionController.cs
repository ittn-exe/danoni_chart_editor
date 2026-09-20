using System.IO;
using System.Windows.Threading;
using DanoniEditor.Collab.Protocol;
using DanoniEditor.Collab.Rendezvous;
using DanoniEditor.Collab.Session;
using DanoniEditor.Collab.Sync;
using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.App.Collab;

/// <summary>
/// 共同編集セッション1本ぶんの実行時状態(ホスト/ゲストいずれか一方のみ)をMainWindowから切り離して
/// 管理する層(共同編集 設計メモ 2026-09-20)。
///
/// 【2026-09-20 セル差分方式への移行】当初は「簡易版:双方向スナップショット送信」で実装していたが、
/// ①プロジェクト全体を毎回やり取りする無駄、②複数人が同時に別の場所を編集した場合に
/// スナップショットの単純上書きでは片方の変更が消えうる、という2点の問題があったため、
/// 設計メモ2.2節が元々想定していた「真のセル差分」方式へ移行した。EditActions.cs側の個々の
/// 操作へ変更範囲を報告させる改修は工数が大きいため、代わりに
/// <see cref="EditorDocument.BeforeEdit"/>/AfterEditの前後でタブ全体を比較する
/// <see cref="CellDiffDetector"/>を新設し、EditActions.cs自体は一切改修していない。
/// スナップショット(<see cref="SnapshotMessage"/>)は「新規参加者が入った瞬間の初期同期」用途としてのみ
/// 引き続き使う(CollabHost.SnapshotProvider経由、設計メモ3.2節)。
///
/// 【2026-09-20 ノート所有者アイコン(設計メモ6.4節)】セル差分メッセージへ送信者の参加者ID
/// (AuthorParticipantId)を付与し、<see cref="NoteOwnershipTracker"/>(セッション中のみのephemeral
/// な情報、プロジェクトファイルへは保存しない)へ反映する。ホストは自分自身も1人の参加者として
/// 登録する(CollabHost.SetSelfIdentity、従来ホストは参加者一覧に現れなかった)。ChartCanvas側は
/// <see cref="GetNoteOwnerColor"/>/<see cref="GetFreezeOwnerColor"/>を通じて色を取得し、
/// ノート/フリーズ始点の左上に小さな丸アイコンとして重ね描きする。
///
/// 公開イベントは全てWPFのUIスレッド上で発火する(CollabHost/CollabGuestClientのイベントは
/// バックグラウンドの受信ループスレッドから飛んでくるため、このクラスの内部でDispatcherへ
/// マーシャリングしてから外へ出す。呼び出し側(MainWindow)はスレッドを一切気にせず
/// コントロールへ直接触ってよい)。
/// </summary>
public sealed class CollabSessionController : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private CollabHost? _host;
    private CollabGuestClient? _guest;
    private EditorDocument? _document;
    private readonly NoteOwnershipTracker _ownership = new();

    /// <summary>直近のBeforeEditで採取したタブのスナップショット(AfterEditで差分を取るまで保持)。
    /// タブ参照が一致しない場合(理論上は起きない想定だが念のため)は差分検出をスキップする。</summary>
    private (DifficultyTab Tab, int TabIndex, List<CellDiffDetector.LaneSnapshot> Before)? _pendingLocalEdit;

    /// <summary>接続状態・参加者数などをまとめた短い文言が変わるたびに発火(ステータスバー表示用)。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>参加者一覧が変化するたびに発火(自分自身は含まない)。</summary>
    public event Action<IReadOnlyList<ParticipantInfo>>? RosterChanged;

    /// <summary>受信した変更(セル差分または初期スナップショット)の適用が完了した
    /// (=譜面ビューを再描画してよい)ことの通知。</summary>
    public event Action? RemoteEditApplied;

    /// <summary>セッションが(相手都合・エラーいずれかで)終了したことの通知。DisconnectAsyncを
    /// 自発的に呼んだ場合も発火する(MainWindow側のUI状態リセットを1箇所にまとめるため)。</summary>
    public event Action? Disconnected;

    public bool IsActive => _host is not null || _guest is not null;
    public bool IsHosting => _host is not null;

    /// <summary>自分自身の参加者情報(表示名+識別色)。ホスト/ゲストいずれの場合もセッション確立後に
    /// 確定する(ノート所有者アイコンの送信元IDとして使う)。</summary>
    public ParticipantInfo? Self { get; private set; }

    private readonly List<ParticipantInfo> _roster = [];

    /// <summary>現在の参加者一覧のスナップショット(自分自身は含まない。RosterChangedイベントと
    /// 同じ内容)。2026-09-21: 参加者一覧パネル(5ステップ計画Step5)が、イベント発火を待たずに
    /// 現在の状態を随時取得できるようにするため新設。</summary>
    public IReadOnlyList<ParticipantInfo> Roster => _roster;

    public CollabSessionController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>ホストとして待受を開始する(設計メモ3.1〜3.2節)。</summary>
    public void StartHost(EditorDocument document, int port, string displayName)
    {
        if (IsActive) throw new InvalidOperationException("既に共同編集セッションが開始されています。");

        _document = document;
        var host = new CollabHost(port)
        {
            SnapshotProvider = () => SnapshotSync.CreateSnapshot(_document!.Project),
        };
        // 2026-09-20: ホスト自身も1人の参加者として登録する(ノート所有者アイコン用)。
        // Start()より前(=誰も接続してくる前)に確定させておく必要がある。
        host.SetSelfIdentity(displayName);
        Self = host.Self;
        host.MessageReceived += OnHostMessageReceived;
        host.ParticipantJoined += info => RaiseOnUi(() =>
        {
            _roster.Add(info);
            RosterChanged?.Invoke(_roster.ToList());
            StatusChanged?.Invoke($"共同編集: ホスト中(ポート{host.Port}、参加者{_roster.Count}人)");
        });
        host.ParticipantLeft += id => RaiseOnUi(() =>
        {
            _roster.RemoveAll(p => p.Id == id);
            RosterChanged?.Invoke(_roster.ToList());
            StatusChanged?.Invoke($"共同編集: ホスト中(ポート{host.Port}、参加者{_roster.Count}人)");
        });

        host.Start();
        _host = host;
        AttachLocalEditHandling();
        StatusChanged?.Invoke($"共同編集: ホスト中(ポート{host.Port}、参加者0人)");
    }

    /// <summary>ホストへ直接接続し、参加する(設計メモ3.2節)。</summary>
    public async Task JoinAsync(EditorDocument document, string hostAddress, int port, string displayName, CancellationToken ct = default)
    {
        if (IsActive) throw new InvalidOperationException("既に共同編集セッションが開始されています。");

        var guest = new CollabGuestClient();
        await guest.ConnectAsync(hostAddress, port, displayName, preferredColor: null, ct).ConfigureAwait(false);

        AttachGuestAndAnnounce(document, guest, displayName);
    }

    /// <summary>
    /// 仲介ヘルパー(設計メモ4.2節、CGNAT対応)経由でホストへ接続し、参加する。ホスト自身が
    /// CGNAT配下で直接到達できない場合に使う。sessionCodeはホスト側の「仲介ヘルパー経由で
    /// 参加者を受け入れる」操作と同じ値を、Discord等で事前に口頭合わせしておく必要がある。
    /// </summary>
    public async Task JoinViaRendezvousAsync(EditorDocument document, string helperAddress, int helperPort, string sessionCode, string displayName, CancellationToken ct = default)
    {
        if (IsActive) throw new InvalidOperationException("既に共同編集セッションが開始されています。");

        var rendezvous = new RendezvousClient();
        var client = await rendezvous.EstablishAsync(helperAddress, helperPort, sessionCode, ct).ConfigureAwait(false);

        var guest = new CollabGuestClient();
        await guest.ConnectViaExistingSocketAsync(client, displayName, preferredColor: null, ct).ConfigureAwait(false);

        AttachGuestAndAnnounce(document, guest, displayName);
    }

    /// <summary>
    /// ホストとして待受中に、仲介ヘルパー(設計メモ4.2節、CGNAT対応)経由で参加者を1人受け入れる。
    /// 自分自身(ホスト)がCGNAT配下で参加者から直接到達できない場合に使う。sessionCodeは相手側の
    /// 「仲介ヘルパー経由でホストへ参加する」操作と同じ値を、Discord等で事前に口頭合わせしておく
    /// 必要がある(ヘルパーは同じSessionCodeを持つ2接続を1組としてペアリングするため、
    /// 参加者が複数いる場合は1人受け入れるたびに毎回別のSessionCodeで行うこと)。
    /// </summary>
    public async Task AcceptViaRendezvousAsync(string helperAddress, int helperPort, string sessionCode, CancellationToken ct = default)
    {
        if (_host is null)
            throw new InvalidOperationException("ホストとして開始していない状態では、仲介ヘルパー経由の参加受け入れはできません。");

        var rendezvous = new RendezvousClient();
        var client = await rendezvous.EstablishAsync(helperAddress, helperPort, sessionCode, ct).ConfigureAwait(false);
        _host.AcceptExternalClient(client);
    }

    /// <summary>ゲストとして接続確立後の共通処理(JoinAsync/JoinViaRendezvousAsyncで共有)。
    /// イベント購読・初期ロスター反映・ローカル編集検知の開始・状態通知までをまとめる。displayNameは
    /// 自分がホストへ送った表示名をそのままSelfへ控える(CollabGuestClient自体は表示名を保持しないため)。</summary>
    private void AttachGuestAndAnnounce(EditorDocument document, CollabGuestClient guest, string displayName)
    {
        _document = document;
        Self = new ParticipantInfo(guest.MyParticipantId!, displayName, guest.MyColor!);
        guest.MessageReceived += OnGuestMessageReceived;
        guest.Disconnected += () => RaiseOnUi(() =>
        {
            StatusChanged?.Invoke("共同編集: 切断されました");
            _ = DisconnectAsync();
        });

        _guest = guest;
        _roster.Clear();
        _roster.AddRange(guest.InitialRoster);
        AttachLocalEditHandling();
        RaiseOnUi(() =>
        {
            RosterChanged?.Invoke(_roster.ToList());
            StatusChanged?.Invoke($"共同編集: 参加者として接続中(自分の色={guest.MyColor})");
        });
    }

    private void AttachLocalEditHandling()
    {
        if (_document is null) return;
        _document.BeforeEdit += OnBeforeLocalEdit;
        _document.AfterEdit += OnAfterLocalEdit;
    }

    /// <summary>編集直前: 対象タブの状態をスナップショットしておく(AfterEditでの差分検出用)。</summary>
    private void OnBeforeLocalEdit(DifficultyTab tab)
    {
        if (_document is null) { _pendingLocalEdit = null; return; }
        var tabIndex = _document.Project.Tabs.IndexOf(tab);
        _pendingLocalEdit = tabIndex < 0 ? null : (tab, tabIndex, CellDiffDetector.Capture(tab));
    }

    /// <summary>編集直後: BeforeEditで採取した状態と比較し、変化したセルぶんのメッセージを送る。
    /// 自分自身の変更なので、送信と同時にNoteOwnershipTrackerへも反映する(自分が置いたノートにも
    /// 自分の色のアイコンが表示されるようにするため)。</summary>
    private async void OnAfterLocalEdit(DifficultyTab tab)
    {
        if (_pendingLocalEdit is not { } pending || !ReferenceEquals(pending.Tab, tab) || Self is null)
        {
            _pendingLocalEdit = null;
            return;
        }
        _pendingLocalEdit = null;

        foreach (var message in CellDiffDetector.Diff(pending.TabIndex, pending.Before, tab, Self.Id))
        {
            ApplyToOwnershipTracker(message);
            try
            {
                if (_host is not null)
                    await _host.BroadcastAsync(message).ConfigureAwait(false); // ホスト自身の編集は全員へ配信
                else if (_guest is not null)
                    await _guest.SendAsync(message).ConfigureAwait(false); // ゲストの編集はホストへ送るのみ(設計メモ2.3節、非対称通信)
            }
            catch (IOException)
            {
                // 送信失敗(切断等)は受信ループ側のDisconnected/ParticipantLeftで検知・通知される。
            }
        }
        RaiseOnUi(() => RemoteEditApplied?.Invoke()); // 自分の編集分もアイコン再描画が必要なため通知する
    }

    /// <summary>ホスト側: 参加者からのメッセージ受信(設計メモ2.1節、ホストが正)。受信したセル差分を
    /// 自分のプロジェクトへ反映したうえで、送信者以外の全員へそのまま転送する
    /// (送信者は既に自分の手元でこの変更を適用済みのため、送り返す必要はない)。
    /// 2026-09-20: AuthorParticipantIdは、クライアントの自己申告ではなくホストが実際に確認できる
    /// TCP接続の参加者ID(participantId引数)で必ず上書きしてから適用・転送する(なりすまし対策、
    /// かつ将来クライアント側の実装ミスがあっても所有者表示だけは必ず正しくなるようにするため)。</summary>
    private void OnHostMessageReceived(string participantId, CollabMessage message)
    {
        switch (message)
        {
            case NoteCellChangedMessage noteCell:
                var authoredNote = noteCell.AuthorParticipantId == participantId ? noteCell : noteCell with { AuthorParticipantId = participantId };
                ApplyRemoteMessage(authoredNote);
                _ = RelayAsync(authoredNote, excludeParticipantId: participantId);
                break;
            case FreezeChangedMessage freeze:
                var authoredFreeze = freeze.AuthorParticipantId == participantId ? freeze : freeze with { AuthorParticipantId = participantId };
                ApplyRemoteMessage(authoredFreeze);
                _ = RelayAsync(authoredFreeze, excludeParticipantId: participantId);
                break;
            // SnapshotMessageは新規参加者への初期同期専用(CollabHost.SnapshotProvider経由の一方向)で、
            // 参加者からホストへ送られてくることは想定しない(簡易版時代の名残の型のため、ここでは無視)。
        }
    }

    private async Task RelayAsync(CollabMessage message, string excludeParticipantId)
    {
        if (_host is null) return;
        try
        {
            await _host.BroadcastAsync(message, excludeParticipantId).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>ゲスト側: ホストからのメッセージ受信。</summary>
    private void OnGuestMessageReceived(CollabMessage message)
    {
        switch (message)
        {
            case SnapshotMessage or NoteCellChangedMessage or FreezeChangedMessage:
                ApplyRemoteMessage(message);
                break;
            case ParticipantJoinedMessage joined:
                RaiseOnUi(() =>
                {
                    _roster.Add(joined.Participant);
                    RosterChanged?.Invoke(_roster.ToList());
                });
                break;
            case ParticipantLeftMessage left:
                RaiseOnUi(() =>
                {
                    _roster.RemoveAll(p => p.Id == left.ParticipantId);
                    RosterChanged?.Invoke(_roster.ToList());
                });
                break;
        }
    }

    /// <summary>セル差分メッセージをNoteOwnershipTrackerへ反映する(適用先スレッドを問わず即座に行える
    /// 単純な辞書操作のため、UIスレッドへのマーシャリングは不要)。</summary>
    private void ApplyToOwnershipTracker(CollabMessage message)
    {
        switch (message)
        {
            case NoteCellChangedMessage noteCell:
                _ownership.Apply(noteCell);
                break;
            case FreezeChangedMessage freeze:
                _ownership.Apply(freeze);
                break;
        }
    }

    /// <summary>受信したセル差分/初期スナップショットを適用する。EditorDocument.Execute/Undoを一切
    /// 経由しないため(設計メモ2.1節「受信側はUndo履歴を汚さず直接データへ反映する」)、
    /// BeforeEdit/AfterEditは発火せず、ネットワークへの再送信ループにはならない
    /// (簡易版時代にあった「適用中フラグ」による抑制は、この理由により不要になった)。</summary>
    private void ApplyRemoteMessage(CollabMessage message)
    {
        RaiseOnUi(() =>
        {
            if (_document is null) return;
            switch (message)
            {
                case SnapshotMessage snapshot:
                    SnapshotSync.ApplySnapshotInPlace(_document.Project, snapshot);
                    break;
                case NoteCellChangedMessage noteCell:
                    CellDiffApplier.ApplyNoteCell(_document.Project, noteCell);
                    ApplyToOwnershipTracker(noteCell);
                    break;
                case FreezeChangedMessage freeze:
                    CellDiffApplier.ApplyFreeze(_document.Project, freeze);
                    ApplyToOwnershipTracker(freeze);
                    break;
                default:
                    return;
            }
            _document.NotifyChanged(); // 画面再描画+未保存マーク(受信内容の保存はローカルの保存操作に委ねる)
            RemoteEditApplied?.Invoke();
        });
    }

    /// <summary>指定セルの通常ノートを置いた参加者の識別色を返す(ChartCanvas描画用、未追跡または
    /// 既に離脱した参加者なら null=アイコンを描かない)。</summary>
    public string? GetNoteOwnerColor(int tabIndex, int laneIndex, long tick)
    {
        var id = _ownership.GetNoteOwner(tabIndex, laneIndex, tick);
        return id is null ? null : ColorOf(id);
    }

    /// <summary>指定StartTickのフリーズを置いた参加者の識別色を返す(ChartCanvas描画用)。</summary>
    public string? GetFreezeOwnerColor(int tabIndex, int laneIndex, long startTick)
    {
        var id = _ownership.GetFreezeOwner(tabIndex, laneIndex, startTick);
        return id is null ? null : ColorOf(id);
    }

    private string? ColorOf(string participantId)
    {
        if (Self?.Id == participantId) return Self.Color;
        return _roster.FirstOrDefault(p => p.Id == participantId)?.Color;
    }

    /// <summary>DispatcherがバインドされたUIスレッド上でactionを実行する
    /// (既にUIスレッドの場合は同期的にそのまま実行される、WPF Dispatcherの通常の挙動)。</summary>
    private void RaiseOnUi(Action action) => _dispatcher.Invoke(action);

    public async Task DisconnectAsync()
    {
        if (_document is not null)
        {
            _document.BeforeEdit -= OnBeforeLocalEdit;
            _document.AfterEdit -= OnAfterLocalEdit;
            _document = null;
        }
        _pendingLocalEdit = null;

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }
        if (_guest is not null)
        {
            await _guest.DisposeAsync().ConfigureAwait(false);
            _guest = null;
        }
        _roster.Clear();
        _ownership.Clear();
        Self = null;
        RaiseOnUi(() => Disconnected?.Invoke());
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
