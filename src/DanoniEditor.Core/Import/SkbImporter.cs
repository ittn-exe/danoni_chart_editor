using System.Text.Json;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Import;

/// <summary>SKBエディタ保存ファイル(JSON)のインポート結果</summary>
public sealed class SkbImportResult
{
    public required DifficultyTab Tab { get; init; }
    public required double BlankFrame { get; init; }
    public required double StartNumber { get; init; }         // blank + timings[0].startNum
    public required List<BpmEvent> BpmEvents { get; init; }
    public required string KeyKind { get; init; }
    public required int ScoreNumber { get; init; }
    public required List<string> Warnings { get; init; }

    public TimingEngine CreateTimingEngine() => new(StartNumber, BpmEvents);
}

/// <summary>
/// SKBエディタ形式のインポーター(仕様書15.4)。実データ(skb_test/skb_test_dos)で検証した解釈:
/// - ★scores[] は難易度ごとではなく「ページごと」の配列(仕様書15.4の記述はこの点誤り)
/// - 1ページ = pageBlockNum × 48tick(4分音符=48tick、SKBネイティブの固定分解能)。
///   globalTick(ネイティブ) = ページindex×tpp + tick
/// - frame = blankFrame + startNum + globalTick × (3600/bpm/48)   ※timings[0]基準
/// - freezes はレーンごとにページ横断で平坦化し、順に[始点,終点,始点,終点...]とペアリング
///   (ページを跨ぐフリーズは始点と終点が別ページに現れる)
/// - timings[1..] は BpmEvent(tick=(label−1)×tpp) として取り込む。2026-07-17c: SKB本体の
///   ソース(ScoreConvertService.ts/Calculator.ts)を確認したところ、各timingセグメントは
///   「前区間からの連続積算」ではなく「自分自身のstartNumberを絶対フレーム基準として持つ」設計
///   だと判明した(=意図的な「再同期」機能で、データの誤りではない)。これをBpmEvent.FrameAnchor
///   (2026-07-17c追加)として正確に再現する — 各セグメント先頭のBpmEventに
///   FrameAnchor=blankFrame+timing.startNumを設定することで、TimingEngine側が区間の境目で
///   積算をリセットし、SKB本体の計算式と厳密に一致するフレーム値を再現できる。
///
/// 2026-08-03不具合修正: notes[]/freezes[]/speeds[].position等の生の整数値(SKBネイティブの
/// 48tick/拍分解能)を、本ツール内部の分解能(TicksPerBeat=1680、2026-07-19gに48から引き上げ)へ
/// 変換せずそのまま加算していたため、実際のSKBエディタで作成されたファイルを読み込むと位置が
/// 大きくズレる不具合があった(SkbExporter側の対称な不具合とユーザー提供の実データ比較
/// (skbtestbynode.txt/ittnbynode.txt)により発覚)。PosScale(=1680/48=35)を乗じて変換する。
/// </summary>
public sealed class SkbImporter
{
    private readonly Func<string, KeyTemplate> _templateResolver;

    /// <summary>SKBネイティブの1拍あたりtick数(4分音符=48tick、実データで確認済みの固定値)。</summary>
    private const long NativeTicksPerBeat = 48;

    /// <summary>SKBネイティブtick(48/拍)→本ツール内部tick(TicksPerBeat=1680)への換算係数(=35)。</summary>
    private static readonly long PosScale = TimingEngine.TicksPerBeat / NativeTicksPerBeat;

    public SkbImporter(Func<string, KeyTemplate> templateResolver)
        => _templateResolver = templateResolver;

