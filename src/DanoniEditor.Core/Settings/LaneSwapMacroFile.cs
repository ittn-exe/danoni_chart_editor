using System.Text.Json;

namespace DanoniEditor.Core.Settings;

/// <summary>
/// レーン入替マクロ(仕様書11章)の永続化。settings.json(AppSettings)とは独立したファイルで管理する。
/// マクロはプロジェクト・設定とは別ライフサイクルで増減するため、専用ファイルに切り出している。
/// 2026-07-26g: 単一ファイル(swap_macro.json)から、キー種ごとの個別ファイル
/// ("s-macro_キー種.json"、例: s-macro_5.json)へ分割した。旧形式のファイルが残っている場合は
/// <see cref="LoadAll"/>呼び出し時に自動でキー種ごとへ分割し、旧ファイルは".bak"へリネームする。
/// </summary>
public static class LaneSwapMacroFile
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>分割前の旧形式ファイル名(2026-07-26設計、2026-07-26g廃止・移行元としてのみ参照)。</summary>
    private const string LegacyFileName = "swap_macro.json";

    private const string FilePrefix = "s-macro_";
    private const string FileSuffix = ".json";

    /// <summary>単一ファイルの読み込み(下位互換・単体テスト用に残す)。壊れたファイルは空リストへ
    /// フォールバックする(アプリ起動を止めない)。</summary>
    public static List<LaneSwapMacro> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<LaneSwapMacro>>(text, JsonOpts) ?? [];
        }
        catch
        {
            // 壊れた/旧形式のファイルは空リストにフォールバック(アプリ起動を止めない)
            return [];
        }
    }

    /// <summary>単一ファイルへの書き込み(下位互換・単体テスト用に残す)。</summary>
    public static void Save(string path, IEnumerable<LaneSwapMacro> macros)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(macros.ToList(), JsonOpts));
    }

    private static string KeyTypeFilePath(string settingsDir, string keyTypeId) =>
        Path.Combine(settingsDir, $"{FilePrefix}{keyTypeId}{FileSuffix}");

    /// <summary>settingsDir内の"s-macro_*.json"を全て読み込み、1つのリストへ統合して返す
    /// (2026-07-26g)。呼び出し時、旧形式の"swap_macro.json"が残っていれば自動で分割・移行する。</summary>
    public static List<LaneSwapMacro> LoadAll(string settingsDir)
    {
        MigrateLegacyFileIfNeeded(settingsDir);

        var result = new List<LaneSwapMacro>();
        if (!Directory.Exists(settingsDir)) return result;

        foreach (var path in Directory.EnumerateFiles(settingsDir, $"{FilePrefix}*{FileSuffix}")
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            result.AddRange(Load(path));
        }
        return result;
    }

    /// <summary>渡されたマクロ一覧をTargetKeyTypeIdごとにグループ化し、"s-macro_キー種.json"へ
    /// それぞれ保存する(2026-07-26g)。あるキー種のマクロが1件も無くなった場合は、対応する
    /// ファイルが残っていれば削除する(空ファイルの残留を防ぐ)。</summary>
    public static void SaveAll(string settingsDir, IEnumerable<LaneSwapMacro> macros)
    {
        Directory.CreateDirectory(settingsDir);

        var groups = macros.GroupBy(m => m.TargetKeyTypeId).ToList();
        var keptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var path = KeyTypeFilePath(settingsDir, g.Key);
            Save(path, g.ToList());
            keptPaths.Add(path);
        }

        foreach (var existing in Directory.EnumerateFiles(settingsDir, $"{FilePrefix}*{FileSuffix}"))
        {
            if (!keptPaths.Contains(existing)) File.Delete(existing);
        }
    }

    /// <summary>旧形式の単一ファイル(swap_macro.json)が存在する場合、中身をTargetKeyTypeIdごとに
    /// "s-macro_キー種.json"へ分割保存し、旧ファイルは"swap_macro.json.bak"(既に存在すれば
    /// ".bak2"...と連番)へリネームする(2026-07-26g)。移行処理自体が失敗しても起動は止めない。</summary>
    private static void MigrateLegacyFileIfNeeded(string settingsDir)
    {
        var legacyPath = Path.Combine(settingsDir, LegacyFileName);
        if (!File.Exists(legacyPath)) return;

        try
        {
            var macros = Load(legacyPath);
            foreach (var g in macros.GroupBy(m => m.TargetKeyTypeId))
                Save(KeyTypeFilePath(settingsDir, g.Key), g.ToList());

            var backupPath = Path.Combine(settingsDir, $"{LegacyFileName}.bak");
            int suffix = 2;
            while (File.Exists(backupPath))
                backupPath = Path.Combine(settingsDir, $"{LegacyFileName}.bak{suffix++}");
            File.Move(legacyPath, backupPath);
        }
        catch
        {
            // 移行に失敗しても旧ファイルはそのまま残し、アプリ起動は止めない
        }
    }
}
