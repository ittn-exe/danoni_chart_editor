using System.Text.Json;
using System.Text.Json.Serialization;

namespace DanoniEditor.Core.Persistence;

/// <summary>
/// 自動保存(クラッシュ復旧用)のファイルI/O本体(2026-07-25、TBD「自動保存・クラッシュ復旧」、
/// 2026-07-26 複数ウィンドウ[=複数プロセス]対応でクラッシュフラグをインスタンス単位に分離)。
/// WPFに依存しない純ロジックとして切り出し、単体テスト可能にしている(DanoniEditor.App側は
/// AppPaths.AutoSaveDirを渡してこのクラスを呼ぶだけの薄いラッパーになる想定)。
///
/// 設計方針(ユーザー確定、B案): 通常の保存(Ctrl+S)には一切影響を与えない、実ファイルとは
/// 別の「復旧用スロット」へ書き込む。次回起動時に前回の異常終了を検知した場合のみ、
/// スロットの内容を「復元しますか?」と提示する。
///
/// クラッシュ検知の仕組み(2026-07-26改訂): 「新しいウィンドウ」機能により同一exeが複数プロセスで
/// 同時実行され得るため、実行中フラグは単一ファイルではなくプロセス(インスタンス)ごとに
/// running_{instanceId}.flag として個別に持つ。フラグの中身にはPIDを記録し、次回起動時は
/// 「そのPIDのプロセスが今も実際に生きているか」で判定する(単にファイルの有無だけで判定すると、
/// 他のウィンドウがまだ正常に開いているだけなのに誤って「クラッシュした」と判定してしまうため)。
/// 生きていないフラグ=そのインスタンスは前回正常終了しなかった、とみなす。
///
/// マルチプロジェクトタブ対応: セッション(開いているプロジェクトタブ)ごとに一意なSlotIdを持ち、
/// スロットファイル(slot_{SlotId}.json、中身は通常のプロジェクトファイルと同じJSON形式)と、
/// 全スロットの一覧(manifest.json: どのインスタンス[プロセス]のどのプロジェクトがどのパスに
/// 対応するか、最終自動保存日時)を別々に持つ。クラッシュ検知時はmanifest.jsonを読んで、
/// 生きていないインスタンスに属するセッション分だけを復元候補として提示する。
/// </summary>
public static class AutoSaveManager
{
    private const string ManifestFileName = "manifest.json";
    private const string CrashFlagPrefix = "running_";
    private const string CrashFlagExt = ".flag";

    // プロセス開始時刻とフラグ時刻の比較に許容する時計の誤差。
    private static readonly TimeSpan PidReuseTolerance = TimeSpan.FromSeconds(5);
    private const string ManifestLockFileName = "manifest.lock";

    private static readonly JsonSerializerOptions ManifestJsonOpts = new() { WriteIndented = true };

    public static string GetManifestPath(string autoSaveDir) => Path.Combine(autoSaveDir, ManifestFileName);
    public static string GetSlotFilePath(string autoSaveDir, string slotId) => Path.Combine(autoSaveDir, $"slot_{slotId}.json");

    // --- クラッシュフラグ(2026-07-26: インスタンス[プロセス]単位) ---

    public static string GetInstanceFlagPath(string autoSaveDir, string instanceId) =>
        Path.Combine(autoSaveDir, $"{CrashFlagPrefix}{instanceId}{CrashFlagExt}");

    /// <summary>起動直後に呼ぶ。中身に自プロセスのPIDを記録しておき、生死判定に使う。</summary>
    public static void SetCrashFlag(string autoSaveDir, string instanceId, int processId)
    {
        Directory.CreateDirectory(autoSaveDir);
        File.WriteAllText(GetInstanceFlagPath(autoSaveDir, instanceId), $"{processId}|{DateTime.UtcNow:o}");
    }

