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
/// FUJIエディタ形式のインポーター(仕様書15.3)。実データ(a.txt/a_dos.txt, by_node)で検証した解釈:
/// - $frame=A/B/C/D,E: blank=B/10, 総フレーム=C/10, E=小節数。ヘッダーのblankFrameより$frameのBが優先。
/// - mlen = (C/10 − blank) / (E − Σskip/16)   ※skipは$barcutの16分単位カット量
/// - 小節行 "MMMM:tok,tok,..." のトークン:
///   - 4hex "PPLL": 通常ノート。frame = cum(M) + PP/256 × mlen。LL = engineLaneNum×0x10
///   - "X8LL-QQQQ": フリーズ。X=1/16スロット、start = cum(M) + X/16 × mlen、
///     dur = (QQQQ ≥ 0x100 ? QQQQ−0x60 : QQQQ)/256 × mlen
///   - "X400-VVVV": speed変化(値=VVVV/1000)、"X410-VVVV": boost変化
/// - カット小節は拍子オブジェクト (16−skip)/16 として表現(次の非カット小節で元拍子へ復帰)
/// </summary>
public sealed class FujiImporter
{
    private readonly Func<string, KeyTemplate> _templateResolver;

    public FujiImporter(Func<string, KeyTemplate> templateResolver)
        => _templateResolver = templateResolver;

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
                    int ppRaw = Convert.ToInt32(token[..2], 16);
                    int laneExt = (ppRaw & 0x0F) * 16;
                    int pp = ppRaw & 0xF0;
                    char laneDigitChar = token[2];
                    char fineChar = token[3];

