using System.Globalization;
using System.Text;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Export;

/// <summary>
/// エディタのKeyTemplateを、danoniplus本体の「カスタムキー」定義(|keyCtrlX=...|等の
/// パイプ区切りヘッダー行群)として書き出す(2026-08-02要望対応)。
///
/// 本体側のカスタムキーは特別な仕組みではなく、dos.txtヘッダー(またはdanoni_settings.js等の
/// 共通設定ファイル)に「パラメータ名+キー種ID」のヘッダー行を並べるだけで実現される
/// (danoni_main.js keysConvert、5228行目付近)。出力先がdos.txtかdanoni_settings.jsかで
/// 書式に違いは無いため、本エクスポータは「テキストの塊」を1つ生成するだけに留め、
/// ユーザーが用途に応じてコピー&ペーストする運用とする(2026-08-02方針)。
///
/// 対応フィールドと本体側ヘッダー名の対応(danoni_main.js 5505〜5620行付近の実装を参照して
/// 決定。行番号は本体ソースの参照時点のものであり将来変わり得る):
/// - keyCtrl{X}: レーンごとのキー割当(LaneDef.KeyAssign)。本体はKeyboardEvent.code準拠の
///   キー名(danoni_constants.js g_kCdN)を期待し、getKeyCtrlVal(danoni_main.js 5215行付近)が
///   ["_kCdN", "Key{_kCdN}", "Arrow{_kCdN}"]のいずれかとg_kCdNの登録名が一致するかで解決する
///   (2026-08-02、実例のkeyCtrl9tを本体ソースと照合して確認済み。数値の生キーコード("103"等)も
///   parseIntフォールバックで有効だが、英語キー名表記の方が可読性が高いため引き続きこちらを採用)。
///   エディタのKeyAssignは表示用に矢印記号("←"等)やテンキー短縮表記("Num5"等)を使うため、
///   本エクスポータはEngineKeyNamesで本体互換名("Left"/"Numpad5"/"NumpadAdd"等)へ変換してから
///   出力する。表に無い値(英字キー等、既に本体互換の値)はそのまま出力する。"["/"]"/"`"は
///   JIS配列/US配列で対応する本体コードが食い違うため、KeyTemplate.KeyboardLayoutに応じて
///   LayoutSpecificEngineKeyNames側で変換する(2026-08-02要望対応)。
/// - chara{X}: レーンごとの読み込み変数名接頭辞(LaneDef.DataName)。本体は{chara}_dataという
///   変数名で譜面データを読み込むため、省略時の自動生成名(1a/2a等)ではエディタが出力する
///   dos.txtの実際のデータ名("left_data"等)と一致しない。DataNameをそのまま出力することで
///   本エクスポータの出力とdos.txt側のデータ名を一致させる(2026-08-02、ユーザー提供資料の
///   noteNames("left_data"等)とchara("left"等)の対応関係から確認)。
/// - color{X}: レーンごとのColorGroup。
/// - pos{X}: レーンごとのPosIndex。
/// - div{X}: KeyTemplate.DivideCnt/PosMaxを"div,divMax"のカンマ2つ組で1ヘッダーに出力する
///   (2026-08-02不具合修正: divMax{X}という独立ヘッダーは本体側に読み取り処理が無く、
///   出力しても無視される。またDivideCntは本体div値-1で保持しているため+1して出力する)。
/// - blank{X}: KeyTemplate.Blank。
/// - scroll{X}: レーンごとのScrollDirection("down"→1、"up"→-1)を"Default::"接頭辞付きで出力する
///   (2026-08-02不具合修正: 名前"::"値の形式が必須で、無いと値全体が名前と誤解釈され
///   本来の値は既定値1で埋められて破棄される)。
/// - stepRtn{X}: レーンごとの回転角、ただしNoteGraphicが"arrow"以外のレーンは回転角の
///   代わりにNoteGraphic名をそのまま出力する(danoni_constants.js
///   stepRtn5_0_0 = [0, -90, 90, 180, `onigiri`] を参照、数値要素と文字列要素が混在する形式)。
/// - keyName{X}: KeyTypeName。
/// - keyHelpJa{X}/keyHelpEn{X}: エディタに対応フィールドが無いため、呼び出し元(UI)から
///   都度渡してもらう(省略可、空文字なら出力しない)。
///
/// キーパターン(ExtraPatterns、$区切りの複数パターン定義)対応(2026-08-02追加、ユーザー提供の
/// 公式wiki(keys/tips-0004-extrakeys/tips-0006-keypattern-update)を精査して確認した仕様):
/// 「キーパターンが複数ある場合は"$"で区切る」(本体wikiより)対象フィールドはkeyCtrl/chara/color/
/// pos/div/blank/scroll/stepRtnで、本体側には「他キー種/他パターンの値を丸ごと再利用する」
/// 略記(`(キー数)_(パターン番号-1)`形式、例:"9A_0")もあるが、本体バージョンによる対応差
/// (略記はv30.5.0以降、blankは略記非対応)があり、エディタは各パターンの実データを常に
/// 完全に保持しているため、本エクスポータは略記を一切使わず全パターン分の値をそのまま
/// $区切りで書き出す(バージョン非依存で確実に動作する)。パターンが1つ(ExtraPatterns未使用)の
/// 場合は従来通り$無しの単一値になる(既存のエクスポート結果は変化しない)。
/// charaは本体仕様上パターンごとに独立指定可能な項目だが、エディタのLaneDef.DataNameは
/// パターン非依存(レーンの素性、KeyTemplate.WithPattern参照)のため、複数パターン時も
/// 同じ値をパターン数ぶん繰り返して出力する(実例のchara9t=...$9t_0が単一値のまま省略せず
/// 明示的に$区切りしていたことに倣う)。
///
/// 意図的に対象外としたもの(将来必要になれば追加):
/// - keyGroup{X}/keyGroupOrder{X}等: 本体側に省略時の自動生成規則があり、エディタ側に
///   対応するデータも無いため省略。
/// - scroll{X}/color{X}の複数名称パターンセット切替(Cross/Split等、"/"区切りで複数の名前付き
///   パターンを並べて後からユーザーが選べるようにする機能)。scroll{X}の"名前::値"自体は
///   本体仕様上必須のため固定名("Default")で1パターンのみ出力する(2026-08-02修正)。
/// - append{X}/transKey{X}(既存キーへのパターン追加・別キーモード): エディタのExtraPatternsは
///   常に「新規カスタムキーを1から定義する」用途を想定しており、既存キーへの追記機能は対象外。
/// </summary>
public static class CustomKeyTemplateExporter
{
    /// <summary>エディタのキー割当表示ラベル→本体互換キー名(KeyboardEvent.code準拠、
    /// danoni_constants.js g_kCdN)の変換表。矢印記号("←"等)とテンキーの短縮表記("Num5"等、
    /// KeyLabelMapperのラベル)は本体側の命名(g_kCdN[37]="ArrowLeft"→"Left"、
    /// g_kCdN[101]="Numpad5"→そのまま"Numpad5"等)と食い違うため変換が必要
    /// (2026-08-02、実例のkeyCtrl9tを本体ソースと照合して発覚。特にテンキーは変換無しだと
    /// getKeyCtrlValが解決できずparseInt→NaNとなり、レーンが機能しなくなる不具合があった)。
    /// 表に無い値(英字キー・Space・Shift等、既に本体互換の値)はそのまま出力する。</summary>
    private static readonly Dictionary<string, string> EngineKeyNames = new()
    {
        ["←"] = "Left",
        ["↓"] = "Down",
        ["↑"] = "Up",
        ["→"] = "Right",
        ["Num0"] = "Numpad0",
        ["Num1"] = "Numpad1",
        ["Num2"] = "Numpad2",
        ["Num3"] = "Numpad3",
        ["Num4"] = "Numpad4",
        ["Num5"] = "Numpad5",
        ["Num6"] = "Numpad6",
        ["Num7"] = "Numpad7",
        ["Num8"] = "Numpad8",
        ["Num9"] = "Numpad9",
        ["Num+"] = "NumpadAdd",
        ["Num-"] = "NumpadSubtract",
        ["Num*"] = "NumpadMultiply",
        ["Num/"] = "NumpadDivide",
        ["Num."] = "NumpadDecimal",
        // 2026-08-02不具合修正: 記号キー("<"等)が一切変換されずそのまま出力されており、本体側の
        // KeyCtrlCodeList(公式wiki)が期待するKeyboardEvent.code名と食い違って反応しないバグが
        // あった(ユーザー報告)。KeyLabelMapperが割当可能とする記号キー全てを変換表へ追加する。
        // このうち"["/"]"/"`"はJIS配列とUS配列で対応するエンジンコードが食い違う(下記
        // LayoutSpecificEngineKeyNames参照)ため、レイアウトに依存しないものだけをここに置く。
        ["<"] = "Comma",
        [">"] = "Period",
        [";"] = "Semicolon",
        [":"] = "Quote",
        ["@"] = "BracketLeft", // JIS配列の@キー固有のラベルのため常に本体BracketLeft(レイアウト非依存)
        ["-"] = "Minus",
        ["="] = "Equal",
        ["/"] = "Slash",
        ["\\"] = "Backslash", // JIS配列に対応する記号キーが無いため常にUS配列の意味で扱う
        // 2026-08-02: KeyLabelMapperの新規対応キーのうち、本体側の略称(g_kCdN)が編集画面の
        // ラベルとそのまま一致しないもの。Esc→Escape、Ctrl→Control("Ctrl"は本体側の略称に無く
        // "Control"/"ControlLeft"のみ有効)。Alt/Shift/Tab/Space/Enter/Backspace/Delete/Insert/
        // Home/End/PageUp/PageDown/F1〜F15は略称自体がそのまま本体互換のため変換不要。
        ["Esc"] = "Escape",
        ["Ctrl"] = "Control",
    };