    /// <summary>正常終了(App.OnExit)時に呼ぶ。OnClosingでキャンセルされた場合はOnExitに到達しないため、
    /// このメソッドが呼ばれるのは「実際に終了する」と確定した時だけになる。自分自身のフラグのみを
    /// 削除するため、他のウィンドウ(プロセス)のフラグには一切影響しない。</summary>
    public static void ClearCrashFlag(string autoSaveDir, string instanceId)
    {
        var path = GetInstanceFlagPath(autoSaveDir, instanceId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>autoSaveDir内の全インスタンスフラグを走査し、フラグに記録されたPIDのプロセスが
    /// 実際に今も生きているインスタンスIDの集合を返す(2026-07-26)。生きていないフラグ(前回
    /// 正常終了しなかった痕跡)は、判定の後にPurgeDeadInstanceFlagsで明示的に片付ける想定。</summary>
    public static HashSet<string> GetAliveInstanceIds(string autoSaveDir)
    {
        var alive = new HashSet<string>();
        if (!Directory.Exists(autoSaveDir)) return alive;
        foreach (var path in Directory.GetFiles(autoSaveDir, $"{CrashFlagPrefix}*{CrashFlagExt}"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!fileName.StartsWith(CrashFlagPrefix, StringComparison.Ordinal)) continue;
            var instanceId = fileName[CrashFlagPrefix.Length..];
            if (IsInstanceFlagAlive(path)) alive.Add(instanceId);
        }
        return alive;
    }

    private static bool IsInstanceFlagAlive(string flagPath)
    {
        try
        {
            var content = File.ReadAllText(flagPath);
            var parts = content.Split('|');
            if (!int.TryParse(parts[0], out var pid)) return false;
            using var process = System.Diagnostics.Process.GetProcessById(pid); // 存在しなければArgumentException

            // PID再利用対策: フラグ書き込み時刻より後に開始したプロセスは、同じPIDを使い回した
            // 別プロセスなので「元のインスタンスは死んでいる」と判定する。開始時刻が取得できない
            // (権限不足など)場合や、フラグに時刻が無い旧形式の場合は従来どおり「生きている」とみなす。
            if (parts.Length > 1
                && DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var flagTime))
            {
                try
                {
                    var startUtc = process.StartTime.ToUniversalTime();
                    if (startUtc > flagTime.ToUniversalTime() + PidReuseTolerance) return false;
                }
                catch { /* 開始時刻が取れない場合は判定不能 → 生存扱い */ }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>生きていないインスタンスのフラグファイルを削除する(2026-07-26)。起動時、
    /// crashSuspectedスロットの提示可否を判定し終えた後に呼ぶ想定(古い痕跡の掃除)。</summary>
    public static void PurgeDeadInstanceFlags(string autoSaveDir, IEnumerable<string> deadInstanceIds)
    {
        foreach (var instanceId in deadInstanceIds)
        {
            var path = GetInstanceFlagPath(autoSaveDir, instanceId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- マニフェスト(開いているセッション一覧) ---

    public static List<AutoSaveSlotInfo> LoadManifest(string autoSaveDir) => TryLoadManifest(autoSaveDir, out _);

    /// <summary>manifestを読む。壊れていた場合は「復旧候補なし」として空リストを返し(起動を止めない)、
    /// corrupted=true を返す。呼び出し側(書き込み経路)は、壊れたmanifestを黙って上書きして
    /// 復旧情報を失わないよう、退避してから作り直す。</summary>
    private static List<AutoSaveSlotInfo> TryLoadManifest(string autoSaveDir, out bool corrupted)
    {
        corrupted = false;
        var path = GetManifestPath(autoSaveDir);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AutoSaveSlotInfo>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            corrupted = true;
            return [];
        }
    }

    private static void SaveManifest(string autoSaveDir, List<AutoSaveSlotInfo> slots)
    {
        AtomicFile.WriteAllText(GetManifestPath(autoSaveDir), JsonSerializer.Serialize(slots, ManifestJsonOpts));
    }

    /// <summary>manifestの「読む→変更→書く」を、ロックファイルで複数プロセス間直列化して行う。
    /// ロックが取れない場合(長時間占有されているなど)は、保存自体を止めないためロック無しで続行する。
    /// 壊れたmanifestは manifest.corrupt-*.json へ退避してから作り直す。</summary>
    private static void UpdateManifest(string autoSaveDir, Func<List<AutoSaveSlotInfo>, bool> mutate)
    {
        Directory.CreateDirectory(autoSaveDir);
        using var lockHandle = AcquireManifestLock(autoSaveDir);

        var manifest = TryLoadManifest(autoSaveDir, out var corrupted);
        if (corrupted)
        {
            try
            {
                var backup = Path.Combine(autoSaveDir, $"manifest.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
                File.Copy(GetManifestPath(autoSaveDir), backup, overwrite: false);
            }
            catch { /* 退避に失敗しても続行 */ }
        }

        if (mutate(manifest) || corrupted)
            SaveManifest(autoSaveDir, manifest);
    }

    private static FileStream? AcquireManifestLock(string autoSaveDir)
    {
        var lockPath = Path.Combine(autoSaveDir, ManifestLockFileName);
        for (var attempt = 0; attempt < 40; attempt++) // 最大およそ2秒
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        return null;
    }

    // --- スロットの読み書き ---

    /// <summary>1セッション分の自動保存を書き込み、manifestの当該エントリを更新(無ければ追加)する。
    /// 呼び出し側は「変更がある(IsModified)セッションのみ」呼ぶ想定(変更が無い間は無駄な書き込みを
    /// しない、というユーザー確定仕様)。</summary>
    public static void WriteSlot(string autoSaveDir, string slotId, string instanceId, string? lastKnownPath, string projectName, string projectJson)
    {
        AtomicFile.WriteAllText(GetSlotFilePath(autoSaveDir, slotId), projectJson);

        UpdateManifest(autoSaveDir, manifest =>
        {
            manifest.RemoveAll(s => s.SlotId == slotId);
            manifest.Add(new AutoSaveSlotInfo(slotId, instanceId, lastKnownPath, projectName, DateTime.UtcNow));
            return true;
        });
    }

    /// <summary>手動保存(Ctrl+S)完了時、またはセッションを閉じた時に呼ぶ。そのスロットはもう
    /// 復旧対象として意味を持たないため、スロットファイル・manifestエントリの両方を削除する。</summary>
    public static void ClearSlot(string autoSaveDir, string slotId)
    {
        var path = GetSlotFilePath(autoSaveDir, slotId);
        if (File.Exists(path)) File.Delete(path);

        if (!Directory.Exists(autoSaveDir)) return;
        UpdateManifest(autoSaveDir, manifest => manifest.RemoveAll(s => s.SlotId == slotId) > 0);
    }

    public static string ReadSlotContent(string autoSaveDir, string slotId) => File.ReadAllText(GetSlotFilePath(autoSaveDir, slotId));
}

/// <summary>
/// 自動保存スロット1件分のメタ情報(manifest.jsonの要素)。
/// </summary>
/// <param name="SlotId">セッションごとに割り当てる一意なID(Guid文字列)。</param>
/// <param name="InstanceId">このスロットを書き込んだプロセス(ウィンドウ)のインスタンスID
/// (2026-07-26追加、複数ウィンドウ利用時に「まだ生きている別ウィンドウ」のセッションを
/// 誤って復旧候補にしないためのもの)。旧バージョンのmanifestから読み込んだ場合は空文字列になる。</param>
/// <param name="LastKnownPath">最後にわかっている保存先パス。未保存の新規プロジェクトならnull。</param>
/// <param name="ProjectName">復旧ダイアログ表示用のプロジェクト名。</param>
/// <param name="SavedAtUtc">最終自動保存日時(UTC)。</param>
public sealed record AutoSaveSlotInfo(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("lastKnownPath")] string? LastKnownPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("savedAtUtc")] DateTime SavedAtUtc);
