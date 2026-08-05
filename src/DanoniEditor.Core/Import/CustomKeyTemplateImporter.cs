using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Import;

/// <summary>取り込み時に確定できず仮の値で埋めた項目の位置情報(2026-08-03、テンプレートエディタでの
/// ハイライト表示・保存時の入力必須チェックに使う)。
///
/// ハイライト(および保存時ブロック)の対象は「プレイテスト・プレビューの画面構成に直接関わる項目」に
/// 限定する(2026-08-03要望対応)。具体的には、レーン単位でcolorGroup/posIndex/scrollDirection/
/// noteGraphic/rotationAngle、パターン単位でblank/divideCnt/posMaxの計8種。
/// keyAssign・dataName・keyboardInputKeys・engineLaneNum等(画面構成に直接関わらない、または
/// キーボードモードのノート入力にのみ使う項目)は対象外とし、従来通り警告メッセージのみで
/// 保存をブロックしない。
///
/// LaneIndexはKeyTemplate.Lanes配列の並び順(=displayOrder順)を指す。PatternIndex=0は既定パターン、
/// 1以降はExtraPatterns[PatternIndex-1]に対応する。</summary>
public sealed class ImportFieldStatus
{
    /// <summary>未確定のレーン単位項目。要素は(PatternIndex, LaneIndex, FieldName)。
    /// FieldNameは"colorGroup"/"posIndex"/"scrollDirection"/"noteGraphic"/"rotationAngle"のいずれか。</summary>
    public HashSet<(int PatternIndex, int LaneIndex, string FieldName)> UnresolvedLaneFields { get; } = [];

    /// <summary>未確定のパターン単位項目。要素は(PatternIndex, FieldName)。
    /// FieldNameは"blank"/"divideCnt"/"posMaxのいずれか。</summary>
    public HashSet<(int PatternIndex, string FieldName)> UnresolvedPatternFields { get; } = [];
}

/// <summary>カスタムキー定義インポートの結果</summary>
public sealed class CustomKeyImportResult
{
    public required KeyTemplate Template { get; init; }
    public required List<string> Warnings { get; init; }
    public required ImportFieldStatus FieldStatus { get; init; }
}

/// <summary>
/// danoniplus本体互換の「カスタムキー」定義テキスト(|keyCtrlX=...|等のパイプ区切りヘッダー行群)を、
/// エディタのKeyTemplateへ取り込む(2026-08-03要望対応、CustomKeyTemplateExporterの逆方向)。
///
/// エクスポート側と対称の変換を行うが、カスタムキー定義のテキスト側には元々存在しない情報
/// (laneId・keyboardInputKeys・engineLaneNum・frzDataNameOverride)があるため、それらは
/// レーンの並び順から機械的に仮生成する(呼び出し元でテンプレートエディタを開いて確認・修正する
/// 運用を前提とする、警告メッセージで明示する)。
///
/// "@"/"["/"]"/"`"はJIS配列/US配列で対応する本体コードが食い違うため、エクスポート時と同様
/// 呼び出し元が明示的にKeyboardLayoutを指定する必要がある(テキスト単体からは判別不能)。
///
/// 本体側には「他キー種/他パターンの値を丸ごと再利用する」略記(`(キー数)_(パターン番号-1)`形式、
/// 例:"5_0"、"5g_0")がある(2026-08-03、実例調査で判明)。標準キー種のテンプレート(temp_5.json等)
/// はエディタ側に揃っているため、<paramref name="templateResolver"/>を渡せば、略記の参照先を
/// そのテンプレートから解決して取り込める。
///
/// 2026-08-03要望対応: 「読み取りが不十分な場合はエラーでストップさせず、取得できる部分だけ取得した上で
/// 取得できなかった部分をテンプレートエディタ上でハイライトする」方針に変更した。取り込み自体を
/// 中止するのは、以下の「本当に復元不能」なケースのみに限定する。
/// - カスタムキー定義のヘッダー行が1つも認識できない
/// - keyCtrl{id}が完全に見つからない(全ての項目の前提となるレーン数が一切判定できない)
/// - keyCtrl{id}のパターン0(既定パターン)が略記であり、かつ解決できない(レーン数の基準が無い)
/// それ以外(必須項目の欠落・略記の解決失敗・値の解釈失敗)は、仮の値で埋めた上で
/// <see cref="CustomKeyImportResult.FieldStatus"/>に位置を記録し、処理を継続する。
/// </summary>
public static class CustomKeyTemplateImporter
{
    private static readonly string[] KnownBaseNames =
        ["keyHelpJa", "keyHelpEn", "keyCtrl", "stepRtn", "divMax", "keyName", "color", "scroll", "chara", "blank", "pos", "div"];