    /// <summary>"["/"]"/"`"の変換表(2026-08-02要望対応、KeyTemplate.KeyboardLayoutで選択)。
    /// 本体のKeyCtrlCodeList(公式wiki)によれば、この3記号はJIS配列とUS配列で1つずつ後ろへずれる
    /// (JIS: @→BracketLeft/[→BracketRight/]→Backslash、US: [→BracketLeft/]→BracketRight/
    /// `→Backquote)。"\"はJIS配列に対応する記号キーが無いため常にUS配列の意味(EngineKeyNames側)
    /// のまま扱う。</summary>
    private static readonly Dictionary<KeyboardLayout, Dictionary<string, string>> LayoutSpecificEngineKeyNames = new()
    {
        [KeyboardLayout.Jis] = new() { ["["] = "BracketRight", ["]"] = "Backslash" },
        [KeyboardLayout.Us] = new() { ["["] = "BracketLeft", ["]"] = "BracketRight", ["`"] = "Backquote" },
    };

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static void AppendParam(StringBuilder sb, string name, string value)
        => sb.AppendLine($"|{name}={value}|");

    /// <summary>本体互換のキー名へ変換する(KeyAssignは複数キー割当("/"複数)を持ち得るが、
    /// 本体keyCtrlは1レーン1エントリの中で複数物理キーを"/"区切りで受け付けるため、
    /// そのままKeyAssignの要素を"/"で連結すればよい)。</summary>
    private static string ToEngineKeyName(string keyAssign, KeyboardLayout layout) =>
        LayoutSpecificEngineKeyNames[layout].TryGetValue(keyAssign, out var layoutMapped) ? layoutMapped
        : EngineKeyNames.TryGetValue(keyAssign, out var mapped) ? mapped : keyAssign;

