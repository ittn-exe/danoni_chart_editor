namespace DanoniEditor.Core.Naming;

/// <summary>
/// 通常ノートのdataNameからフリーズアロー名を導出する。
/// danoniplus本体の g_escapeStr.frzName 置換テーブル + フォールバック規則
/// (danoni_main.js L3026-3030)を完全再現。
/// </summary>
public static class FrzNameResolver
{
    // 順序が重要(leftdia を left より先に評価する)
    private static readonly (string From, string To)[] ReplaceTable =
    [
        ("leftdia", "frzLdia"), ("rightdia", "frzRdia"),
        ("left", "frzLeft"), ("down", "frzDown"), ("up", "frzUp"), ("right", "frzRight"),
        ("space", "frzSpace"), ("iyo", "frzIyo"), ("gor", "frzGor"), ("oni", "foni"),
    ];

    /// <summary>例: left→frzLeft, sleft→sfrzLeft, leftdia→frzLdia, oni→foni, 1x→frz1x</summary>
    public static string Resolve(string dataName)
    {
        var name = dataName;
        foreach (var (from, to) in ReplaceTable)
            name = name.Replace(from, to);

        if (!name.Contains("frz") && !name.Contains("foni"))
            name = "frz" + char.ToUpperInvariant(name[0]) + name[1..];

        return name;
    }
}
