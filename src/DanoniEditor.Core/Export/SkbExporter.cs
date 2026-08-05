using System.Text.Json;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Export;

/// <summary>SKBエディタ形式(<see cref="DanoniEditor.Core.Import.SkbImporter"/>の逆方向)への
/// エクスポートオプション(2026-08-03)。</summary>
public sealed class SkbExportOptions
{
    /// <summary>1ページあたりの拍数(danoni-editor CONFIG画面の「Page Block Num」相当、本体の既定は8)。
    /// BPM変化点はページ境界(この値×TicksPerBeatの倍数tick)にしか置けないため、値を大きくするほど
    /// BPM変化位置がページ境界からズレやすくなる。本ツールでは「1ページ=1小節(4/4)」に近い4を
    /// 既定とする(本体既定の8より、一般的な小節頭でのBPM変化に整合しやすいため)。</summary>
    public double PageBlockNum { get; init; } = 4;

    /// <summary>BPM変化点がページ境界に乗らない場合の扱い(2026-08-03要望対応)。
    /// true=最も近いページ境界へ丸める、false=そのBPM変化点自体を出力から除外する
    /// (直前のBPMのまま継続する)。いずれの場合も警告に記録する。</summary>
    public bool RoundMisalignedBpmEvents { get; init; } = true;

    /// <summary>ノート・フリーズ・速度変化の位置がSKBネイティブのグリッド(1/48拍刻み、下記クラス
    /// コメント参照)に乗らない場合の扱い(2026-08-03不具合修正で新設)。
    /// true=最も近い位置へ丸める、false=そのオブジェクト自体を出力から除外する。</summary>
    public bool RoundMisalignedPositions { get; init; } = true;

    public int ScoreNumber { get; init; } = 1;
    public string? ScorePrefix { get; init; }
}

public sealed class SkbExportResult
{
    public required string Json { get; init; }
    public required List<string> Warnings { get; init; }
}

