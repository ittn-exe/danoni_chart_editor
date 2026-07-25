using DanoniEditor.Core.Persistence;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>
/// AutoSaveManager(2026-07-25、TBD「自動保存・クラッシュ復旧」、2026-08-06 複数ウィンドウ
/// [=複数プロセス]対応でインスタンス単位のクラッシュフラグに改訂)のテスト。
/// 実ファイルI/Oを行うため、テストごとにOSの一時フォルダ配下へ使い捨てディレクトリを作って検証する。
/// </summary>
public class AutoSaveManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "danoni_autosave_test_" + Guid.NewGuid().ToString("N"));

    // 「生きているプロセス」の代わりに、このテストプロセス自身のPIDを使う(実在確認可能なため)。
    private static readonly int OwnPid = Environment.ProcessId;
    // 「生きていないプロセス」を模すための、まず実在しないだろう非常に大きなPID値。
    private const int DeadPid = 999_999_999;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CrashFlag_AliveProcess_IsReportedAlive()
    {
        AutoSaveManager.SetCrashFlag(_dir, "inst-a", OwnPid);

        var alive = AutoSaveManager.GetAliveInstanceIds(_dir);
        Assert.Contains("inst-a", alive);
    }

    [Fact]
    public void CrashFlag_DeadProcess_IsNotReportedAlive()
    {
        AutoSaveManager.SetCrashFlag(_dir, "inst-a", DeadPid);

        var alive = AutoSaveManager.GetAliveInstanceIds(_dir);
        Assert.DoesNotContain("inst-a", alive);
    }

    [Fact]
    public void CrashFlag_ClearedByOwnInstance_DoesNotAffectOtherInstances()
    {
        AutoSaveManager.SetCrashFlag(_dir, "inst-a", OwnPid);
        AutoSaveManager.SetCrashFlag(_dir, "inst-b", OwnPid);

        AutoSaveManager.ClearCrashFlag(_dir, "inst-a");

        var alive = AutoSaveManager.GetAliveInstanceIds(_dir);
        Assert.DoesNotContain("inst-a", alive);
        Assert.Contains("inst-b", alive); // 他インスタンスのフラグには影響しない(2026-08-06の主眼)
    }

    [Fact]
    public void ClearCrashFlag_WhenNotSet_DoesNotThrow()
    {
        AutoSaveManager.ClearCrashFlag(_dir, "inst-a"); // フォルダ自体が無い状態でも例外にならないこと
        Assert.Empty(AutoSaveManager.GetAliveInstanceIds(_dir));
    }

    [Fact]
    public void PurgeDeadInstanceFlags_RemovesOnlySpecifiedFlags()
    {
        AutoSaveManager.SetCrashFlag(_dir, "inst-a", DeadPid);
        AutoSaveManager.SetCrashFlag(_dir, "inst-b", OwnPid);

        AutoSaveManager.PurgeDeadInstanceFlags(_dir, ["inst-a"]);

        Assert.False(File.Exists(AutoSaveManager.GetInstanceFlagPath(_dir, "inst-a")));
        Assert.True(File.Exists(AutoSaveManager.GetInstanceFlagPath(_dir, "inst-b")));
    }

    [Fact]
    public void WriteSlot_CreatesFileAndManifestEntry()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", "inst-a", @"C:\proj\a.json", "曲A", "{\"dummy\":1}");

        Assert.Equal("{\"dummy\":1}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));

        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("slot-a", entry.SlotId);
        Assert.Equal("inst-a", entry.InstanceId);
        Assert.Equal(@"C:\proj\a.json", entry.LastKnownPath);
        Assert.Equal("曲A", entry.ProjectName);
    }

    [Fact]
    public void WriteSlot_CalledAgainForSameSlotId_ReplacesNotDuplicates()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", "inst-a", null, "曲A", "{\"v\":1}");
        AutoSaveManager.WriteSlot(_dir, "slot-a", "inst-a", null, "曲A(改題)", "{\"v\":2}");

        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("曲A(改題)", entry.ProjectName);
        Assert.Equal("{\"v\":2}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));
    }

    [Fact]
    public void MultipleSlots_AreIndependent()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", "inst-a", null, "曲A", "{\"v\":\"a\"}");
        AutoSaveManager.WriteSlot(_dir, "slot-b", "inst-a", null, "曲B", "{\"v\":\"b\"}");

        var manifest = AutoSaveManager.LoadManifest(_dir);
        Assert.Equal(2, manifest.Count);
        Assert.Equal("{\"v\":\"a\"}", AutoSaveManager.ReadSlotContent(_dir, "slot-a"));
        Assert.Equal("{\"v\":\"b\"}", AutoSaveManager.ReadSlotContent(_dir, "slot-b"));
    }

    [Fact]
    public void ClearSlot_RemovesFileAndManifestEntry_LeavesOthersIntact()
    {
        AutoSaveManager.WriteSlot(_dir, "slot-a", "inst-a", null, "曲A", "{}");
        AutoSaveManager.WriteSlot(_dir, "slot-b", "inst-a", null, "曲B", "{}");

        AutoSaveManager.ClearSlot(_dir, "slot-a");

        Assert.False(File.Exists(AutoSaveManager.GetSlotFilePath(_dir, "slot-a")));
        var manifest = AutoSaveManager.LoadManifest(_dir);
        var entry = Assert.Single(manifest);
        Assert.Equal("slot-b", entry.SlotId);
    }

    [Fact]
    public void ClearSlot_WhenSlotDoesNotExist_DoesNotThrow()
    {
        AutoSaveManager.ClearSlot(_dir, "nonexistent"); // 例外にならないこと
        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }

    [Fact]
    public void LoadManifest_WhenFileMissing_ReturnsEmptyList()
    {
        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }

    [Fact]
    public void LoadManifest_WhenFileCorrupted_ReturnsEmptyListInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AutoSaveManager.GetManifestPath(_dir), "{ this is not valid json ");

        Assert.Empty(AutoSaveManager.LoadManifest(_dir));
    }

    /// <summary>2026-08-06の主眼: 「別ウィンドウ(生きているプロセス)がまだ開いているだけ」の
    /// セッションは、それが属するインスタンスが生きている限り復旧候補から除外できることを確認する
    /// (App層側のフィルタ処理: manifest.Where(s => !alive.Contains(s.InstanceId)) を模した検証)。</summary>
    [Fact]
    public void Scenario_AliveInstanceSlotsAreDistinguishableFromDeadInstanceSlots()
    {
        AutoSaveManager.SetCrashFlag(_dir, "inst-alive", OwnPid);
        AutoSaveManager.SetCrashFlag(_dir, "inst-dead", DeadPid);
        AutoSaveManager.WriteSlot(_dir, "slot-alive", "inst-alive", null, "生存中の曲", "{}");
        AutoSaveManager.WriteSlot(_dir, "slot-dead", "inst-dead", null, "クラッシュした曲", "{}");

        var alive = AutoSaveManager.GetAliveInstanceIds(_dir);
        var recoverable = AutoSaveManager.LoadManifest(_dir).Where(s => !alive.Contains(s.InstanceId)).ToList();

        var entry = Assert.Single(recoverable);
        Assert.Equal("slot-dead", entry.SlotId);
    }
}