    private static readonly Regex HeaderLine = new(@"^\s*\|([A-Za-z][A-Za-z0-9]*)=(.*)\|\s*$", RegexOptions.Compiled);

    /// <summary>「(キー数等)_(パターン番号-1)」形式の略記(本体側の「他キー種/パターンの値を丸ごと
    /// 再利用する」記法)。例: "5_0"(キー種"5"のパターン0を丸ごと再利用)。</summary>
    private static readonly Regex ShorthandPattern = new(@"^([0-9A-Za-z]+)_([0-9]+)$", RegexOptions.Compiled);

    private delegate bool TryParseToken<T>(string token, out T value);

    /// <summary>keyTypeIdを省略した場合、テキスト中で最も多くの既知ヘッダーに使われているIDを自動検出する。
    /// <paramref name="templateResolver"/>を渡すと、本体側の「他キー種/パターンの値を丸ごと再利用する」
    /// 略記("5_0"等)を、そのキー種のエディタ用テンプレート(TemplateRepository経由)から解決できる。</summary>
    public static CustomKeyImportResult Import(string text, KeyboardLayout layout, string? keyTypeId = null, Func<string, KeyTemplate>? templateResolver = null)
    {
        var warnings = new List<string>();
        var status = new ImportFieldStatus();
        var fields = new Dictionary<string, string>(); // baseName -> value (最後に出現した行を採用)
        var idVotes = new Dictionary<string, int>();

        foreach (var rawLine in text.Split('\n'))
        {
            var m = HeaderLine.Match(rawLine.TrimEnd('\r'));
            if (!m.Success) continue;
            var paramName = m.Groups[1].Value;

            var baseName = KnownBaseNames.FirstOrDefault(b => paramName.StartsWith(b, StringComparison.Ordinal));
            if (baseName is null) continue;
            var suffix = paramName[baseName.Length..];
            if (suffix.Length == 0) continue; // 見出しにID部分が無い(想定外の書式)

            idVotes[suffix] = idVotes.GetValueOrDefault(suffix) + 1;
        }

        if (idVotes.Count == 0)
            throw new InvalidDataException("カスタムキー定義のヘッダー行(|keyCtrlX=...|等)が見つかりませんでしたの。");

        var id = keyTypeId ?? idVotes.OrderByDescending(kv => kv.Value).First().Key;
        if (idVotes.Count > 1)
            warnings.Add($"複数のkeyTypeId({string.Join("/", idVotes.Keys)})が混在するテキストでしたの。" +
                         $"「{id}」向けのヘッダーのみを取り込みますわ。");

        foreach (var rawLine in text.Split('\n'))
        {
            var m = HeaderLine.Match(rawLine.TrimEnd('\r'));
            if (!m.Success) continue;
            var paramName = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            if (!paramName.EndsWith(id, StringComparison.Ordinal)) continue;
            var baseName = KnownBaseNames.FirstOrDefault(b => paramName == b + id);
            if (baseName is null) continue;
            fields[baseName] = value;
        }

        if (!fields.TryGetValue("keyCtrl", out var keyCtrlRaw))
            throw new InvalidDataException($"keyCtrl{id}が見つからないため取り込めませんでしたの(必須項目)。" +
                "レーン数の基準となる項目のため、これだけは本インポート機能では復元できませんの。");

        var reverseMap = BuildReverseKeyNameMap(layout);

        // --- 略記("5_0"等)解決ヘルパー(非致命: 解決できない場合は警告のみでfalseを返す) ---
        bool TryResolveShorthand(string fieldBaseName, string segment, int patternIndexInThis, Match match, out KeyTemplate? refPattern)
        {
            refPattern = null;
            if (templateResolver is null)
            {
                warnings.Add($"{fieldBaseName}{id}(パターン{patternIndexInThis})に、他のキー種/パターンのデータを再利用する" +
                    $"略記(\"{segment}\")が使われていますが、参照解決用のテンプレート集合が渡されていないため解決できませんでしたの。" +
                    "仮の値で埋めましたので、テンプレートエディタで確認・入力してくださいまし。");
                return false;
            }
            var refId = match.Groups[1].Value;
            var refPatternIndex = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            KeyTemplate refBase;
            try { refBase = templateResolver(refId); }
            catch (Exception ex)
            {
                warnings.Add($"{fieldBaseName}{id}(パターン{patternIndexInThis})の略記(\"{segment}\")が参照するキー種" +
                    $"「{refId}」のテンプレートが見つかりませんでしたの: {ex.Message}。" +
                    "仮の値で埋めましたので、テンプレートエディタで確認・入力してくださいまし。");
                return false;
            }
            if (refPatternIndex > 0 && refPatternIndex > refBase.ExtraPatterns.Count)
            {
                warnings.Add($"{fieldBaseName}{id}(パターン{patternIndexInThis})の略記(\"{segment}\")が参照するパターン" +
                    $"{refPatternIndex}は、キー種「{refId}」に存在しませんでしたの。" +
                    "仮の値で埋めましたので、テンプレートエディタで確認・入力してくださいまし。");
                return false;
            }
            warnings.Add($"{fieldBaseName}{id}(パターン{patternIndexInThis})は略記(\"{segment}\")のため、" +
                         $"テンプレート「{refId}」パターン{refPatternIndex}のデータから復元しましたの。" +
                         "参照先テンプレートが本体の実際の標準データと食い違っていないか、念のためご確認くださいまし。");
            refPattern = refBase.WithPattern(refPatternIndex);
            return true;
        }

        string[] SplitPatterns(string baseName, string fallback)
        {
            var raw = fields.GetValueOrDefault(baseName, fallback);
            return raw.Split('$');
        }

        var keyCtrlSegments = SplitPatterns("keyCtrl", "");
        int patternCount = keyCtrlSegments.Length;

        // --- keyCtrl(レーン数の基準)。パターン0(既定)が略記で解決不能な場合のみ、レーン数の
        // 基準そのものが無くなるため例外にする。パターン1以降が解決不能な場合は、その
        // パターンだけプレースホルダのkeyAssign(["?"])で埋めて処理を継続する(keyAssignは
        // 画面構成に直接関わらないためFieldStatusの対象外、警告のみで保存もブロックしない)。
        var keyCtrlByPattern = new List<List<IReadOnlyList<string>>>();
        for (int p = 0; p < patternCount; p++)
        {
            var seg = keyCtrlSegments[p];
            var shMatch = ShorthandPattern.Match(seg);
            if (shMatch.Success)
            {
                if (TryResolveShorthand("keyCtrl", seg, p, shMatch, out var refPattern))
                {
                    keyCtrlByPattern.Add(refPattern!.Lanes.Select(l => l.KeyAssign).ToList());
                    continue;
                }
                if (p == 0)
                    throw new InvalidDataException($"keyCtrl{id}(パターン0)が略記(\"{seg}\")で、かつ解決できませんでしたの。" +
                        "レーン数の基準となる項目のため、これだけは本インポート機能では復元できませんの。");
                // レーン数はパターン0から既に確定しているので、プレースホルダで埋めて続行する。
                int fallbackLaneCount = keyCtrlByPattern[0].Count;
                keyCtrlByPattern.Add([.. Enumerable.Repeat((IReadOnlyList<string>)new List<string> { "?" }, fallbackLaneCount)]);
                continue;
            }
            keyCtrlByPattern.Add([.. seg.Split(',').Select(tok =>
                (IReadOnlyList<string>)tok.Split('/').Select(k => ReverseKeyName(k.Trim(), reverseMap)).ToList())]);
        }
        int laneCount = keyCtrlByPattern[0].Count;

        // --- レーン単位の項目を、必須ヘッダーの有無・略記解決・個別トークンの解釈失敗を通して
        // 「取得できる部分だけ取得し、取得できなかった部分をFieldStatusへ記録する」形で解決する。
        List<T> ResolvePerLane<T>(string baseName, int p, string fieldStatusName, Func<LaneDef, T> fromReference,
            TryParseToken<T> tryParseToken, T defaultValue)
        {
            void MarkAllUnresolved()
            {
                for (int lane = 0; lane < laneCount; lane++) status.UnresolvedLaneFields.Add((p, lane, fieldStatusName));
            }

            if (!fields.ContainsKey(baseName))
            {
                warnings.Add($"{baseName}{id}が見つからなかったため、{fieldStatusName}を仮の値で埋めましたの。" +
                             "テンプレートエディタで確認・入力してくださいまし。");
                MarkAllUnresolved();
                return [.. Enumerable.Repeat(defaultValue, laneCount)];
            }

            var segments = SplitPatterns(baseName, "");
            var raw = p < segments.Length ? segments[p] : (segments.Length > 0 ? segments[0] : "");
            if (p >= segments.Length)
                warnings.Add($"{baseName}{id}のパターン数({segments.Length})がkeyCtrl({patternCount})と一致しませんの。" +
                             "不足分はパターン0の値で埋めますわ。");

            var shMatch = ShorthandPattern.Match(raw);
            if (shMatch.Success)
            {
                if (TryResolveShorthand(baseName, raw, p, shMatch, out var refPattern))
                {
                    var result = refPattern!.Lanes.Select(fromReference).ToList();
                    if (result.Count != laneCount)
                        warnings.Add($"{baseName}{id}(パターン{p})の略記参照先のレーン数({result.Count})がkeyCtrl({laneCount})と" +
                                     "一致しませんの。取り込み結果を必ずご確認くださいまし。");
                    return result;
                }
                MarkAllUnresolved();
                return [.. Enumerable.Repeat(defaultValue, laneCount)];
            }

            var tokens = raw.Split(',');
            if (tokens.Length != laneCount)
                warnings.Add($"{baseName}{id}(パターン{p})のレーン数({tokens.Length})がkeyCtrl({laneCount})と" +
                             "一致しませんの。取り込み結果を必ずご確認くださいまし。");
            var outList = new List<T>();
            for (int lane = 0; lane < laneCount; lane++)
            {
                if (lane < tokens.Length && tryParseToken(tokens[lane], out var v)) { outList.Add(v); continue; }
                status.UnresolvedLaneFields.Add((p, lane, fieldStatusName));
                outList.Add(defaultValue);
            }
            return outList;
        }

        bool TryParseColorToken(string t, out int value) =>
            int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        bool TryParsePosToken(string t, out double value) =>
            double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // stepRtn(noteGraphic+rotationAngleの複合項目)とscroll(scrollDirection)は、汎用の
        // ResolvePerLaneでは表現しづらい個別ルールがあるため専用の解決関数を用意する。

        List<(string Graphic, double Angle)> ResolveStepRtnList(int p)
        {
            void MarkUnresolved(int lane)
            {
                status.UnresolvedLaneFields.Add((p, lane, "noteGraphic"));
                status.UnresolvedLaneFields.Add((p, lane, "rotationAngle"));
            }

            if (!fields.ContainsKey("stepRtn"))
            {
                warnings.Add($"stepRtn{id}(パターン{p})が見つからなかったため、noteGraphic/rotationAngleを仮の値で" +
                             "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
                for (int lane = 0; lane < laneCount; lane++) MarkUnresolved(lane);
                return [.. Enumerable.Repeat(("arrow", 0.0), laneCount)];
            }

            var segments = SplitPatterns("stepRtn", "");
            var raw = p < segments.Length ? segments[p] : (segments.Length > 0 ? segments[0] : "");
            if (p >= segments.Length)
                warnings.Add($"stepRtn{id}のパターン数({segments.Length})がkeyCtrl({patternCount})と一致しませんの。" +
                             "不足分はパターン0の値で埋めますわ。");

            var shMatch = ShorthandPattern.Match(raw);
            if (shMatch.Success)
            {
                if (TryResolveShorthand("stepRtn", raw, p, shMatch, out var refPattern))
                {
                    var result = refPattern!.Lanes.Select(l => (l.NoteGraphic, l.RotationAngle)).ToList();
                    if (result.Count != laneCount)
                        warnings.Add($"stepRtn{id}(パターン{p})の略記参照先のレーン数({result.Count})がkeyCtrl({laneCount})と" +
                                     "一致しませんの。取り込み結果を必ずご確認くださいまし。");
                    return result;
                }
                for (int lane = 0; lane < laneCount; lane++) MarkUnresolved(lane);
                return [.. Enumerable.Repeat(("arrow", 0.0), laneCount)];
            }

            var tokens = raw.Split(',');
            if (tokens.Length != laneCount)
                warnings.Add($"stepRtn{id}(パターン{p})のレーン数({tokens.Length})がkeyCtrl({laneCount})と" +
                             "一致しませんの。取り込み結果を必ずご確認くださいまし。");
            var outList = new List<(string, double)>();
            for (int lane = 0; lane < laneCount; lane++)
            {
                var t = lane < tokens.Length ? tokens[lane].Trim() : "";
                if (t.Length > 0)
                {
                    outList.Add(double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle)
                        ? ("arrow", angle) : (t, 0.0));
                    continue;
                }
                MarkUnresolved(lane);
                outList.Add(("arrow", 0.0));
            }
            return outList;
        }

        List<string> ResolveScrollList(int p)
        {
            if (!fields.ContainsKey("scroll"))
            {
                warnings.Add($"scroll{id}(パターン{p})が見つからなかったため、scrollDirectionを仮の値(down)で" +
                             "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
                for (int lane = 0; lane < laneCount; lane++) status.UnresolvedLaneFields.Add((p, lane, "scrollDirection"));
                return [.. Enumerable.Repeat("down", laneCount)];
            }

            var segments = SplitPatterns("scroll", "");
            var raw = p < segments.Length ? segments[p] : (segments.Length > 0 ? segments[0] : "");
            var shMatch = ShorthandPattern.Match(raw);
            if (shMatch.Success)
            {
                if (TryResolveShorthand("scroll", raw, p, shMatch, out var refPattern))
                    return [.. refPattern!.Lanes.Select(l => l.ScrollDirection)];
                for (int lane = 0; lane < laneCount; lane++) status.UnresolvedLaneFields.Add((p, lane, "scrollDirection"));
                return [.. Enumerable.Repeat("down", laneCount)];
            }

            var firstGroup = raw.Split('/')[0]; // 名前付き複数パターン("/"区切り)は最初の1つだけ取り込む
            if (raw.Contains('/'))
                warnings.Add($"scroll{id}(パターン{p})に複数の名前付きスクロールパターンが含まれていましたが、" +
                             "エディタには切替の概念が無いため最初の1つのみ取り込みましたの。");
            var afterName = firstGroup.Contains("::") ? firstGroup[(firstGroup.IndexOf("::", StringComparison.Ordinal) + 2)..] : firstGroup;
            if (!firstGroup.Contains("::"))
                warnings.Add($"scroll{id}(パターン{p})に本体仕様上必須の\"名前::\"が付いていませんでしたが、" +
                             "値としてそのまま取り込みますわ。");

            var result = new List<string>();
            int laneIdx = 0;
            foreach (var t in afterName.Split(','))
            {
                if (t.Trim() == "1") result.Add("down");
                else if (t.Trim() == "-1") result.Add("up");
                else
                {
                    warnings.Add($"scroll{id}(パターン{p})のレーン{laneIdx + 1}に1/-1以外の値({t})がありましたの。" +
                                 "仮の値(down)で埋めましたので、テンプレートエディタで確認・入力してくださいまし。");
                    status.UnresolvedLaneFields.Add((p, laneIdx, "scrollDirection"));
                    result.Add("down");
                }
                laneIdx++;
            }
            while (result.Count < laneCount)
            {
                status.UnresolvedLaneFields.Add((p, result.Count, "scrollDirection"));
                result.Add("down");
            }
            return result;
        }

        (double DivideCnt, double PosMax) ResolveDiv(int p)
        {
            void MarkBothUnresolved()
            {
                status.UnresolvedPatternFields.Add((p, "divideCnt"));
                status.UnresolvedPatternFields.Add((p, "posMax"));
            }

            if (!fields.ContainsKey("div"))
            {
                warnings.Add($"div{id}(パターン{p})が見つからなかったため、divideCnt/posMaxを仮の値(0)で" +
                             "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
                MarkBothUnresolved();
                return (0, 0);
            }

            var segments = SplitPatterns("div", "");
            var raw = p < segments.Length ? segments[p] : segments[0];
            var shMatch = ShorthandPattern.Match(raw);
            if (shMatch.Success)
            {
                if (TryResolveShorthand("div", raw, p, shMatch, out var refPattern))
                    return (refPattern!.DivideCnt, refPattern.PosMax);
                MarkBothUnresolved();
                return (0, 0);
            }

            var commaIdx = raw.IndexOf(',');
            if (commaIdx >= 0)
            {
                var okDiv = double.TryParse(raw[..commaIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var divRaw);
                var okDivMax = double.TryParse(raw[(commaIdx + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var divMaxRaw2);
                if (okDiv && okDivMax) return (divRaw - 1, divMaxRaw2);
                warnings.Add($"div{id}(パターン{p})の値(\"{raw}\")を解釈できなかったため、divideCnt/posMaxを仮の値(0)で" +
                             "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
                MarkBothUnresolved();
                return (0, 0);
            }

            // カンマ無し(本エクスポータの現行仕様では常にカンマ組だが、手編集データ等の
            // 旧形式・簡略形式向けフォールバック): divMaxXの独立ヘッダーがあればそちらを使う。
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var div))
            {
                warnings.Add($"div{id}(パターン{p})の値(\"{raw}\")を解釈できなかったため、divideCnt/posMaxを仮の値(0)で" +
                             "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
                MarkBothUnresolved();
                return (0, 0);
            }
            var divMaxSegments = fields.TryGetValue("divMax", out var divMaxRaw) ? divMaxRaw.Split('$') : [];
            if (p < divMaxSegments.Length && double.TryParse(divMaxSegments[p], NumberStyles.Float, CultureInfo.InvariantCulture, out var dm))
                return (div - 1, dm);
            warnings.Add($"div{id}(パターン{p})にdivMaxに相当する値が見つからなかったため、posMaxを仮の値(divideCntと同じ)で" +
                         "埋めましたの。テンプレートエディタで確認・入力してくださいまし。");
            status.UnresolvedPatternFields.Add((p, "posMax"));
            return (div - 1, div);
        }

        double ResolveBlank(int p)
        {
            if (!fields.ContainsKey("blank"))
            {
                warnings.Add($"blank{id}(パターン{p})が見つからなかったため、仮の値(0)で埋めましたの。" +
                             "テンプレートエディタで確認・入力してくださいまし。");
                status.UnresolvedPatternFields.Add((p, "blank"));
                return 0;
            }

            var segments = SplitPatterns("blank", "0");
            var raw = p < segments.Length ? segments[p] : segments[0];
            var shMatch = ShorthandPattern.Match(raw);
            if (shMatch.Success)
            {
                if (TryResolveShorthand("blank", raw, p, shMatch, out var refPattern)) return refPattern!.Blank;
                status.UnresolvedPatternFields.Add((p, "blank"));
                return 0;
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            warnings.Add($"blank{id}(パターン{p})の値(\"{raw}\")を解釈できなかったため、仮の値(0)で埋めましたの。" +
                         "テンプレートエディタで確認・入力してくださいまし。");
            status.UnresolvedPatternFields.Add((p, "blank"));
            return 0;
        }

        // --- レーンの素性(laneId/dataName/displayOrder/engineLaneNum)は全パターン共通、chara(パターン0のみ参照) ---
        // 2026-08-03: これらは画面構成に直接関わらない項目のため、FieldStatusの対象外(警告のみ)。
        var laneIds = new List<string>();
        List<string>? charaNames = null;
        if (fields.TryGetValue("chara", out var charaRaw))
        {
            var seg0 = charaRaw.Split('$')[0];
            var shMatch = ShorthandPattern.Match(seg0);
            charaNames = shMatch.Success && TryResolveShorthand("chara", seg0, 0, shMatch, out var refPattern)
                ? [.. refPattern!.Lanes.Select(l => l.DataName)]
                : shMatch.Success ? null : [.. seg0.Split(',')];
        }
        var dataNames = new List<string>();
        for (int lane = 0; lane < laneCount; lane++)
        {
            laneIds.Add($"lane{lane + 1}");
            dataNames.Add(charaNames is not null && lane < charaNames.Count ? charaNames[lane] : $"lane{lane + 1}");
        }
        if (charaNames is null)
            warnings.Add($"chara{id}が見つからなかったため、読み込み変数名を仮の値(lane1, lane2, ...)で埋めましたの。" +
                         "dos.txt側の実際のデータ名と一致するよう、取り込み後に確認・修正してくださいまし。");
        warnings.Add("laneId・engineLaneNum・keyboardInputKeysはカスタムキー定義に含まれない情報のため、" +
                     "レーンの並び順から仮に生成していますの。engineLaneNumは特にncolor_data等の対応関係に" +
                     "影響するため、取り込み後にテンプレートエディタ上で確認・修正してくださいまし。");

        // --- パターンごとのレーンデータを組み立てる ---
        var patternLaneData = new List<List<(IReadOnlyList<string> KeyAssign, int ColorGroup, double PosIndex, string ScrollDirection, string NoteGraphic, double RotationAngle)>>();
        var patternNums = new List<(double Blank, double DivideCnt, double PosMax)>();
        (double Blank, double DivideCnt, double PosMax) baseNums = default;

        for (int p = 0; p < patternCount; p++)
        {
            var keyCtrlLanes = keyCtrlByPattern[p];
            var colorList = ResolvePerLane("color", p, "colorGroup", l => l.ColorGroup, TryParseColorToken, 0);
            var posList = ResolvePerLane("pos", p, "posIndex", l => l.PosIndex, TryParsePosToken, 0.0);
            var stepRtnList = ResolveStepRtnList(p);
            var scrollList = ResolveScrollList(p);
            var (divideCnt, posMax) = ResolveDiv(p);
            var blank = ResolveBlank(p);

            if (keyCtrlLanes.Count != laneCount)
                warnings.Add($"パターン{p}のkeyCtrlレーン数({keyCtrlLanes.Count})が基準({laneCount})と一致しませんの。" +
                             "取り込み結果を必ずご確認くださいまし。");

            var lanes = new List<(IReadOnlyList<string>, int, double, string, string, double)>();
            for (int lane = 0; lane < laneCount; lane++)
            {
                var keyAssign = lane < keyCtrlLanes.Count ? keyCtrlLanes[lane] : ["?"];
                var color = lane < colorList.Count ? colorList[lane] : 0;
                var pos = lane < posList.Count ? posList[lane] : lane;
                var scroll = lane < scrollList.Count ? scrollList[lane] : "down";
                var (graphic, angle) = lane < stepRtnList.Count ? stepRtnList[lane] : ("arrow", 0);
                lanes.Add((keyAssign, color, pos, scroll, graphic, angle));
            }
            patternLaneData.Add(lanes);
            patternNums.Add((blank, divideCnt, posMax));
            if (p == 0) baseNums = (blank, divideCnt, posMax);
        }

        var baseLanes = new List<LaneDef>();
        for (int lane = 0; lane < laneCount; lane++)
        {
            var l0 = patternLaneData[0][lane];
            baseLanes.Add(new LaneDef
            {
                LaneId = laneIds[lane],
                DataName = dataNames[lane],
                DisplayOrder = lane,
                KeyAssign = l0.KeyAssign,
                ColorGroup = l0.ColorGroup,
                PosIndex = l0.PosIndex,
                ScrollDirection = l0.ScrollDirection,
                NoteGraphic = l0.NoteGraphic,
                RotationAngle = l0.RotationAngle,
                EngineLaneNum = lane,
            });
        }

        var extraPatterns = new List<KeyPattern>();
        for (int p = 1; p < patternCount; p++)
        {
            var (blankP, divideCntP, posMaxP) = patternNums[p];
            var overrides = new List<LanePatternOverride>();
            for (int lane = 0; lane < laneCount; lane++)
            {
                var d = patternLaneData[p][lane];
                overrides.Add(new LanePatternOverride
                {
                    KeyAssign = d.KeyAssign,
                    ColorGroup = d.ColorGroup,
                    PosIndex = d.PosIndex,
                    ScrollDirection = d.ScrollDirection,
                    NoteGraphic = d.NoteGraphic,
                    RotationAngle = d.RotationAngle,
                });
            }
            extraPatterns.Add(new KeyPattern
            {
                Blank = blankP,
                DivideCnt = divideCntP,
                PosMax = posMaxP,
                LaneOverrides = overrides,
            });
        }

        var keyTypeName = fields.GetValueOrDefault("keyName", id);
        if (!fields.ContainsKey("keyName"))
            warnings.Add($"keyName{id}が見つからなかったため、keyTypeNameを仮に「{id}」としましたの。");

        var template = new KeyTemplate
        {
            KeyTypeId = id,
            KeyTypeName = keyTypeName,
            KeyCount = laneCount,
            Blank = baseNums.Blank,
            DivideCnt = baseNums.DivideCnt,
            PosMax = baseNums.PosMax,
            Lanes = baseLanes,
            ExtraPatterns = extraPatterns,
            KeyboardLayout = layout,
        };

        return new CustomKeyImportResult { Template = template, Warnings = warnings, FieldStatus = status };
    }

    /// <summary>CustomKeyTemplateExporterのEngineKeyNames/LayoutSpecificEngineKeyNamesと対称の逆引き表を
    /// 構築する。レイアウト固有の変換(下記layout引数)が、共通表より優先される(エクスポート時の
    /// 優先順序と対称)。</summary>
    private static Dictionary<string, string> BuildReverseKeyNameMap(KeyboardLayout layout)
    {
        var map = new Dictionary<string, string>();
        var common = new Dictionary<string, string>
        {
            ["←"] = "Left", ["↓"] = "Down", ["↑"] = "Up", ["→"] = "Right",
            ["Num0"] = "Numpad0", ["Num1"] = "Numpad1", ["Num2"] = "Numpad2", ["Num3"] = "Numpad3",
            ["Num4"] = "Numpad4", ["Num5"] = "Numpad5", ["Num6"] = "Numpad6", ["Num7"] = "Numpad7",
            ["Num8"] = "Numpad8", ["Num9"] = "Numpad9",
            ["Num+"] = "NumpadAdd", ["Num-"] = "NumpadSubtract", ["Num*"] = "NumpadMultiply",
            ["Num/"] = "NumpadDivide", ["Num."] = "NumpadDecimal",
            ["<"] = "Comma", [">"] = "Period", [";"] = "Semicolon", [":"] = "Quote",
            ["@"] = "BracketLeft", ["-"] = "Minus", ["="] = "Equal", ["/"] = "Slash", ["\\"] = "Backslash",
            ["Esc"] = "Escape", ["Ctrl"] = "Control",
        };
        var layoutSpecific = layout == KeyboardLayout.Jis
            ? new Dictionary<string, string> { ["["] = "BracketRight", ["]"] = "Backslash" }
            : new Dictionary<string, string> { ["["] = "BracketLeft", ["]"] = "BracketRight", ["`"] = "Backquote" };

        foreach (var kv in common) map[kv.Value] = kv.Key;
        foreach (var kv in layoutSpecific) map[kv.Value] = kv.Key; // レイアウト固有側が優先(エクスポート側と対称)
        return map;
    }

    private static string ReverseKeyName(string engineName, Dictionary<string, string> reverseMap) =>
        reverseMap.TryGetValue(engineName, out var label) ? label : engineName;
}