/// <summary>
/// SKBエディタ(superkuppabros/danoni-editor)保存ファイル(JSON)形式のエクスポーター
/// (<see cref="DanoniEditor.Core.Import.SkbImporter"/>の逆方向、2026-08-03)。
///
/// 【拍飛ばし・変拍子について】SKBのtickモデルは「ページ番号×ページあたりtick数+ページ内位置」
/// という完全な線形モデルで、「小節」「拍子」という概念自体を持たない(danoni-editor公式リポジトリの
/// CONFIG画面説明「Page Block Num: 1ページに表示する拍子の数を変更する(デフォルトは8)」、および
/// PR #124「ページのブロック数をラベルごとに設定できるように」の記述により確認済み。$barcut
/// 相当の「小節カット」を表す項目も公式仕様書に存在しない)。このため本ツール内部のTimeSignatures
/// (拍飛ばし・変拍子)はSKB側に出力しない。各ノート・フリーズの実際のtick位置は内部のTimingEngineで
/// 正しく求まるため、ゲームプレイ上のタイミングそのものには影響しない(SKBエディタで開いた際の
/// 「ページ区切り」が元の小節構造と一致しない見た目になるだけで、これはSKB自体の仕様に由来する
/// 制約であり、本エクスポータの制限ではない)。
///
/// 【tickスケールについて、2026-08-03不具合修正】SKBネイティブの保存形式は「4分音符=48tick」の
/// 分解能で固定(実データ・danoniplus本体資料で一貫して確認された値、docs/fuji_format_notes.md等
/// 参照)であり、本ツール内部の分解能(TicksPerBeat=1680、2026-07-19gに5連符/7連符対応で48から
/// 引き上げ)とは<b>別物</b>。notes[]等の生の整数値(以下「ネイティブtick」)は、内部tickを
/// PosScale(=1680/48=35)で割った値として書き出す必要がある。
///
/// 当初の実装では内部tickをそのままネイティブtick扱いで書き出しており、実際のSKBエディタで
/// 読み込むと位置が最大35倍近くズレる不具合があった(ユーザー提供の実データ
/// skbtestbynode.txt/ittnbynode.txtの比較で発覚、2026-08-03)。startNum/bpm/blankFrame(タイミング側)は
/// 元々スケールの影響を受けないため問題無く一致していたが、notes/freezes/speedsの位置のみ
/// この不具合の影響を受けていた。
///
/// 内部tickがPosScaleの倍数でない位置(標準的なグリッド分割では基本的に発生しないが、微調整等で
/// 発生し得る)は、<see cref="SkbExportOptions.RoundMisalignedPositions"/>の方針(丸める/削除する)に
/// 従って処理する。
/// </summary>
public static class SkbExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>SKBネイティブの1拍あたりtick数(4分音符=48tick、実データで確認済みの固定値)。</summary>
    private const long NativeTicksPerBeat = 48;

    /// <summary>本ツール内部tick(TicksPerBeat=1680)→SKBネイティブtick(48/拍)への換算係数(=35)。</summary>
    private static readonly long PosScale = TimingEngine.TicksPerBeat / NativeTicksPerBeat;

    private sealed record SkbSpeedOut(long position, double value, string type);
    private sealed record SkbPageOut(List<List<long>> notes, List<List<long>> freezes, List<SkbSpeedOut> speeds);
    private sealed record SkbTimingOut(long label, double startNum, double bpm, double pageBlockNum);
    private sealed record SkbFileOut(string keyKind, List<SkbPageOut> scores, double blankFrame,
                                      List<SkbTimingOut> timings, int scoreNumber, string? scorePrefix);

    public static SkbExportResult Export(ChartProject project, DifficultyTab tab, KeyTemplate template, SkbExportOptions options)
    {
        var warnings = new List<string>();
        int laneCount = template.KeyCount;
        // ticksPerPageInternal: 本ツール内部tickでのページ境界(BPM変化点のラベル計算に使う)。
        long ticksPerPageInternal = (long)Math.Round(options.PageBlockNum * TimingEngine.TicksPerBeat);
        if (ticksPerPageInternal <= 0)
            throw new InvalidOperationException("pageBlockNumは正の値を指定してくださいまし");
        // ticksPerPageNative: SKBネイティブtickでのページ境界(notes[]等の実際の値の範囲を決める)。
        long ticksPerPageNative = (long)Math.Round(options.PageBlockNum * NativeTicksPerBeat);

        var engine = project.CreateTimingEngine();

        // --- timings(BPM変化点)。ページ境界(ticksPerPageInternalの倍数)にしか置けないため、
        // 乗らない変化点はオプションに従って丸める/削除する。startNum/bpmはtick単位ではなく
        // フレーム値そのものなので、PosScaleの影響を受けない。 ---
        var sourceBpmEvents = project.BpmEvents.OrderBy(e => e.Tick).ToList();
        if (sourceBpmEvents.Count == 0 || sourceBpmEvents[0].Tick != 0)
            throw new InvalidOperationException("BPMイベントの先頭がtick0にありません(不正なプロジェクトデータ)");

        var timings = new List<SkbTimingOut>();
        long? lastEmittedTick = null;
        foreach (var ev in sourceBpmEvents)
        {
            long tick = ev.Tick;
            if (tick != 0)
            {
                long remainder = tick % ticksPerPageInternal;
                if (remainder != 0)
                {
                    if (!options.RoundMisalignedBpmEvents)
                    {
                        warnings.Add($"BPM変化(tick={tick}, BPM={ev.Bpm})はページ境界(pageBlockNum={options.PageBlockNum})に" +
                                     "乗らないため、削除しましたの。直前のBPMのまま継続します。");
                        continue;
                    }
                    long rounded = (long)Math.Round(tick / (double)ticksPerPageInternal, MidpointRounding.AwayFromZero) * ticksPerPageInternal;
                    warnings.Add($"BPM変化(tick={tick}, BPM={ev.Bpm})はページ境界に乗らないため、tick={rounded}へ" +
                                 $"丸めましたの(誤差{Math.Abs(tick - rounded)}tick)。");
                    tick = rounded;
                }
            }
            if (lastEmittedTick == tick)
            {
                warnings.Add($"BPM変化(tick={ev.Tick}, BPM={ev.Bpm})は直前のBPM変化と同じページに丸められたため、" +
                             "削除しましたの。");
                continue;
            }
            long label = tick / ticksPerPageInternal + 1;
            double startNum = engine.TickToFrame(tick);
            timings.Add(new SkbTimingOut(label, startNum, ev.Bpm, options.PageBlockNum));
            lastEmittedTick = tick;
        }
        if (timings.Count == 0)
            throw new InvalidOperationException("BPM変化点を1つも出力できませんでしたの(プロジェクトデータをご確認くださいまし)");

        // --- ノート・フリーズ・速度変化の最大tickからページ数を決定 ---
        long maxTick = 0;
        foreach (var lane in tab.Lanes)
        {
            foreach (var n in lane.Notes) maxTick = Math.Max(maxTick, n);
            foreach (var f in lane.Freezes) maxTick = Math.Max(maxTick, Math.Max(f.StartTick, f.EndTick));
        }
        foreach (var e in tab.SpeedEvents) maxTick = Math.Max(maxTick, e.Tick);
        foreach (var e in tab.BoostEvents) maxTick = Math.Max(maxTick, e.Tick);

        int pageCount = (int)(maxTick / ticksPerPageInternal) + 1;
        var pages = new List<SkbPageOut>();
        for (int p = 0; p < pageCount; p++)
            pages.Add(new SkbPageOut(
                notes: [.. Enumerable.Range(0, laneCount).Select(_ => new List<long>())],
                freezes: [.. Enumerable.Range(0, laneCount).Select(_ => new List<long>())],
                speeds: []));

        // 2026-08-03不具合修正: 内部tickをPosScale(=35)で割ってSKBネイティブtickへ変換してから
        // ページ・ページ内位置を求める(修正前は内部tickをそのままネイティブtick扱いで書き出しており、
        // 実機のSKBエディタで読み込むと位置が大きくズレる不具合があった)。
        void PlaceTick(long tick, string label, Action<SkbPageOut, long> place)
        {
            long remainder = tick % PosScale;
            long nativeTick;
            if (remainder == 0)
            {
                nativeTick = tick / PosScale;
            }
            else if (!options.RoundMisalignedPositions)
            {
                warnings.Add($"{label}(tick={tick})はSKB形式のグリッド(1/{NativeTicksPerBeat}拍刻み)に乗らないため、" +
                             "削除しましたの。");
                return;
            }
            else
            {
                long roundedTick = (long)Math.Round(tick / (double)PosScale, MidpointRounding.AwayFromZero) * PosScale;
                nativeTick = roundedTick / PosScale;
                warnings.Add($"{label}(tick={tick})はSKB形式のグリッド(1/{NativeTicksPerBeat}拍刻み)に乗らないため、" +
                             $"tick={roundedTick}へ丸めましたの。");
            }
            int page = (int)(nativeTick / ticksPerPageNative);
            long pos = nativeTick % ticksPerPageNative;
            if (page < 0 || page >= pages.Count)
            {
                warnings.Add($"{label}(tick={tick})はページ範囲外のため出力できませんでしたの。");
                return;
            }
            place(pages[page], pos);
        }

        for (int lane = 0; lane < laneCount && lane < tab.Lanes.Count; lane++)
        {
            var laneLabel = template.Lanes[lane].LaneId;
            foreach (var n in tab.Lanes[lane].Notes)
            {
                int laneIdx = lane;
                PlaceTick(n, $"'{laneLabel}'のノート", (pg, pos) => pg.notes[laneIdx].Add(pos));
            }
            foreach (var f in tab.Lanes[lane].Freezes)
            {
                int laneIdx = lane;
                PlaceTick(f.StartTick, $"'{laneLabel}'のフリーズ始点", (pg, pos) => pg.freezes[laneIdx].Add(pos));
                PlaceTick(f.EndTick, $"'{laneLabel}'のフリーズ終点", (pg, pos) => pg.freezes[laneIdx].Add(pos));
            }
        }
        foreach (var e in tab.SpeedEvents)
            PlaceTick(e.Tick, "速度変化", (pg, pos) => pg.speeds.Add(new SkbSpeedOut(pos, e.Value, "speed")));
        foreach (var e in tab.BoostEvents)
            PlaceTick(e.Tick, "ブースト変化", (pg, pos) => pg.speeds.Add(new SkbSpeedOut(pos, e.Value, "boost")));

        // 各ページ内のnotes/freezesは昇順にしておく(本体側の想定順序に合わせる、読み込み比較のしやすさのため)
        foreach (var pg in pages)
        {
            foreach (var l in pg.notes) l.Sort();
            foreach (var l in pg.freezes) l.Sort();
            pg.speeds.Sort((a, b) => a.position.CompareTo(b.position));
        }

        var file = new SkbFileOut(tab.KeyTypeId, pages, project.BlankFrame, timings, options.ScoreNumber, options.ScorePrefix);
        var json = JsonSerializer.Serialize(file, JsonOpts);

        return new SkbExportResult { Json = json, Warnings = warnings };
    }
}
