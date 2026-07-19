using System.IO;

namespace DanoniEditor.App;

/// <summary>
/// アセットフォルダ(./template, ./img)の探索(仕様書2章/3.1)。
/// publish単独exe配布時はexeと同じフォルダ(csprojのContent項目でコピーされる)を最優先、
/// 見つからなければDebug実行用に上位ディレクトリを辿る(リポジトリルート直下の./template等)。
/// </summary>
internal static class AppPaths
{
    public static string? FindAssetDir(string folderName)
    {
        var exeAdjacent = Path.Combine(AppContext.BaseDirectory, folderName);
        if (Directory.Exists(exeAdjacent)) return exeAdjacent;

        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, folderName)))
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        return dir is null ? null : Path.Combine(dir, folderName);
    }

    /// <summary>
    /// アプリケーション環境設定ファイル(仕様書14章)のパス。template/imgと同じく
    /// exe直下に置くポータブル方式(settings.jsonが無ければ初回起動として既定値を使う)。
    /// </summary>
    public static string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
}