    private static string EngineKeyCtrlValue(LaneDef lane, KeyboardLayout layout) =>
        string.Join("/", lane.KeyAssign.Select(k => ToEngineKeyName(k, layout)));

    /// <summary>ScrollDirectionを本体の1/-1表記へ変換する。"down"(標準)=1、"up"(逆行)=-1。</summary>
    private static int ScrollValue(LaneDef lane) => lane.ScrollDirection == "up" ? -1 : 1;

    /// <summary>stepRtn値。NoteGraphicが"arrow"のレーンは回転角(数値)、それ以外はNoteGraphic名
    /// (onigiri/giko/iyo/c/monar/morara等)をそのまま出力する
    /// (danoni_constants.js stepRtn5_0_0等、数値要素と文字列要素が混在する形式に合わせる)。</summary>
    private static string StepRtnValue(LaneDef lane) =>
        lane.NoteGraphic == "arrow" ? Num(lane.RotationAngle) : lane.NoteGraphic;

    /// <summary>パターン数ぶんの値を"$"区切りで連結する。パターンが1つだけの場合は$を含まない
    /// 単一値になる(既存の単一パターンテンプレートの出力を変えないため)。</summary>
    private static string JoinPatterns(int patternCount, Func<int, string> patternValue) =>
        string.Join("$", Enumerable.Range(0, patternCount).Select(patternValue));

