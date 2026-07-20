using System.Text.RegularExpressions;

namespace DanoniEditor.Core.Audio;

/// <summary>
/// BASE64エンコードされた楽曲データJS/txt(仕様書TBD#6、danoniplus wiki dos-h0011-musicUrl)のデコーダ。
/// 対応フォーマット(公式wiki記載・変換ツール(suzme/danoni-base64)のソースで確認済み):
/// <code>function musicInit(){g_musicdata='&lt;base64データ&gt;'}</code>
/// base64はdata URLのプレフィックス(data:audio/mpeg;base64,等)を含まない素のバイト列で、
/// 元のファイル形式(mp3/wav/wma/ogg)を示す情報はJS側に残らないため、デコード後のバイト列を
/// マジックバイトで判定して拡張子を復元する。
/// </summary>
public static partial class Base64MusicDecoder
{
    [GeneratedRegex(@"g_musicdata\s*=\s*'(?<b64>[^']*)'|g_musicdata\s*=\s*""(?<b64>[^""]*)""")]
    private static partial Regex MusicDataRegex();

    /// <summary>g_musicdata='...'の中身(base64文字列)を抜き出す。見つからなければfalse。</summary>
    public static bool TryExtractBase64(string content, out string base64)
    {
        var m = MusicDataRegex().Match(content);
        if (!m.Success) { base64 = ""; return false; }
        base64 = m.Groups["b64"].Value;
        return base64.Length > 0;
    }

    /// <summary>g_musicdataを抜き出してデコードし、音声バイト列を返す。抽出/デコードに失敗すればnull。</summary>
    public static byte[]? DecodeToBytes(string content)
    {
        if (!TryExtractBase64(content, out var base64)) return null;
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
    }

    /// <summary>デコード済みバイト列の先頭マジックバイトから拡張子を推定する(元の形式情報が
    /// JS側に残っていないため)。判定できなければ既定で".mp3"を返す(最も一般的な形式のため)。</summary>
    public static string GuessExtension(byte[] bytes)
    {
        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E')
            return ".wav";

        if (bytes.Length >= 4 && bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S')
            return ".ogg";

        // ASF/WMAコンテナのGUID(30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C)
        ReadOnlySpan<byte> asfGuid = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
        if (bytes.Length >= 16 && bytes.AsSpan(0, 16).SequenceEqual(asfGuid))
            return ".wma";

        if (bytes.Length >= 3 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            return ".mp3"; // ID3タグ付きmp3

        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
            return ".mp3"; // MPEGフレーム同期(IDタグ無しmp3)

        return ".mp3"; // 判定不能時のフォールバック(最も一般的な形式)
    }
}
