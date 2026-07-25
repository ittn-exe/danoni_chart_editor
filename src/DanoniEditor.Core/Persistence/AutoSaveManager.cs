using System.Text.Json;
using System.Text.Json.Serialization;

namespace DanoniEditor.Core.Persistence;

/// <summary>
/// 自動保存(クラッシュ復旧用)のファイルI/O本体(2026-07-25、TBD「自動保存・クラッシュ復旧」)。
/// WPFに依存しない純ロジックとして切り出し、単体テスト可能にしている(DanoniEditor.App側は
/// AppPaths.AutoSaveDirを渡してこのクラスを呼ぶだけの薄いラッパーになる想定)。
///
/// 設計方針(ユーザー確定、B案): 通常の保存(Ctrl+S)には一切影響を与えない、実ファイルとは
/// 別の「復旧用スロット」へ書き込む。次回起動時に前回の異常終了を検知した場合のみ、
/// スロットの内容を「復元しますか?」と提示する。
///
/// クラッシュ検知の仕組み: 起動直後に「実行中フラグ」ファイル(running.flag)を作成し、
/// 正常終了(App.OnExit)時に削除する。次回起動時にこのファイルが残っていれば、
/// 前回は正常終了しなかった(クラッシュ・強制終了等)と判定する。
///
/// マルチプロジェクトタブ対応: セッション(開いているプロジェクトタブ)ごとに一意なSlotIdを持ち、
/// スロットファイル(slot_{SlotId}.json、中身は通常のプロジェクトファイルと同じJSON形式)と、
/// 全スロットの一覧(manifest.json: どのプロジェクトがどのパスに対応するか、最終自動保存日時)を
/// 別々に持つ。クラッシュ検知時はmanifest.jsonを読んで、開いていた全セッション分を個別に
/// 復元するか判断できるようにする。
/// </summary>
public static class AutoSaveManager
{
    private const string ManifestFileName = "manifest.json";
    private const string CrashFlagFileName = "running.flag";

    private static readonly JsonSerializerOptions ManifestJsonOpts = new() { WriteIndented = true };

    public static string GetCrashFlagPath(string autoSaveDir) => Path.Combine(autoSaveDir, CrashFlagFileName);
    public static string GetManifestPath(string autoSaveDir) => Path.Combine(autoSaveDir, ManifestFileName);
    public static string GetSlotFilePath(string autoSaveDir, string slotId) => Path.Combine(autoSaveDir, $"slot_{slotId}.json");

    // --- クラッシュフラグ ---

    public static bool IsCrashFlagSet(string autoSaveDir) => File.Exists(GetCrashFlagPath(autoSaveDir));

    /// <summary>起動直後に呼ぶ。次回起動時、これが残っていれば「前回クラッシュした」と判定される。</summary>
    public static void SetCrashFlag(string autoSaveDir)
    {
        Directory.CreateDirectory(autoSaveDir);
        File.WriteAllText(GetCrashFlagPath(autoSaveDir), DateTime.UtcNow.ToString("o"));
    }

    /// <summary>正常終了(App.OnExit)時に呼ぶ。OnClosingでキャンセルされた場合はOnExitに到達しないため、
    /// このメソッドが呼ばれるのは「実際に終了する」と確定した時だけになる。</summary>
    public static void ClearCrashFlag(string autoSaveDir)
    {
        var path = GetCrashFlagPath(autoSaveDir);
        if (File.Exists(path)) File.Delete(path);
    }

    // --- マニフェスト(開いているセッション一覧) ---

    public static List<AutoSaveSlotInfo> LoadManifest(string autoSaveDir)
    {
        var path = GetManifestPath(autoSaveDir);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AutoSaveSlotInfo>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            // 壊れたmanifestは「復旧候補なし」として扱う(起動を止めない)
            return [];
        }
    }

    private static void SaveManifest(string autoSaveDir, List<AutoSaveSlotInfo> slots)
    {
        Directory.CreateDirectory(autoSaveDir);
        File.WriteAllText(GetManifestPath(autoSaveDir), JsonSerializer.Serialize(slots, ManifestJsonOpts));
    }

    // --- スロットの読み書き ---

    /// <summary>1セッション分の自動保存を書き込み、manifestの当該エントリを更新(無ければ追加)する。
    /// 呼び出し側は「変更がある(IsModified)セッションのみ」呼ぶ想定(変更が無い間は無駄な書き込みを
    /// しない、というユーザー確定仕様)。</summary>
    public static void WriteSlot(string autoSaveDir, string slotId, string? lastKnownPath, string projectName, string projectJson)
    {
        Directory.CreateDirectory(autoSaveDir);
        File.WriteAllText(GetSlotFilePath(autoSaveDir, slotId), projectJson);

        var manifest = LoadManifest(autoSaveDir);
        manifest.RemoveAll(s => s.SlotId == slotId);
        manifest.Add(new AutoSaveSlotInfo(slotId, lastKnownPath, projectName, DateTime.UtcNow));
        SaveManifest(autoSaveDir, manifest);
    }

    /// <summary>手動保存(Ctrl+S)完了時、またはセッションを閉じた時に呼ぶ。そのスロットはもう
    /// 復旧対象として意味を持たないため、スロットファイル・manifestエントリの両方を削除する。</summary>
    public static void ClearSlot(string autoSaveDir, string slotId)
    {
        var path = GetSlotFilePath(autoSaveDir, slotId);
        if (File.Exists(path)) File.Delete(path);

        var manifest = LoadManifest(autoSaveDir);
        if (manifest.RemoveAll(s => s.SlotId == slotId) > 0)
            SaveManifest(autoSaveDir, manifest);
    }

    public static string ReadSlotContent(string autoSaveDir, string slotId) => File.ReadAllText(GetSlotFilePath(autoSaveDir, slotId));
}

/// <summary>
/// 自動保存スロット1件分のメタ情報(manifest.jsonの要素)。
/// </summary>
/// <param name="SlotId">セッションごとに割り当てる一意なID(Guid文字列)。</param>
/// <param name="LastKnownPath">最後にわかっている保存先パス。未保存の新規プロジェクトならnull。</param>
/// <param name="ProjectName">復旧ダイアログ表示用のプロジェクト名。</param>
/// <param name="SavedAtUtc">最終自動保存日時(UTC)。</param>
public sealed record AutoSaveSlotInfo(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("lastKnownPath")] string? LastKnownPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("savedAtUtc")] DateTime SavedAtUtc);
