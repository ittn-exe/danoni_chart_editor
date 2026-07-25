using System.Globalization;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Import;

/// <summary>difData候補1件分(2026-07-16e: 同一キー種のdifData行が複数ある場合の選択用)</summary>
public sealed record DifDataCandidate(string KeyTypeId, string DifficultyName, double? InitialSpeed)
{
    /// <summary>ドロップダウン表示用ラベル(「(キー種) - (難易度名)」形式、ユーザー指定フォーマット)</summary>
    public string Label => $"{KeyTypeId} - {DifficultyName}";
}

/// <summary>FUJIエディタ保存ファイル(.txt)のインポート結果</summary>
public sealed class FujiImportResult
{
    public required DifficultyTab Tab { get; init; }
    /// <summary>ファイルのヘッダー記述部(|blankFrame=...|)から取得したblankFrame(2026-07-17)。
    /// 従来はここに$frame由来の値(StartNumberと同一視した値)を入れていたが、
    /// 「firstNumber − blankFrame = StartNumber」という関係式に合わせて分離した。</summary>
    public required double BlankFrame { get; init; }
    /// <summary>tick0の絶対フレーム位置。firstNumber(=$frame先頭セグメントの開始フレーム)そのまま
    /// (2026-07-19c: blankFrameは織り込まない方針に変更)。</summary>
    public required double StartNumber { get; init; }
    public required double MeasureLength { get; init; }       // mlen(先頭セグメントの4/4小節あたりフレーム数、後方互換)
    public required List<BpmEvent> BpmEvents { get; init; }
    public required List<TimeSignatureEvent> TimeSignatures { get; init; }
    public required Dictionary<string, string> HeaderParams { get; init; }
    public required List<string> Warnings { get; init; }

    /// <summary>
    /// difDataに同一キー種の行が複数あった場合の候補一覧(2026-07-16e)。1件以下なら空リスト
    /// (0件=候補なし、1件=自動確定済みでImport側がTabへ反映済み)。呼び出し側(UI)はこれが2件以上の
    /// 場合のみ選択ダイアログを出し、選んだ候補のDifficultyName/InitialSpeedをTabへ反映すること
    /// (Import時点では暫定的に先頭候補を適用済みなので、選ばなければそのまま先頭候補が使われる)。
    /// </summary>
    public required IReadOnlyList<DifDataCandidate> DifDataCandidates { get; init; }

    /// <summary>この結果のタイミングでTimingEngineを作る</summary>
    public TimingEngine CreateTimingEngine() => new(StartNumber, BpmEvents, TimeSignatures);
}

