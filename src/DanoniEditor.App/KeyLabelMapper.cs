using System.Windows.Input;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>
/// キー割当ラベル("←"、"S"、"Space"等)とWPFの<see cref="Key"/>との対応表(2026-07-21、SKB操作モード対応で
/// PlaytestWindow内部private実装から抽出・共有化)。
/// - Playtest(<see cref="LaneDef.KeyAssign"/>、実プレイキー)
/// - キーボードモード(<see cref="LaneDef.KeyboardInputKeys"/>、ノート入力キー)
/// の両方から参照される。
/// </summary>
internal static class KeyLabelMapper
{
    /// <summary>ラベル1件を対応する物理キー(複数候補あり得る、例:数字キーはメイン列/テンキー両対応)へ変換。
    /// 対応するキーが無い場合は空リストを返す。</summary>
    public static IReadOnlyList<Key> KeysForLabel(string label) => label switch
    {
        "←" => [Key.Left],
        "↓" => [Key.Down],
        "↑" => [Key.Up],
        "→" => [Key.Right],
        "<" => [Key.OemComma],
        ">" => [Key.OemPeriod],
        ";" => [Key.OemSemicolon],
        ":" => [Key.OemQuotes],
        "@" => [Key.OemOpenBrackets, Key.Oem3], // JIS配列の@(本家keycode BracketLeft由来)
        "[" => [Key.OemOpenBrackets], // 2026-07-21: SKB式12ikey対応で追加(本家keycode BracketLeft)
        "]" => [Key.OemCloseBrackets], // 2026-07-21: 同上(本家keycode BracketRight)
        "Space" or "SP" or "␣" or " " => [Key.Space],
        "Enter" => [Key.Enter],
        _ when label.Length == 1 && label[0] is >= 'A' and <= 'Z' => [(Key)((int)Key.A + (label[0] - 'A'))],
        _ when label.Length == 1 && label[0] is >= 'a' and <= 'z' => [(Key)((int)Key.A + (char.ToUpperInvariant(label[0]) - 'A'))],
        _ when label.Length == 1 && label[0] is >= '0' and <= '9' =>
            [(Key)((int)Key.D0 + (label[0] - '0')), (Key)((int)Key.NumPad0 + (label[0] - '0'))],
        _ => [],
    };

    /// <summary>レーン定義列(テンプレートのLanes)から「物理キー→レーン番号」の逆引き表を構築する。
    /// <paramref name="labelsForLane"/>は各レーンのラベル列(KeyAssignまたはKeyboardInputKeys)を渡す。
    /// 変換できなかったラベルは<paramref name="unmapped"/>に集約される(呼び出し元で警告表示等に利用可)。</summary>
    public static Dictionary<Key, int> BuildKeyMap(
        int laneCount,
        Func<int, IReadOnlyList<string>> labelsForLane,
        ICollection<string>? unmapped = null)
    {
        var map = new Dictionary<Key, int>();
        for (int lane = 0; lane < laneCount; lane++)
        {
            foreach (var label in labelsForLane(lane))
            {
                var keys = KeysForLabel(label);
                if (keys.Count == 0)
                {
                    unmapped?.Add($"{label}(レーン{lane + 1})");
                    continue;
                }
                foreach (var k in keys) map[k] = lane;
            }
        }
        return map;
    }
}