                    if (fineChar == '0')
                    {
                        // 通常ノート PPLL(LL=FUJI列番号の下位×0x10)
                        int ll = Convert.ToInt32(token[2..], 16);
                        if ((ll & 0x0F) != 0)
                        { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視(LL下位ニブル非0)"); return; }
                        int fujiLane = (ll >> 4) + laneExt;
                        if (!laneByFuji.TryGetValue(fujiLane, out int laneIdx))
                        { warnings.Add($"小節{measure}: FUJIレーン{fujiLane}は{keyTypeId}keyに存在しません('{token}')"); return; }
                        tab.Lanes[laneIdx].Notes.Add(TickOf(measure, pp / 256.0));
                        return;
                    }

                    // 2026-07-17: 高精度ノート。4文字目が'0'以外(1-F、または'T')の場合、3文字目は
                    // レーン番号を直接指す(×0x10しない)。4文字目の意味は2種類あることがユーザーに
                    // 確認済み:
                    //  ・'T': 16分グリッド(PP)から32分音符ぶん(=PP単位で+8、tick換算で+6)後ろの位置を
                    //         表す固定マーカー(最終拍に32分音符を含む譜面のデータで確認済み)。
                    //  ・1-F: PP単位をさらに16分割する微調整量(FUJIエディタ上で手動フレーム補正した
                    //         ノートのデータ、実測小節47のパターンから逆算)。
                    if (!int.TryParse(laneDigitChar.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int laneDigit))
                    { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }
                    int fineFujiLane = laneDigit + laneExt;
                    if (!laneByFuji.TryGetValue(fineFujiLane, out int fineLaneIdx))
                    { warnings.Add($"小節{measure}: FUJIレーン{fineFujiLane}は{keyTypeId}keyに存在しません('{token}')"); return; }

                    double finePp;
                    if (fineChar == 'T')
                    {
                        finePp = pp + 8; // 32分音符ぶん後ろ(PP単位、256分の1測度)
                    }
                    else if (fineChar == 'S')
                    {
                        // 2026-07-18e 確定(nkeys25W修正版+dos出力の全ノート照合):
                        // 'S' = 24分グリッド配置。PPの16分スロット内にある唯一の24分3連位置へ「後ろに」シフト。
                        //   PP%32==0(8分表) → +2/3×16分(+32/3pp)   例: 0x00→10.67, 0x20→42.67
                        //   PP%32==16(8分裏) → +1/3×16分(+16/3pp)  例: 0x10→21.33, 0x90→149.33
                        // tick換算では常に整数(24分=8tick)。
                        finePp = pp + (pp % 32 == 0 ? 32.0 / 3.0 : 16.0 / 3.0);
                    }
                    else if (fineChar == 'R')
                    {
                        // 2026-07-18e 確定(同上): 'R' = 12分グリッド配置。PPを12分グリッドへ
                        // 四捨五入する(同距離のときは後ろへ)。Sと違い前方向へも動く
                        // (例: '70CR'=112→106.67、'313R'=48→42.67。60小節の実データで確認)。
                        // なお'R'はフリーズが小節を跨いでいるレーンでは「継続マーカー」(2026-07-15確定)の
                        // 用途もあるため、該当時は従来通り静かに無視する(フリーズ中のレーンに
                        // ノートは置けないため両用途は衝突しない)。
                        long mStartR = engine.MeasureStartTick(measure);
                        long mEndR = engine.MeasureStartTick(measure + 1);
                        bool freezeSpanning = tab.Lanes[fineLaneIdx].Freezes
                            .Any(f => (f.StartTick < mStartR && f.EndTick > mStartR)
                                   || (f.StartTick < mEndR && f.EndTick > mEndR));
                        if (freezeSpanning) return; // フリーズ継続マーカー
                        double n = Math.Floor(pp * 3.0 / 64.0 + 0.5); // 12分単位で四捨五入(0.5は切り上げ)
                        finePp = n * 64.0 / 3.0;
                    }
                    else if (!int.TryParse(fineChar.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int fineDigit))
                    { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }
                    else
                    {
                        // 2026-07-18e 確定(dos出力4トークンとの照合、誤差全て0.5f未満):
                        // fine桁d(1〜F)は「PP位置から(9−d)フレームのシフト」を表す手動フレーム補正。
                        //   A〜F → −1〜−6フレーム(手前へ)、1〜8 → +8〜+1フレーム(後ろへ、実例未観測の外挿)。
                        // 従来解釈(+d/16 PP)は誤りだった(最大5フレームずれていた)。
                        // フレーム軸の補正はtickグリッドに乗らないため、最近傍tickへの丸めが発生する
                        // (下のTickOfが情報として警告を出す)。
                        double baseTickF = engine.MeasureStartTick(measure) + pp / 256.0 * (4.0 * TimingEngine.TicksPerBeat);
                        double baseFrame = FrameAtFractionalTick(engine, baseTickF);
                        double shifted = baseFrame + (9 - fineDigit);
                        double tickF = engine.FrameToTick(shifted);
                        tab.Lanes[fineLaneIdx].Notes.Add(TickOf(measure, (tickF - engine.MeasureStartTick(measure)) / (4.0 * TimingEngine.TicksPerBeat)));
                        return;
                    }

                    tab.Lanes[fineLaneIdx].Notes.Add(TickOf(measure, finePp / 256.0));
                }
                else
                {
                    var head = token[..dash];
                    var tail = token[(dash + 1)..];
                    if (head.Length != 4) { warnings.Add($"小節{measure}: 不明トークン'{token}'を無視"); return; }
                    int x = Convert.ToInt32(head[..1], 16);
                    int value = Convert.ToInt32(tail, 16);

                    if (head[1] == '8' || head[1] == '9')
                    {
                        // フリーズ X8LL-QQQQ(2026-07-18c: フラグ'9'はレーン番号+16の拡張。
                        // 実データの'0920-0120'(レーン2+16=18=sright)等から確定)
                        int ll = Convert.ToInt32(head[2..], 16);
                        int fujiLane = (ll >> 4) + (head[1] == '9' ? 16 : 0);
                        if (!laneByFuji.TryGetValue(fujiLane, out int laneIdx))
                        { warnings.Add($"小節{measure}: フリーズのFUJIレーン{fujiLane}が不明('{token}')"); return; }
                        int dur = value >= 0x100 ? value - 0x60 : value; // 実データ検証済みの補正(仕様15.3)
                        long startTick = TickOf(measure, x / 16.0);
                        long endTick = TickOf(measure, x / 16.0 + dur / 256.0);
                        tab.Lanes[laneIdx].Freezes.Add(new FreezeNote(startTick, endTick));
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
