using System.Text.Json;

namespace DanoniEditor.Core.Settings;

/// <summary>
/// レーン入替マクロ(仕様書11章)の永続化(2026-07-30)。settings.json(AppSettings)とは独立した
/// swap_macro.json(同じ./settingsフォルダ内、AppPaths.LaneSwapMacroFilePath)で管理する。
/// マクロはプロジェクト・設定とは別ライフサイクルで増減するため、専用ファイルに切り出した。
/// </summary>
public static class LaneSwapMacroFile
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

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

    public static void Save(string path, IEnumerable<LaneSwapMacro> macros)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(macros.ToList(), JsonOpts));
    }
}