/// <summary>
/// FUJIエディタ形式のインポーター(仕様書15.3)。実データ(a.txt/a_dos.txt, by_node,
/// 2026-07-25の1224frztest.txt/1224frztest_dos.txt、2026-07-26のtest23key_v2.txt/
/// test23key_v2_dos.txt全数比較[measure0〜10、note23点・freeze24ペア全一致])で検証した解釈:
/// - $frame=A/B/C/D,E: blank=B/10, 総フレーム=C/10, E=小節数。ヘッダーのblankFrameより$frameのBが優先。
/// - mlen = (C/10 − blank) / (E − Σskip/16)   ※skipは$barcutの16分単位カット量
/// - 小節行 "MMMM:tok,tok,..." のトークン:
///   - 4hex "PPDF": 通常ノート。PP=2桁16進(下位ニブルはレーン拡張ビット+16、2026-07-18c)、
///     D=レーン数字(1桁16進)、F=fine文字(位置微調整、下記参照)。frame = cum(M) + PP/256 × mlen
///     をF='0'なら無補正、それ以外はfine文字の規則で補正する。
///   - "X8DF-DDDE": フリーズ。X=1/16スロット(始点)、D=レーン数字、F=始点のfine文字。
///     フラグ'8'/'9'(2文字目)はレーン+16拡張(実データの'0920-0120'等から確定)。
///     終点側のDDD(3桁)は常に10進数値(公式文書のD欄説明どおり)、Eは始点と同じfine文字体系。
///     終点pp=始点pp+DDD×16(始点からの長さとして加算)、終点は
///     cum(M)+ResolveFineFrame(pp,終点fine文字)(始点のfine補正の影響を受けない)。
///     2026-07-26訂正: 旧実装は「QQQQ全体が16進として解釈できればhexのdurとして使い、
///     0x100以上は−0x60補正」という独自ルールを使っており、これは誤りだった(末尾1文字が
///     たまたま0〜9/A〜Fの場合にhex解釈と10進解釈が偶然一致するケースしか検証できておらず、
///     より大きい値や終点位置が始点と異なる場合に実測値と食い違うことがtest23key_v2.txtで
///     判明した)。あわせて、旧実装は終点pp=DDD×16を「絶対位置」として扱っていたが、これも
///     誤りで、始点位置が0以外の場合に終点が始点より前に来る不正な結果を生んでいた
///     (test23key_v2.txtのsright_data等、複数件で実測値との厳密一致により確定)。
///   - fine文字の規則(ノート位置・フリーズ始点・フリーズ終点で共通。2026-07-26: ユーザー提供の
///     公式フォーマット文書(FUJI氏本人執筆、公式サイト掲載)で「E(終点)はC(始点/位置)と同一体系」と
///     明記されたため、位置/始点/終点の3者は常に同一ロジックで統一している):
///     '0'=補正なし、'1'〜'9'=+1〜+9フレーム、'A'〜'I'=−1〜−9フレーム(旧「9−d」式は
///     A〜Fでのみ数学的に等価だっただけで、1〜9側は誤りだった)、'R'=12分グリッド丸め、
///     'S'=24分グリッド加算、'W'=24分グリッド減算(公式文書で新規判明、Sの逆符号版と推定。
///     実データでの単独検証はまだ済んでいない)、'T'=pp自体に常に+8(=256/32)・
///     'X'=pp自体に常に−8(Tの符号反転)。2026-07-26: test23key_v2.txtの実測値(通常ノート4点+
///     フリーズ終点2点、計6点、mlen=62.3375下で理論値と厳密一致)により、旧実装の
///     「固定+3/−3フレーム」説は誤りで、pp自体に+8/−8する式が正しいと確定した。当初は
///     S/Wと同様「32刻みなら+8、それ以外は+0」というアライメント依存式を疑ったが、これは
///     フリーズ終点tail解析の別バグ(上記)と混同した誤った暫定結論であり、両バグを修正した
///     上で再検証した結果、アライメントに関係なく常に+8/−8が正しいと判明した。
///     ただし通常ノートの'R'は、対象レーンでフリーズが小節を跨いでいる場合に限り
///     「継続マーカー」として無視される(2026-07-15確定、フリーズ終点側の'R'とは別物)。
///   - "X400-VVVV": speed変化(値=VVVV/1000)、"X410-VVVV": boost変化
/// - カット小節は拍子オブジェクト (16−skip)/16 として表現(次の非カット小節で元拍子へ復帰)
/// - 未確定事項(2026-07-26時点、docs/fuji_format_notes.md参照): 'W'の数式(Sの符号反転と推定
///   しているが実データでの単独検証はまだ済んでいない。test23key_v2.txtのW実例は密な配置での
///   間接確認のみ)、レーン拡張(マーカー'9')とフリーズ終点数字系fine文字の組み合わせ(直接の
///   実例はあるが今回はすべて非拡張レーンでの検証にとどまる)。
/// </summary>
public sealed class FujiImporter
{
    private readonly Func<string, KeyTemplate> _templateResolver;

    public FujiImporter(Func<string, KeyTemplate> templateResolver)
        => _templateResolver = templateResolver;

    /// <summary>
    /// ファイルからdifData行を、キー種で絞り込まずに全て読み取る(2026-07-20: D&D等で
    /// 「まずdifDataを見てキー種・難易度を自動/選択で決める」フローに使う。従来はImport()に
    /// キー種を先に渡す必要があったが、difData自体は各行が自分のキー種を持つ自己完結データ
    /// なので、キー種決定より前に読める)。difData未記載のファイルは空リストを返す
    /// (呼び出し側はキー種を手動で聞く必要がある)。$frameが無い非FUJI形式でも例外を投げない。
    /// </summary>
    public static IReadOnlyList<DifDataCandidate> ScanDifData(string fileText)
    {
        var headerParams = DosParamParser.Parse(ExtractHeaderText(fileText));
        if (!headerParams.TryGetValue("difData", out var difData)) return [];

        return difData.Split('$', '\n')
            .Select(r => r.Trim()).Where(r => r.Length > 0)
            .Select(r => r.Split(','))
            .Where(r => r.Length >= 2)
            .Select(r => new DifDataCandidate(
                r[0].Trim(),
                r[1].Trim(),
                r.Length > 2 && double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null))
            .ToList();
    }

