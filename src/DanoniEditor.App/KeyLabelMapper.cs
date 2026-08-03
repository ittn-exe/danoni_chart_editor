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
        // 2026-07-26e: 標準キー種のキーパターン追加データ(8key/12key/14key等の変則配置)に
        // Shiftキーを使うものがあったため対応(左右いずれかで反応する)。
        "Shift" => [Key.LeftShift, Key.RightShift],
        // 2026-07-26e: temp_11j.json(既存出荷テンプレート、keyCtrl11j_0由来)が既に"Tab"を
        // keyAssignに使っていたが対応表に無かったため合わせて追加(未対応判明、8key/14keyの
        // キーパターン追加データでも使用するため今回まとめて対応)。
        "Tab" => [Key.Tab],
        // 2026-08-02要望対応: danoniplus本体のKeyCtrlCodeList(公式wiki)に載っている割当可能な
        // キーを網羅する。CapsLock/Windows(Meta)/Unknown/JIS配列のBackquote(IME用)は本体側が
        // 明示的に「割り当て不可」としているため対象外(意図的に未対応のまま)。
        "Esc" => [Key.Escape],
        "Backspace" => [Key.Back],
        "Delete" => [Key.Delete],
        "Insert" => [Key.Insert],
        "Home" => [Key.Home],
        "End" => [Key.End],
        "PageUp" => [Key.PageUp],
        "PageDown" => [Key.PageDown],
        // Ctrl/Altは左右いずれでも反応する(Shiftと同じ方針)。ただしCtrlの本体側略称は"Control"の
        // ためEngineKeyNames側で変換する(Alt/Shiftは略称自体が"Alt"/"Shift"のため変換不要)。
        "Ctrl" => [Key.LeftCtrl, Key.RightCtrl],
        "Alt" => [Key.LeftAlt, Key.RightAlt],
        "-" => [Key.OemMinus],
        "=" => [Key.OemPlus],
        "/" => [Key.OemQuestion],
        "\\" => [Key.OemPipe],
        // "`"(US配列のBackquote)はWPF上"@"(Key.Oem3、JIS配列)と同一のKey値(OemTilde==Oem3)のため
        // キャプチャボタンでは生成されない(常に"@"になる)が、自由入力欄からの手入力・既存テンプレート
        // のJSONインポート等では引き続きこのラベルを受け付ける(EngineKeyNamesでBackquoteへ変換)。
        "`" => [Key.OemTilde],
        // 2026-07-26e: F1〜F12(temp_12i.jsonのパターン0がkeyAssignにF1〜F12を使っているが、
        // 実は対応表に無く、これまでプレイテストでは12ikeyが一切キー入力できていなかった不具合。
        // 今回12ikeyのパターン追加データを検証していて発覚したため合わせて対応する)。
        // 2026-08-02: 本体wikiはF15まで掲載しているため範囲をF12→F15へ拡張。
        _ when label.Length is 2 or 3 && label[0] == 'F'
            && int.TryParse(label.AsSpan(1), out var fn) && fn is >= 1 and <= 15 =>
            [(Key)((int)Key.F1 + (fn - 1))],
        // 2026-07-26: テンキー専用ラベル(メイン列の数字キーとは独立して指定したい場合用)。
        // 数字キー単体のラベル("5"等)は従来通りメイン列/テンキー両対応のままにし、
        // "Num5"のようにテンキー側だけを明示的に指定したい場合の受け皿として追加する。
        "Num+" => [Key.Add],
        "Num-" => [Key.Subtract],
        "Num*" => [Key.Multiply],
        "Num/" => [Key.Divide],
        "Num." => [Key.Decimal],
        _ when label.Length == 4 && label.StartsWith("Num", StringComparison.Ordinal) && label[3] is >= '0' and <= '9' =>
            [(Key)((int)Key.NumPad0 + (label[3] - '0'))],
        _ when label.Length == 1 && label[0] is >= 'A' and <= 'Z' => [(Key)((int)Key.A + (label[0] - 'A'))],
        _ when label.Length == 1 && label[0] is >= 'a' and <= 'z' => [(Key)((int)Key.A + (char.ToUpperInvariant(label[0]) - 'A'))],
        _ when label.Length == 1 && label[0] is >= '0' and <= '9' =>
            [(Key)((int)Key.D0 + (label[0] - '0')), (Key)((int)Key.NumPad0 + (label[0] - '0'))],
        _ => [],
    };

    /// <summary>物理キー1件を対応するラベル文字列へ変換する(2026-07-26、テンプレート編集の
    /// 「入力開始→キー押下→そのキーが指定される」キャプチャUI用、<see cref="KeysForLabel"/>の逆引き)。
    /// 対応表に無いキーはnullを返す。テンキーの数字キーは"Num0"〜"Num9"、テンキーの演算子キーは
    /// "Num+"等、メイン列の数字キーは"0"〜"9"を返す(メイン列digitはKeysForLabel側でテンキーとも
    /// 相互変換されるため、キャプチャ時にどちらを押しても正しく使えるラベルになる)。</summary>
    public static string? LabelForKey(Key key) => key switch
    {
        Key.Left => "←",
        Key.Down => "↓",
        Key.Up => "↑",
        Key.Right => "→",
        Key.OemComma => "<",
        Key.OemPeriod => ">",
        Key.OemSemicolon => ";",
        Key.OemQuotes => ":",
        Key.Oem3 => "@",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.Tab => "Tab",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        // 2026-08-02: "`"(Backquote、本体のcode=Backquote)は、WPFのKey列挙体ではKey.OemTilde/Key.Oem3が
        // 同一の値(146)のため"@"(Key.Oem3、JIS配列)と区別できず、キャプチャボタンでは常に既存の
        // "@"が優先される(JIS対応が既存仕様のため)。"`"はKeysForLabel側では引き続き受け付け、
        // US配列テンプレートを手入力(自由入力欄)する場合のみ使える形とする。
        >= Key.F1 and <= Key.F15 => $"F{(int)(key - Key.F1) + 1}",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        Key.Multiply => "Num*",
        Key.Divide => "Num/",
        Key.Decimal => "Num.",
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num{(int)(key - Key.NumPad0)}",
        _ => null,
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