    /// <summary>KeyTemplateから、本体のカスタムキー定義ヘッダー行群を生成する(2026-08-02:
    /// ExtraPatterns(キーパターン)にも対応、パターンが2つ以上ある場合は各フィールドを$区切りで
    /// 全パターンぶん出力する)。keyHelpJa/keyHelpEnは省略可(空またはnullなら出力しない)。</summary>
    public static string Export(KeyTemplate template, string? keyHelpJa = null, string? keyHelpEn = null)
    {
        var id = template.KeyTypeId;
        var sb = new StringBuilder();

        int patternCount = template.PatternCount;
        // WithPattern(0)はthisをそのまま返す(KeyTemplate.WithPattern参照)ため、
        // effectivePatterns[0]は基底パターン(従来通りの単一パターン)と完全に一致する。
        var effectivePatterns = Enumerable.Range(0, patternCount).Select(template.WithPattern).ToList();

        AppendParam(sb, $"keyCtrl{id}", JoinPatterns(patternCount,
            i => string.Join(",", effectivePatterns[i].Lanes.Select(l => EngineKeyCtrlValue(l, template.KeyboardLayout)))));
        // charaはパターン非依存(LaneDef.DataName、レーンの素性)のため、複数パターン時も
        // 同じ値をパターン数ぶん繰り返す(本体側はcharaもパターンごとの指定に対応しているが、
        // エディタのモデル上は変わりようが無い)。
        AppendParam(sb, $"chara{id}", JoinPatterns(patternCount,
            _ => string.Join(",", template.Lanes.Select(l => l.DataName))));
        AppendParam(sb, $"color{id}", JoinPatterns(patternCount,
            i => string.Join(",", effectivePatterns[i].Lanes.Select(l => l.ColorGroup.ToString(CultureInfo.InvariantCulture)))));
        AppendParam(sb, $"pos{id}", JoinPatterns(patternCount,
            i => string.Join(",", effectivePatterns[i].Lanes.Select(l => Num(l.PosIndex)))));
        // 2026-08-02不具合修正: div{X}とdivMax{X}は別々のヘッダーではなく、div{X}の値自体を
        // "div,divMax"のカンマ2つ組で書く仕様(本体keysConvert、divMax{X}という見出しは本体側に
        // 読み取り処理が存在せず無視される)。また、エディタ内部のDivideCntは本体div値から1引いた
        // 値(KeyTemplate.DivideCntのコメント参照)のため、出力時は+1して本体div値へ戻す。
        AppendParam(sb, $"div{id}", JoinPatterns(patternCount,
            i => $"{Num(effectivePatterns[i].DivideCnt + 1)},{Num(effectivePatterns[i].PosMax)}"));
        AppendParam(sb, $"blank{id}", JoinPatterns(patternCount, i => Num(effectivePatterns[i].Blank)));
        // 2026-08-02不具合修正: scroll{X}は「名前::値」形式が必須(本体newKeyPairParamは"::"で
        // 分割し、無ければ値全体を名前と誤認して肝心の値は既定値1で埋められ破棄される)。
        // 名前付きパターン切替は非対応のため、固定名("Default")を付与するのみに留める。
        AppendParam(sb, $"scroll{id}", JoinPatterns(patternCount,
            i => $"Default::{string.Join(",", effectivePatterns[i].Lanes.Select(l => ScrollValue(l).ToString(CultureInfo.InvariantCulture)))}"));
        AppendParam(sb, $"stepRtn{id}", JoinPatterns(patternCount,
            i => string.Join(",", effectivePatterns[i].Lanes.Select(StepRtnValue))));
        AppendParam(sb, $"keyName{id}", template.KeyTypeName);

        if (!string.IsNullOrWhiteSpace(keyHelpJa)) AppendParam(sb, $"keyHelpJa{id}", keyHelpJa);
        if (!string.IsNullOrWhiteSpace(keyHelpEn)) AppendParam(sb, $"keyHelpEn{id}", keyHelpEn);

        return sb.ToString();
    }
}
