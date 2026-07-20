using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanoniEditor.Core.Import;

/// <summary>D&Dで渡されたファイルの判定結果種別(TBD#7)</summary>
public enum DroppedFileKind
{
    /// <summary>自形式プロジェクトファイル(.json、schemaVersion/project持ち)</summary>
    OwnProject,
    /// <summary>FUJIエディタファイル(.txt、$frame=を含む)</summary>
    Fuji,
    /// <summary>SKBエディタファイル(.txt/.json、keyKind/scores/timings持ちのJSON)</summary>
    Skb,
    /// <summary>dos.txt(|name=value|形式のパラメータを含む)</summary>
    Dos,
    /// <summary>BASE64エンコードされた楽曲データJS/txt(TBD#6、g_musicdata代入を含む)</summary>
    Base64Music,
    /// <summary>楽曲ファイル本体(mp3/wav/wma/ogg)</summary>
    RawAudio,
    /// <summary>上記いずれにも該当しない</summary>
    Unknown,
}

/// <summary>
/// D&Dされたファイルの種別を中身から判定する(仕様書TBD#7)。
/// 拡張子だけでは判別できない組み合わせがある(FUJI/SKB/dos.txtが.txtを共有、自形式/SKBが.jsonを共有)ため、
/// 中身をスニッフィングして判定する。判定根拠は全て実データ/公式wikiで確認済みの各形式固有マーカー:
/// - 自形式: JSONで"schemaVersion"+"project"キーを持つ(ProjectSerializerの出力形式)
/// - SKB: JSONで"keyKind"+"scores"+"timings"キーを持つ(SkbImporterのSkbFile形状)
/// - FUJI: "$frame="を含む(FujiImporterが必須マーカーとして要求している行)
/// - dos.txt: "|name=value|"形式のパラメータを含む(DosParamParserが読む記法)
/// - BASE64楽曲JS: "g_musicdata=" 代入を含む(danoniplus wiki dos-h0011-musicUrl記載の
///   `function musicInit(){g_musicdata='...'}` 形式。拡張子は.js/.txtいずれもあり得るため中身で判定)
/// - 楽曲ファイル本体: 拡張子(mp3/wav/wma/ogg)で判定(バイナリのため中身は見ない)
/// </summary>
public static partial class DroppedFileClassifier
{
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".wma", ".ogg"];

    [GeneratedRegex(@"g_musicdata\s*=\s*['""]")]
    private static partial Regex MusicDataRegex();

    [GeneratedRegex(@"\|[A-Za-z0-9_]+=")]
    private static partial Regex DosParamRegex();

    public static DroppedFileKind Classify(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (AudioExtensions.Contains(ext)) return DroppedFileKind.RawAudio;

        if (!TryDecodeText(bytes, out var text)) return DroppedFileKind.Unknown;

        if (MusicDataRegex().IsMatch(text)) return DroppedFileKind.Base64Music;

        if (TryClassifyJson(text, out var jsonKind)) return jsonKind;

        if (text.Contains("$frame=")) return DroppedFileKind.Fuji;

        if (DosParamRegex().IsMatch(text)) return DroppedFileKind.Dos;

        return DroppedFileKind.Unknown;
    }

    /// <summary>バイト列を妥当なテキストとしてデコードできるか判定する(音楽ファイル本体等の
    /// バイナリを誤ってテキスト判定しないためのガード)。不正なUTF-8シーケンス、または
    /// 制御文字の混入率が高いものはバイナリ扱いにしてfalseを返す。</summary>
    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0) return false;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(StripBom(bytes));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        int control = text.Count(c => c < 0x20 && c is not ('\t' or '\n' or '\r'));
        return control <= text.Length / 100 + 1;
    }

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;

    private static bool TryClassifyJson(string text, out DroppedFileKind kind)
    {
        kind = DroppedFileKind.Unknown;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return false; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var keys = new HashSet<string>(
                doc.RootElement.EnumerateObject().Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            if (keys.Contains("schemaVersion") && keys.Contains("project"))
            {
                kind = DroppedFileKind.OwnProject;
                return true;
            }
            if (keys.Contains("keyKind") && keys.Contains("scores") && keys.Contains("timings"))
            {
                kind = DroppedFileKind.Skb;
                return true;
            }
            return false; // JSONだが未知の形状
        }
    }
}
