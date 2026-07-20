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
    /// アプリケーション環境設定ファイルの格納フォルダ(exe直下の./settings、2026-07-20)。
    /// 将来設定ファイルの種類が増えた場合に備えてサブディレクトリに分離する。
    /// </summary>
    public static string SettingsDir => Path.Combine(AppContext.BaseDirectory, "settings");

    /// <summary>
    /// アプリケーション環境設定ファイル(仕様書14章)のパス。ポータブル方式
    /// (settings.jsonが無ければ初回起動として既定値を使う)。フォルダが無ければ保存時に作成する。
    /// </summary>
    public static string SettingsFilePath => Path.Combine(SettingsDir, "settings.json");

    /// <summary>
    /// プロジェクトファイルの既定保存フォルダ(仕様書3.1確定: exe直下の./projects)。
    /// exeと同じフォルダにプロジェクトファイルが散らばるのを避けるための専用フォルダ。
    /// フォルダが無ければ保存/参照時に作成する。
    /// </summary>
    public static string ProjectsDir => Path.Combine(AppContext.BaseDirectory, "projects");
}
