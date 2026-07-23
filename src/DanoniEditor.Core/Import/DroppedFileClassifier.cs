using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanoniEditor.Core.Import;

/// <summary>D&Dで渡されたファイルの判定結果種別(TBD#7)</summary>
public enum DroppedFileKind
{
    /// <summary>自形式プロジェクトファイル(.json、schemaVersion/project持ち)</summary>
    OwnProject,
    /// <summary>自形式タブファイル(.json、schemaVersion/project/tabExport持ち、2026-07-23、TBD 5)。
    /// 合作用途でカレント難易度タブ1つだけを書き出したもの。</summary>
    OwnTabExport,
    /// <summary>FUJIエディタファイル(.txt、$セクションキー8種全部揃い。2026-07-26仕様)</summary>
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
/// - 自形式タブファイル: 上記に加え"tabExport"キーを持つ(ProjectSerializer.SerializeTabExportの出力形式、
///   2026-07-23、TBD 5)
/// - SKB: JSONで"keyKind"+"scores"+"timings"キーを持つ(SkbImporterのSkbFile形状)
/// - FUJI: $セクションキーとして version/template/dospath/option/frame/barcut/score/header の
///   8つが全部揃っている(2026-07-26確定仕様。従来の「$frame=を含む」単独チェックから変更。
///   SKBエディタファイルやdos.txtが偶然8つ全部を持つ可能性は理論上あるが、実用上は許容する)
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

        if (IsFuji(text)) return DroppedFileKind.Fuji;

        if (DosParamRegex().IsMatch(text)) return DroppedFileKind.Dos;

        return DroppedFileKind.Unknown;
    }

    /// <summary>FUJIエディタファイル判定(2026-07-26確定仕様)。$で始まる行のセクションキー
    /// ($key=value形式は=より前、$key単独行はキー全体)を集め、FUJI形式が必ず持つ8キー
    /// (version/template/dospath/option/frame/barcut/score/header)が全部揃っていればFUJIとみなす。
    /// キーの切り出し方はFujiImporterのセクション分解と同一ロジック。</summary>
    private static bool IsFuji(string text)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith('$')) continue;
            var eq = line.IndexOf('=');
            keys.Add(eq > 0 ? line[1..eq] : line[1..]);
        }
        string[] required = ["version", "template", "dospath", "option", "frame", "barcut", "score", "header"];
        return required.All(keys.Contains);
    }

    /// <summary>バイト列を妥当なテキストとしてデコードできるか判定する(音楽ファイル本体等の
    /// バイナリを誤ってテキスト判定しないためのガード)。
    /// 2026-07-26: 従来は不正なUTF-8シーケンスで即バイナリ扱いにしていたが、FUJIエディタ等の
    /// Shift-JIS保存ファイルが全滅する(D&DのFUJI識別が100%失敗していた根本原因)ため、
    /// 置換文字(U+FFFD)フォールバック付きで寛容にデコードする。判定に使うマーカー類はすべて
    /// ASCIIなので、日本語部分が化けても識別には影響しない。バイナリ除外は
    /// 「制御文字+置換文字の混入率」で行う(バイナリはこれらが大量に発生する)。</summary>
    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0) return false;

        text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(StripBom(bytes));
        if (text.Length == 0) return false;

        int suspicious = text.Count(c => c == '�' || (c < 0x20 && c is not ('\t' or '\n' or '\r')));
        // Shift-JISの日本語はUTF-8として読むと2バイト中1〜2文字が置換文字になりうるため、
        // 閾値は緩め(30%)にする。真のバイナリ(音声等)は50%超になるのが通例。
        return suspicious <= text.Length * 3 / 10 + 1;
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

            // 2026-07-23: tabExportキーを持つ場合はタブ単体エクスポート(OwnTabExport)。
            // 通常のOwnProject判定より先にチェックする(両方とも"schemaVersion"+"project"を持つため)。
            if (keys.Contains("schemaVersion") && keys.Contains("project") && keys.Contains("tabExport"))
            {
                kind = DroppedFileKind.OwnTabExport;
                return true;
            }
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