    /// <summary>$header〜次の$セクションまでの本文を取り出す(Import()とScanDifData()で共用)</summary>
    private static string ExtractHeaderText(string fileText)
    {
        var headerText = new System.Text.StringBuilder();
        string mode = "";
        foreach (var raw in fileText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('$'))
            {
                var eq = line.IndexOf('=');
                var key = eq > 0 ? line[1..eq] : line[1..];
                mode = key == "header" ? "header" : "";
                continue;
            }
            if (mode == "header") headerText.AppendLine(raw);
        }
        return headerText.ToString();
    }

    public FujiImportResult Import(string fileText, string keyTypeId)
    {
        var warnings = new List<string>();
        var template = _templateResolver(keyTypeId);

        // --- セクション分解 ---
        var lines = fileText.Replace("\r\n", "\n").Split('\n');
        string? frameLine = null, barcutLine = null;
        var scoreLines = new List<string>();
        var headerText = new System.Text.StringBuilder();
        string mode = "";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('$'))
            {
                var eq = line.IndexOf('=');
                var key = eq > 0 ? line[1..eq] : line[1..];
                var val = eq > 0 ? line[(eq + 1)..] : "";
                switch (key)
                {
                    case "frame": frameLine = val; mode = ""; break;
                    case "barcut": barcutLine = val; mode = ""; break;
                    case "score": mode = "score"; break;
                    case "header": mode = "header"; break;
                    default: mode = ""; break; // $version/$template/$option/$sclist/$dospath等は無視(仕様15.3)
                }
                continue;
            }
            if (mode == "score" && line.Length > 0) scoreLines.Add(line);
            else if (mode == "header") headerText.AppendLine(raw);
        }

        if (frameLine is null) throw new InvalidDataException("$frameがありません(FUJI形式ではない可能性)");

        // --- ヘッダー先読み(blankFrame/StartNumberの算出に必要なため、本体パースより前に読んでおく) ---
        var headerParams = DosParamParser.Parse(headerText.ToString());
        double headerBlankFrame = 0;
        if (headerParams.TryGetValue("blankFrame", out var bfText) &&
            double.TryParse(bfText, NumberStyles.Float, CultureInfo.InvariantCulture, out var bfVal))
            headerBlankFrame = bfVal;

        // $barcut = m/skip,m/skip,...(先にパースし、セグメントごとの小節数計算にも使う)。
        // 2026-07-17: 実データで"0-17/4"のような範囲指定(小節0〜17すべてにskip=4を適用)が
        // 存在することが判明。単一小節番号(int.Parse失敗)前提だったため、この形式に当たると
        // FormatExceptionでインポート全体が失敗していた。"m1-m2/skip"形式も受け付けるようにする。
        var barcut = new Dictionary<int, int>(); // 小節番号 → skip(16分単位)
        if (!string.IsNullOrWhiteSpace(barcutLine))
        {
            foreach (var part in barcutLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var mv = part.Split('/');
                int skip = int.Parse(mv[1], CultureInfo.InvariantCulture);
                var measureSpec = mv[0];
                var dash = measureSpec.IndexOf('-');
                if (dash < 0)
                {
                    barcut[int.Parse(measureSpec, CultureInfo.InvariantCulture)] = skip;
                }
                else
                {
                    int mFrom = int.Parse(measureSpec[..dash], CultureInfo.InvariantCulture);
                    int mTo = int.Parse(measureSpec[(dash + 1)..], CultureInfo.InvariantCulture);
                    for (int m = mFrom; m <= mTo; m++) barcut[m] = skip;
                }
            }
        }

        // --- タイミング導出 ---
        // $frame = セグメント1,セグメント2,...,E
        //   各セグメントは"開始小節/開始フレーム×10/終了フレーム×10/(不明フラグ)"、Eはスラッシュ無しの総小節数。
        // 2026-07-17: 従来は単一BPM("A/B/C/D,E")のみ対応しており、可変BPMのファイル
        // ("0/2110/8410/1,14/8410/27800/0,...,144"のように複数セグメントが連なる形式)ではint.Parseで
        // 例外が飛んでいた(可変BPMインポート失敗の原因)。セグメントの先頭要素(開始小節)を境に
        // 複数のBPMイベントを組み立てるよう変更する。
        // ※この解釈はユーザー提供の再現用ファイル(needs.txt)のデータパターンから逆算したもので、
        //   FUJIエディタ本体の公式仕様は未確認。実データでBPM/tick位置に違和感があれば要追加調査。
        var frameTokens = frameLine.Split(',', StringSplitOptions.TrimEntries);
        if (frameTokens.Length < 2 || frameTokens[^1].Contains('/'))
            throw new InvalidDataException("$frameに小節数(末尾要素)がありません");
        int measureCount = int.Parse(frameTokens[^1], CultureInfo.InvariantCulture);
        var segmentTokens = frameTokens[..^1];

        var segments = segmentTokens.Select(s =>
        {
            var parts = s.Split('/');
            if (parts.Length < 3) throw new InvalidDataException($"$frameのセグメント'{s}'の形式が不正です");
            return (
                StartMeasure: int.Parse(parts[0], CultureInfo.InvariantCulture),
                StartFrame: double.Parse(parts[1], CultureInfo.InvariantCulture) / 10.0,
                EndFrame: double.Parse(parts[2], CultureInfo.InvariantCulture) / 10.0
            );
        }).ToList();

        double firstNumber = segments[0].StartFrame; // $frameから求まる先頭フレーム
        // 2026-07-19c: blankFrameはStartNumberへ織り込まない(ユーザー指示)。
        // blankFrameは人によって変わる個人設定値のため、軸計算には一切使わず、ヘッダー値として
        // project.BlankFrameに保持・受け渡しするだけとする。旧関係式(firstNumber−blankFrame=StartNumber、
        // 2026-07-17に採用)はこの方針変更により撤回。
        double startNumber = firstNumber;

        // --- 拍子オブジェクト生成(カット小節=(16−skip)/16、次の非カット小節で4/4復帰) ---
        var signatures = new List<TimeSignatureEvent>();
        foreach (var (m, skip) in barcut.OrderBy(kv => kv.Key))
        {
            signatures.Add(new TimeSignatureEvent(m, 16 - skip, 16));
            if (!barcut.ContainsKey(m + 1))
                signatures.Add(new TimeSignatureEvent(m + 1, 4, 4));
        }

        // 小節→tick変換は拍子情報だけで決まる(BPMに依存しない)ため、仮のBPMイベントで一旦エンジンを組み、
        // 各セグメント開始小節のtickを求めてから、本物のBPMイベント列を作る。
        var measureTickEngine = new TimingEngine(startNumber, [new BpmEvent(0, 120)], signatures);

        var bpmEvents = new List<BpmEvent>();
        double mlen = 0; // MeasureLength結果用(先頭セグメントの代表値、後方互換のため残す)
        for (int i = 0; i < segments.Count; i++)
        {
            int startM = segments[i].StartMeasure;
            int endM = i + 1 < segments.Count ? segments[i + 1].StartMeasure : measureCount;
            int measureSpan = Math.Max(1, endM - startM);
            double skip16InSpan = barcut.Where(kv => kv.Key >= startM && kv.Key < endM).Sum(kv => kv.Value);
            double segMlen = (segments[i].EndFrame - segments[i].StartFrame) / (measureSpan - skip16InSpan / 16.0);
            double segBpm = 14400.0 / segMlen; // 4/4想定: framesPerMeasure=mlen → BPM=3600×4/mlen
            if (i == 0) mlen = segMlen;
            long tick = measureTickEngine.MeasureStartTick(startM);
            bpmEvents.Add(new BpmEvent(tick, segBpm));
        }
        if (bpmEvents.Count == 0) bpmEvents.Add(new BpmEvent(0, 120));
        if (bpmEvents[0].Tick != 0) bpmEvents.Insert(0, new BpmEvent(0, bpmEvents[0].Bpm));
        var engine = new TimingEngine(startNumber, bpmEvents, signatures);

        // --- 譜面本体パース ---
        var tab = DifficultyTab.CreateFor(template, name: "", initialSpeed: 3.5);
        // 2026-07-18c: レーン索引をEngineLaneNum(本体エンジン順)からFUJI列番号(EffectiveFujiLane)へ変更。
        // 23keyでFUJIの列順(a,main,oni,s,b)と本体エンジン順(a,b,main,oni,s)が異なることが
        // template_23.txt($dosformatの[aNN]対応)から確定したため。FujiLaneNum未定義のテンプレートは
        // 従来通りEngineLaneNumが使われる(5key等、両者が一致するキー種は挙動不変)。
        var laneByFuji = template.Lanes
            .Select((lane, idx) => (lane, idx))
            .ToDictionary(x => x.lane.EffectiveFujiLane, x => x.idx);

        static double FrameAtFractionalTick(TimingEngine eng, double tick)
        {
            long t0 = (long)Math.Floor(Math.Max(0, tick));
            double f0 = eng.TickToFrame(t0);
            double f1 = eng.TickToFrame(t0 + 1);
            return f0 + (Math.Max(0, tick) - t0) * (f1 - f0);
        }

        long TickOf(int measure, double fractionOfMeasure)
        {
            // fraction は名目上の4/4グリッド(192tick)に対する割合
            long start = engine.MeasureStartTick(measure);
            double t = start + fractionOfMeasure * (4.0 * TimingEngine.TicksPerBeat); // 1小節=4拍ぶんのtick
            long tick = (long)Math.Round(t);
            if (Math.Abs(t - tick) > 0.01)
                warnings.Add($"小節{measure}: グリッド外の位置を最近傍tickへ丸めました(誤差{Math.Abs(t - tick):F3}tick)");
            return tick;
        }

        // =====================================================================
        // fine文字(位置微調整)の共通処理。2026-07-26: ユーザー提供の公式フォーマット文書(FUJI氏
        // 本人執筆、公式サイト掲載)により、位置(A欄)・フリーズ始点(C欄)・フリーズ終点(E欄、
        // 「形式はCと同じ」と明記)の3者は完全に同一体系であることが確定した。従って
        // PositionQuantizedPp/DurationQuantizedPpの区別は廃止し、ResolveFineFrameから
        // durationContextパラメータを削除して単一ロジックへ統一する。
        //   '0'      : 補正なし
        //   '1'〜'9' : +1〜+9フレーム(そのまま。旧実装の「9−d」式はA〜Fの範囲でしか正しくなかった)
        //   'A'〜'I' : −1〜−9フレーム(A=10〜I=18として−(値−9))
        //   'R'      : 12分グリッド丸め。旧実装ではフリーズ終点側のみS式(24分グリッド)と
        //              未区別だったが、公式文書の「Eと同じ」との明記により、終点でも12分グリッド式を
        //              使うと確定(2026-07-26、旧「未確定事項」を解消)。
        //   'S'      : 24分グリッドへの加算式(32刻みなら+32/3、それ以外は+16/3)
        //   'W'      : 24分グリッドへの減算式(公式文書で新規判明、Sの符号反転と推定。実データでの
        //              検証はまだ済んでいないため、フォーミュラは暫定)
        //   'T'/'X'  : 公式文書によれば32分グリッドの加算/減算(Sの32分グリッド版)のはずだが、
        //              正確な数式(32刻みで区別するオフセット値)は実データで未検証。過去の暫定実装
        //              (固定+3/−3フレーム)は「mlen=100」という特殊なサンプルでのみS式と数値上
        //              区別できなかっただけで、一般には誤りの可能性が高い。実データ入手まで
        //              現状維持(固定値)とする(docs/fuji_format_notes.md参照)。
        // =====================================================================

        static double? LiteralFrameShift(char fine) => fine switch
        {
            '0' => 0,
            >= '1' and <= '9' => fine - '0',
            >= 'A' and <= 'I' => -(fine - 'A' + 1),
            _ => null,
        };

        // ノート位置・フリーズ始点・フリーズ終点で共通(2026-07-26、公式文書によりE=Cと確定したため
        // 位置/終点を区別する必要がなくなった)。
        //   R = 12分グリッドへ最近傍丸め(タイは後ろ優先)
        //   S = 24分グリッドへの加算式(32刻みなら+32/3、それ以外は+16/3)
        //   W = 24分グリッドへの減算式(Sの符号反転。実データ未検証の暫定式)
        //   T = pp自体に常に+8(=256/32)する式。2026-07-26: ユーザー提供test23key.txt/
        //       test23key_v2_dos.txtの実測値(oni_dataの通常ノート4点+sright系フリーズ終点2点、
        //       計6点)で確認・確定。当初はS/Wと同様「32刻みなら+8、それ以外は+0」という
        //       アライメント依存式を疑ったが、これはフリーズ終点tail解析のバグ
        //       (10進dをhexで誤読していた別バグ)と混同した誤った暫定結論だった。両バグを
        //       修正した上で全6点を再検証した結果、**アライメントに関係なく常に+8**が
        //       正しいと判明した(S/Wのような32刻み依存の式ではない、より単純な固定pp加算式)
        //   X = Tの符号反転(常に−8)
        static double QuantizedPp(double pp, char fine) => fine switch
        {
            'R' => Math.Floor(pp * 3.0 / 64.0 + 0.5) * 64.0 / 3.0,
            'S' => pp + (pp % 32 == 0 ? 32.0 / 3.0 : 16.0 / 3.0),
            'W' => pp - (pp % 32 == 0 ? 32.0 / 3.0 : 16.0 / 3.0),
            'T' => pp + 8.0,
            'X' => pp - 8.0,
            _ => pp,
        };

        // (measure, pp[0-255スケール])にfine文字による微調整を適用した最終フレーム値を返す。
        // R/S/W/T/Xはpp自体をグリッドへ丸めた(足し引きした)上でフレーム変換、それ以外(数字)は
        // 無補正のフレームに対して文字ごとの固定フレームシフトを加算する。2026-07-26:
        // durationContextパラメータは位置/終点の式が統一されたため廃止。T/Xも旧来の固定フレーム
        // シフト方式からpp加減算方式(QuantizedPp)へ移行した。
        double ResolveFineFrame(int measure, double pp, char fine)
        {
            if (fine is 'R' or 'S' or 'W' or 'T' or 'X')
            {
                double adjustedPp = QuantizedPp(pp, fine);
                double tickF = engine.MeasureStartTick(measure) + adjustedPp / 256.0 * (4.0 * TimingEngine.TicksPerBeat);
                return FrameAtFractionalTick(engine, tickF);
            }
            double baseTickF = engine.MeasureStartTick(measure) + pp / 256.0 * (4.0 * TimingEngine.TicksPerBeat);
            double baseFrame = FrameAtFractionalTick(engine, baseTickF);
            return baseFrame + (LiteralFrameShift(fine) ?? 0);
        }

        // ResolveFineFrameが返したフレーム値をtickへ変換し、TickOfで最終スナップ(丸め警告込み)する。
        long FrameToMeasureTick(int measure, double frame)
        {
            double tickF = engine.FrameToTick(frame);
            return TickOf(measure, (tickF - engine.MeasureStartTick(measure)) / (4.0 * TimingEngine.TicksPerBeat));
        }

        static bool IsKnownFineChar(char fine) =>
            fine is 'R' or 'S' or 'W' or 'T' or 'X' || LiteralFrameShift(fine) is not null;

        // 2026-07-26: ノート/フリーズ1件の取り込み中に警告(丸め処理等)が発生した場合、そのオブジェクトへ
        // エラーダイアログと同じ文言のコメント+警告フラグを付与する(warningsリストの増分を利用する)。
        void AnnotateIfWarned(int laneIdx, long tick, int warnCountBefore)
        {
            if (warnings.Count == warnCountBefore) return;
            var msg = string.Join("\n", warnings.Skip(warnCountBefore));
            tab.Lanes[laneIdx].Annotations.Add(new NoteAnnotation(tick, msg, Warning: true));
        }

        foreach (var line in scoreLines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (!int.TryParse(line[..colon], out int measure)) continue;
            var body = line[(colon + 1)..];

            void ParseToken(int measure, string token)
            {
                var dash = token.IndexOf('-');
                if (dash < 0)
                {
                    if (token.Length != 4) { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }

                    // 2026-07-18c: PPフィールドの下位ニブルはレーン番号の拡張ビット(+16)であることが
                    // 実データ(nkeys25W、レーン16以降を含む23key譜面の90トークン)から確定。
                    // 例: '2160' = PP=0x20, レーン6+16=22(bright)。位置は上位ニブルのみで表す。
                    // 2026-07-25: 3文字目(レーン数字)+4文字目(fine文字)の構造はfine='0'の場合も
                    // 含めて常に同一であることが判明(旧「PPLL」解釈はfine='0'のときの特殊ケースに
                    // すぎなかった)。以下、両ケースを統一的に処理する。
                    int ppRaw = Convert.ToInt32(token[..2], 16);
                    int laneExt = (ppRaw & 0x0F) * 16;
                    int pp = ppRaw & 0xF0;
                    char laneDigitChar = token[2];
                    char fineChar = token[3];

                    if (!int.TryParse(laneDigitChar.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int laneDigit))
                    { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }
                    int fujiLane = laneDigit + laneExt;
                    if (!laneByFuji.TryGetValue(fujiLane, out int laneIdx))
                    { warnings.Add($"小節{measure}: FUJIレーン{fujiLane}は{keyTypeId}keyに存在しません('{token}')"); return; }

                    if (fineChar == 'R')
                    {
                        // 'R'はフリーズが小節を跨いでいるレーンでは「継続マーカー」(2026-07-15確定)の
                        // 用途もあるため、該当時は静かに無視する(フリーズ中のレーンにノートは
                        // 置けないため、位置微調整用途とは衝突しない)。
                        long mStartR = engine.MeasureStartTick(measure);
                        long mEndR = engine.MeasureStartTick(measure + 1);
                        bool freezeSpanning = tab.Lanes[laneIdx].Freezes
                            .Any(f => (f.StartTick < mStartR && f.EndTick > mStartR)
                                   || (f.StartTick < mEndR && f.EndTick > mEndR));
                        if (freezeSpanning) return; // フリーズ継続マーカー
                    }

                    if (!IsKnownFineChar(fineChar))
                    { warnings.Add($"小節{measure}: 不明なfine文字を含むトークン'{token}'を無視"); return; }

                    if (fineChar == '0')
                    {
                        int wb0 = warnings.Count;
                        long noteTick0 = TickOf(measure, pp / 256.0);
                        tab.Lanes[laneIdx].Notes.Add(noteTick0);
                        AnnotateIfWarned(laneIdx, noteTick0, wb0);
                        return;
                    }

                    int wb = warnings.Count;
                    double frame = ResolveFineFrame(measure, pp, fineChar);
                    long noteTick = FrameToMeasureTick(measure, frame);
                    tab.Lanes[laneIdx].Notes.Add(noteTick);
                    AnnotateIfWarned(laneIdx, noteTick, wb);
                }
                else
                {
                    var head = token[..dash];
                    var tail = token[(dash + 1)..];
                    if (head.Length != 4) { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }
                    int x = Convert.ToInt32(head[..1], 16);

                    if (head[1] == '8' || head[1] == '9')
                    {
                        // フリーズ: head=開始スロット(1/16)+マーカー(8/9)+レーン数字+fine文字、
                        // tail=終点(duration)。2026-07-25判明: headの3・4文字目はノート位置と同じ
                        // 「レーン数字+fine文字」構造(旧「LL=レーン<<4」解釈はfine='0'の特殊ケース)。
                        // 'フラグ9'はレーン番号+16の拡張(実データの'0920-0120'等から確定、変更なし)。
                        if (!int.TryParse(head[2].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int laneDigit))
                        { warnings.Add($"小節{measure}: フリーズの不明トークン'{token}'を無視"); return; }
                        int fujiLane = laneDigit + (head[1] == '9' ? 16 : 0);
                        if (!laneByFuji.TryGetValue(fujiLane, out int laneIdx))
                        { warnings.Add($"小節{measure}: フリーズのFUJIレーン{fujiLane}が不明('{token}')"); return; }

                        char startFine = head[3];
                        if (!IsKnownFineChar(startFine))
                        { warnings.Add($"小節{measure}: フリーズ始点の不明なfine文字を含むトークン'{token}'を無視"); return; }

                        int wbFrz = warnings.Count; // 始点・終点の丸め警告をまとめてアノテーション化する
                        double startPp = x * 16.0;
                        long startTick = startFine == '0'
                            ? TickOf(measure, startPp / 256.0)
                            : FrameToMeasureTick(measure, ResolveFineFrame(measure, startPp, startFine));

                        // 終点(duration): 公式文書により「D(3桁)は10進数値、E(末尾1文字)はCと同じ
                        // fine文字体系」と明記されている(2026-07-26、test23key.txt/
                        // test23key_dos.txtの16件全数比較で確認・確定)。旧実装は「QQQQが全て16進
                        // として解釈できればhexのdurとしてそのまま使い、0x100以上は−0x60補正」という
                        // 独自ルールを使っていたが、これは誤りだった(末尾1文字がたまたま0〜9/A〜Fの
                        // 場合にhex解釈と10進解釈が偶然一致するケースしか検証できておらず、より大きい
                        // 値や非整列位置では実測値と食い違うことが新サンプルで判明した)。
                        // 正しくは常に「先頭3文字を10進数値dとして読み、終点pp=始点pp+d×16、
                        // 末尾1文字を始点と同じfine文字体系で適用」という単一ルールになる。
                        char endFine = tail[^1];
                        if (!int.TryParse(tail[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int durDigit) || !IsKnownFineChar(endFine))
                        { warnings.Add($"小節{measure}: フリーズ終点の不明なトークン'{token}'を無視"); return; }
                        double durPp = startPp + durDigit * 16.0;
                        long endTick = FrameToMeasureTick(measure, ResolveFineFrame(measure, durPp, endFine));

                        tab.Lanes[laneIdx].Freezes.Add(new FreezeNote(startTick, endTick));
                        AnnotateIfWarned(laneIdx, startTick, wbFrz); // フリーズはStartTickで同定(ColorOverridesと同規約)
                    }
                    else if (head[1] == '4' && (head[2..] == "00" || head[2..] == "10"))
                    {
                        // speed(400)/boost(410): 値=VVVV(10進)/1000
                        double v = int.Parse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture) / 1000.0;
                        var ev = new ValueEvent(TickOf(measure, x / 16.0), v);
                        if (head[2..] == "00") tab.SpeedEvents.Add(ev); else tab.BoostEvents.Add(ev);
                    }
                    else warnings.Add($"小節{measure}: 不明トークン'{token}'を無視");
                }
            }

            foreach (var token in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    ParseToken(measure, token);
                }
                catch (FormatException)
                {
                    // 16進として解釈できないトークン(例: 実データで観測された'502R'等、末尾が16進数字でない特殊記法)。
                    // 原因不明の記法だが、1トークンの解釈失敗で譜面全体のインポートを止めないため警告に留めて継続する。
                    warnings.Add($"小節{measure}: 16進数として解釈できないトークン'{token}'を無視しました(未知の記法の可能性)");
                }
            }
        }

        // --- ヘッダー(参考情報として保持、headerParamsは先頭で取得済みのものを再利用) ---
        var difDataCandidates = new List<DifDataCandidate>();
        if (headerParams.TryGetValue("difData", out var difData))
        {
            var rows = difData.Split('$', '\n')
                .Select(r => r.Trim()).Where(r => r.Length > 0)
                .Select(r => r.Split(',')).ToList();
            var mine = rows.Where(r => r[0].Trim() == keyTypeId).ToList();
            if (mine.Count == 1)
            {
                tab.DifficultyName = mine[0][1].Trim();
                if (mine[0].Length > 2 && double.TryParse(mine[0][2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                    tab.InitialSpeed = sp;
            }
            else if (mine.Count > 1)
            {
                // 複数候補あり: どれを使うかはUI側で選んでもらう(2026-07-16e)。ここでは暫定的に
                // 先頭候補を適用しておき、DifDataCandidatesで全候補を呼び出し側へ渡す。
                foreach (var r in mine)
                {
                    var name = r.Length > 1 ? r[1].Trim() : "";
                    double? spd = r.Length > 2 && double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        ? s : null;
                    difDataCandidates.Add(new DifDataCandidate(keyTypeId, name, spd));
                }
                tab.DifficultyName = difDataCandidates[0].DifficultyName;
                if (difDataCandidates[0].InitialSpeed is { } sp0) tab.InitialSpeed = sp0;
            }
            else
            {
                warnings.Add($"difDataに{keyTypeId}keyの行が見つからないため難易度名を特定できません(手動設定が必要)");
            }
        }

        // FUJIのspeedタグはVVVV=1000(1.00)等。値が入っていないspeed/boostはソート
        tab.SpeedEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        tab.BoostEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        return new FujiImportResult
        {
            Tab = tab,
            BlankFrame = headerBlankFrame,
            StartNumber = startNumber,
            MeasureLength = mlen,
            BpmEvents = bpmEvents,
            TimeSignatures = signatures,
            HeaderParams = headerParams,
            Warnings = warnings,
            DifDataCandidates = difDataCandidates,
        };
    }
}
