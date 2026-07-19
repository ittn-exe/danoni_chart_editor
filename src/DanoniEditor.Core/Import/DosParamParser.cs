using System.Text.RegularExpressions;

namespace DanoniEditor.Core.Import;

/// <summary>
/// dos.txt形式のパラメータ(|名前=値|)を抽出するパーサ。
/// JSラッパー(externalDosInit)内でも、値内の改行(複数行difData等)でも動作する。
/// </summary>
public static partial class DosParamParser
{
    [GeneratedRegex(@"\|(?<name>[A-Za-z0-9_]+)=(?<value>[^|]*)", RegexOptions.Singleline)]
    private static partial Regex ParamRegex();

    /// <summary>テキスト全体から |name=value| を全て抽出(同名が複数あれば後勝ち)</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in ParamRegex().Matches(text))
            result[m.Groups["name"].Value] = m.Groups["value"].Value.Trim();
        return result;
    }

    /// <summary>カンマ区切り数値列(空要素は無視)をdouble配列へ</summary>
    public static double[] ParseNumberList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
             .ToArray();
}
