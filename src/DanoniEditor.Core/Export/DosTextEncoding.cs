using System.Globalization;
using System.Text;

namespace DanoniEditor.Core.Export;

/// <summary>
/// dos.txt/FUJIファイルの文字コード対応(2026-08-08要望対応: エクスポート時の文字コード選択、
/// 2026-08-09要望対応: インポート時の文字コード自動判定)。
/// .NET(Core/5以降)は既定でShift-JIS(コードページ932)を扱えないため、System.Text.Encoding.CodePages
/// パッケージ(DanoniEditor.Core.csproj参照)のCodePagesEncodingProviderを実行時に登録して使う。
/// エクスポート時、変換不可文字(絵文字等、Shift-JISに存在しないコードポイント)は「?」へ置換して
/// 保存する方針(2026-08-08ユーザー確認済み: 事前にダイアログで警告した上で置換する)。
/// </summary>
public static class DosTextEncoding
{
    private static readonly object RegisterLock = new();
    private static bool _registered;

    /// <summary>CodePagesEncodingProviderの登録(初回のみ、以降は何もしない)。
    /// GetShiftJis/FindUnmappableChars/EncodeShiftJisWithReplacementの内部から自動的に呼ばれるため、
    /// 通常は呼び出し側で意識する必要は無い。</summary>
    private static void EnsureProviderRegistered()
    {
        lock (RegisterLock)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>置換無し(例外送出)のShift-JISエンコーディング。変換可否の検査専用に使う。</summary>
    private static Encoding GetShiftJisStrict()
    {
        EnsureProviderRegistered();
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>「?」へ置換するShift-JISエンコーディング。実際の書き出しに使う。</summary>
    private static Encoding GetShiftJisReplacement()
    {
        EnsureProviderRegistered();
        return Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    /// <summary>
    /// 指定文字列の中からShift-JISへ変換できない文字(コードポイント単位、サロゲートペアの絵文字等も
    /// 1つとして数える)を重複除去したうえで検出順に返す。1つも無ければ空配列。
    /// エクスポート前の確認ダイアログ表示用。
    /// </summary>
    public static IReadOnlyList<string> FindUnmappableChars(string text)
    {
        var strict = GetShiftJisStrict();
        var found = new List<string>();
        var seen = new HashSet<string>();

        var elementEnumerator = StringInfo.GetTextElementEnumerator(text);
        while (elementEnumerator.MoveNext())
        {
            string element = (string)elementEnumerator.Current;
            if (element.Length == 0) continue;
            try
            {
                strict.GetByteCount(element);
            }
            catch (EncoderFallbackException)
            {
                if (seen.Add(element)) found.Add(element);
            }
        }
        return found;
    }

    /// <summary>Shift-JISへエンコードする(変換不可文字は「?」へ置換)。</summary>
    public static byte[] EncodeShiftJisWithReplacement(string text) => GetShiftJisReplacement().GetBytes(text);

    /// <summary>
    /// 2026-08-09要望対応: dos.txt/FUJIインポート時、文字コード(UTF-8/Shift-JIS)をユーザーに
    /// 確認させることなく自動判定してデコードする。まずUTF-8として厳密デコードを試み(不正な
    /// バイト列があれば例外)、成功すればそのままUTF-8として扱う。失敗した場合のみShift-JISとして
    /// 読み直す(置換フォールバック付き、SJISとしても不正なバイト列があれば「?」等へ置換)。
    /// UTF-8は「このバイト列はUTF-8として成立し得ない」という明確な規則を持つ一方、Shift-JISの
    /// 2バイト文字が偶然その規則に合致することは実務上ほぼ無いため、この方式で高い精度の判別が
    /// できる(第三者報告の文字化け事例で実データ検証済み: Shift-JIS保存のdos.txtをUTF-8として
    /// 読み込んでいたことが原因と判明したケースで、本メソッドなら正しくShift-JISへ自動フォールバック
    /// する)。BOM付きUTF-8はBOMを除去した上でデコードする(従来のFile.ReadAllTextと同じ挙動)。
    /// </summary>
    public static (string Text, bool WasShiftJis) ReadAutoDetectText(byte[] bytes)
    {
        var stripped = StripUtf8Bom(bytes);
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return (utf8Strict.GetString(stripped), false);
        }
        catch (DecoderFallbackException)
        {
            return (GetShiftJisReplacement().GetString(bytes), true);
        }
    }

    private static byte[] StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
}