    private sealed record SkbSpeed(long position, double value, string type);
    private sealed record SkbPage(List<List<long>> notes, List<List<long>> freezes, List<SkbSpeed> speeds);
    // 2026-07-17: 実データでpageBlockNumが4.5のような非整数値を取り得ることが判明(intのままだと
    // JSON逆シリアライズで例外発生・インポート全体が失敗する)。doubleに変更して受け付ける。
    private sealed record SkbTiming(long label, double startNum, double bpm, double pageBlockNum);
    private sealed record SkbFile(string keyKind, List<SkbPage> scores, double blankFrame,
                                  List<SkbTiming> timings, int scoreNumber, string? scorePrefix);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = false,
    };

    public SkbImportResult Import(string jsonText)
    {
        var warnings = new List<string>();
        var skb = JsonSerializer.Deserialize<SkbFile>(jsonText, JsonOpts)
            ?? throw new InvalidDataException("SKBファイルの解析に失敗しました");
        if (skb.timings.Count == 0) throw new InvalidDataException("timingsが空です");

        var template = _templateResolver(skb.keyKind);
        var tab = DifficultyTab.CreateFor(template, name: "", initialSpeed: 3.5); // 難易度名はSKBに無い→要手動設定(仕様15.4)

        var t0 = skb.timings[0];
        // pageBlockNumが非整数(例:4.5)の場合もtick数は整数になるよう丸める(0.5刻みならTicksPerBeat=48との
        // 積は必ず整数になるが、念のためRoundで安全側に倒す)。
        long ticksPerPage = (long)Math.Round(t0.pageBlockNum * TimingEngine.TicksPerBeat);

        // 2026-07-18c: 軸の修正。エディタ内部のフレーム軸は「曲頭=0」(再生同期・波形表示と同一基準)で、
        // blankFrameはヘッダー値として別途保持し、本体側が再生時に加算する設計。SKBの
        // 「blankFrame+startNum+…」はdos.txt出力軸(ゲーム開始=0)のため、従来のようにblankFrameを
        // ここで加算すると二重計上になり、(1)再生位置がblankFrameぶんズレる
        // (2)右パネルのStartNumberが「blank+負のstartNum」の謎の正値になる、という不具合が出ていた。
        // 曲頭=0軸ではstartNumをそのまま使う。
        double startNumber = t0.startNum;

        // --- BPMイベント列(labelは1始まりのページ番号)。2026-07-17c: 各セグメント先頭に
        // FrameAnchorを設定し、SKB本体と同じ「区間ごとの絶対フレーム基準」を再現する
        // (前区間からの連続積算に頼らないため、再同期ジャンプがあっても正確)。
        // 2026-07-18c: 先頭区間にはアンカーを張らない。TimingEngineは先頭アンカーがあると
        // StartNumberを無視するため、右パネルからのStartNumber編集が一切効かなくなっていた
        // (謎の不具合(3)の正体)。先頭区間=StartNumber基準、2区間目以降=アンカー基準(曲頭=0軸)とする。
        var bpmEvents = new List<BpmEvent> { new(0, t0.bpm) };
        if (t0.label != 1)
            warnings.Add($"最初のtimingのlabelが{t0.label}です(1を想定)。tick0にBPM{t0.bpm}を適用します");
        foreach (var t in skb.timings.Skip(1))
        {
            long tick = (t.label - 1) * ticksPerPage;
            bpmEvents.Add(new BpmEvent(tick, t.bpm, t.startNum));
            if (Math.Abs(t.pageBlockNum - t0.pageBlockNum) > 0.001)
                warnings.Add($"timing(label={t.label})のpageBlockNum={t.pageBlockNum}が先頭と異なります(グリッド変更は未対応、{t0.pageBlockNum}を継続)");
        }

        // --- ノート/フリーズ/速度 ---
        int laneCount = template.KeyCount;
        var frzBoundaries = Enumerable.Range(0, laneCount).Select(_ => new List<long>()).ToList();

        for (int page = 0; page < skb.scores.Count; page++)
        {
            var pg = skb.scores[page];
            long pageBase = (long)page * ticksPerPage;

            for (int lane = 0; lane < Math.Min(laneCount, pg.notes.Count); lane++)
                foreach (var pos in pg.notes[lane])
                    tab.Lanes[lane].Notes.Add(pageBase + pos * PosScale);
            if (pg.notes.Count != laneCount)
                warnings.Add($"ページ{page + 1}: notesのレーン数{pg.notes.Count}がキー数{laneCount}と一致しません");

            for (int lane = 0; lane < Math.Min(laneCount, pg.freezes.Count); lane++)
                foreach (var pos in pg.freezes[lane])
                    frzBoundaries[lane].Add(pageBase + pos * PosScale);

            foreach (var sp in pg.speeds)
            {
                var ev = new ValueEvent(pageBase + sp.position * PosScale, sp.value);
                if (sp.type == "speed") tab.SpeedEvents.Add(ev);
                else if (sp.type == "boost") tab.BoostEvents.Add(ev);
                else warnings.Add($"ページ{page + 1}: 不明なspeeds.type='{sp.type}'を無視");
            }
        }

        // フリーズのペアリング(境界列を順に始点/終点として組む)
        for (int lane = 0; lane < laneCount; lane++)
        {
            var b = frzBoundaries[lane];
            b.Sort();
            for (int i = 0; i + 1 < b.Count; i += 2)
                tab.Lanes[lane].Freezes.Add(new FreezeNote(b[i], b[i + 1]));
            if (b.Count % 2 == 1)
                warnings.Add($"{template.Lanes[lane].LaneId}: 終点の無いフリーズ始点(tick={b[^1]})を破棄しました(作業途中データ)");
        }

        tab.SpeedEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        tab.BoostEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        return new SkbImportResult
        {
            Tab = tab,
            BlankFrame = skb.blankFrame,
            StartNumber = startNumber,
            BpmEvents = bpmEvents,
            KeyKind = skb.keyKind,
            ScoreNumber = skb.scoreNumber,
            Warnings = warnings,
        };
    }
}
