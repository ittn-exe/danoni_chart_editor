using System.Globalization;
using System.Text;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Export;

/// <summary>FUJIエディタ形式(<see cref="DanoniEditor.Core.Import.FujiImporter"/>の逆方向)への
/// エクスポートオプション(2026-08-03)。</summary>
public sealed class FujiExportOptions
{
    /// <summary>グリッドに乗らない位置(fine文字での微調整でも再現しきれない場合)の扱い。
    /// true=最も近い位置へ丸めて警告を出す、false=当該オブジェクト(ノート/フリーズ/BPM変化/
    /// 速度変化)自体を出力から除外する。</summary>
    public bool RoundMisalignedPositions { get; init; } = true;
}

public sealed class FujiExportResult
{
    public required string Text { get; init; }
    public required List<string> Warnings { get; init; }
}

/// <summary>
/// FUJIエディタ形式のエクスポーター(<see cref="DanoniEditor.Core.Import.FujiImporter"/>の逆方向、
/// 2026-08-03)。
///
/// 【使用するfine文字の範囲について】FujiImporterのdocs/fuji_format_notes.mdに記載の通り、
/// 数字系fine文字('0'〜'9','A'〜'I' = リテラルなフレームシフト)は実データで全数検証済みだが、
/// 'R'/'S'/'W'/'T'/'X'(グリッド依存のpp加減算式)は'W'の単独実データ検証が未了(Sの符号反転という
/// 推定のみ)であるなど、確実性に差がある。本エクスポータは確実性の高い方式のみを採用する:
/// 位置は常に16刻みの基準pp(0,16,32,...,240 = 1/16測度単位、'0'相当)へスナップし、そこからの
/// 残差は数字系fine文字(±1〜±9フレームのリテラルシフト)でのみ補正する。±9フレームを超える
/// 残差は「グリッドに乗らない位置」として<see cref="FujiExportOptions.RoundMisalignedPositions"/>の
/// 方針に従う(丸める=±9フレームへクランプ、削除する=そのオブジェクトを出力しない)。
/// 'R'/'S'/'W'/'T'/'X'は一切使用しない(確実性の低い式に依存しないため)。
///
/// 【拍飛ばし・変拍子】ChartProject.TimeSignaturesのうち既定(4/4)と異なる区間を$barcutとして
/// 出力する(FujiImporterの逆変換、Denominatorが16でない場合は近似する旨を警告する)。
///
/// 【BPM変化】$frameの各セグメントは小節境界にしか置けないため、小節境界に乗らないBPM変化点は
/// <see cref="FujiExportOptions.RoundMisalignedPositions"/>の方針に従って最寄りの小節境界へ丸めるか、
/// その変化点自体を削除する。
/// </summary>
public static class FujiExporter
{
    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    public static FujiExportResult Export(ChartProject project, DifficultyTab tab, KeyTemplate template, FujiExportOptions options)
    {
        var warnings = new List<string>();
        var engine = project.CreateTimingEngine();

        long TicksPerMeasureNominal = 4L * TimingEngine.TicksPerBeat;

        // --- 最大tick(measureCount決定用) ---
        long maxTick = 0;
        foreach (var lane in tab.Lanes)
        {
            foreach (var n in lane.Notes) maxTick = Math.Max(maxTick, n);
            foreach (var f in lane.Freezes) maxTick = Math.Max(maxTick, Math.Max(f.StartTick, f.EndTick));
        }
        foreach (var e in tab.SpeedEvents) maxTick = Math.Max(maxTick, e.Tick);
        foreach (var e in tab.BoostEvents) maxTick = Math.Max(maxTick, e.Tick);
        var (maxMeasure, _) = engine.TickToMeasurePosition(maxTick);
        int measureCount = maxMeasure + 1;

        // --- $barcut(拍飛ばし・変拍子) ---
        var barcutEntries = new List<(int Measure, int Skip)>();
        foreach (var sig in project.TimeSignatures)
        {
            if (sig.MeasureIndex == 0 && sig.Numerator == 4 && sig.Denominator == 4) continue; // 既定値、出力不要
            if (sig.Numerator == sig.Denominator * 4 / 4 && sig.Denominator == 4) continue; // 4/4相当は出力不要
            double sixteenths = sig.Numerator * 16.0 / sig.Denominator;
            int skip = (int)Math.Round(16 - sixteenths);
            if (Math.Abs(sixteenths - Math.Round(sixteenths)) > 0.001)
                warnings.Add($"小節{sig.MeasureIndex}の拍子({sig.Numerator}/{sig.Denominator})は16分単位に" +
                             "正確に変換できないため、近似値で$barcutへ出力しますの。");
            if (skip is <= 0 or >= 16) continue; // 4/4相当または不正値は出力不要
            barcutEntries.Add((sig.MeasureIndex, skip));
        }

        // --- $frame(BPM変化点、小節境界にしか置けない) ---
        // 2026-08-23要望対応(BPMリンク): FUJI形式は各セグメントのstartFrame/endFrameを直接指定する
        // ため、区切り(小節境界)を細かく増やせば直線ランプを近似できる。ExpandLinkedBpmEventsで
        // リンク区間を選択した設置間隔の細かい離散ステップへ分解してから、既存の小節境界丸め処理へ渡す。
        var sourceBpmEvents = ValueEventSmoothing.ExpandLinkedBpmEvents(project.BpmEvents);
        if (sourceBpmEvents.Count == 0 || sourceBpmEvents[0].Tick != 0)
            throw new InvalidOperationException("BPMイベントの先頭がtick0にありません(不正なプロジェクトデータ)");

        var segmentMeasures = new List<int>();
        int? lastEmittedMeasure = null;
        foreach (var ev in sourceBpmEvents)
        {
            var (measure, tickInMeasure) = engine.TickToMeasurePosition(ev.Tick);
            if (tickInMeasure != 0)
            {
                if (!options.RoundMisalignedPositions)
                {
                    warnings.Add($"BPM変化(tick={ev.Tick}, BPM={ev.Bpm})は小節境界に乗らないため、削除しましたの。" +
                                 "直前のBPMのまま継続します。");
                    continue;
                }
                // 直前・直後どちらの小節境界に近いかで丸める
                long thisMeasureStart = engine.MeasureStartTick(measure);
                long nextMeasureStart = engine.MeasureStartTick(measure + 1);
                int roundedMeasure = (ev.Tick - thisMeasureStart) * 2 < (nextMeasureStart - thisMeasureStart) ? measure : measure + 1;
                warnings.Add($"BPM変化(tick={ev.Tick}, BPM={ev.Bpm})は小節境界に乗らないため、小節{roundedMeasure}へ" +
                             "丸めましたの。");
                measure = roundedMeasure;
            }
            if (lastEmittedMeasure == measure)
            {
                warnings.Add($"BPM変化(tick={ev.Tick}, BPM={ev.Bpm})は直前のBPM変化と同じ小節に丸められたため、" +
                             "削除しましたの。");
                continue;
            }
            segmentMeasures.Add(measure);
            lastEmittedMeasure = measure;
        }
        if (segmentMeasures.Count == 0)
            throw new InvalidOperationException("BPM変化点を1つも出力できませんでしたの(プロジェクトデータをご確認くださいまし)");

        var frameTokens = new List<string>();
        for (int i = 0; i < segmentMeasures.Count; i++)
        {
            int startM = segmentMeasures[i];
            int endM = i + 1 < segmentMeasures.Count ? segmentMeasures[i + 1] : measureCount;
            long startTick = engine.MeasureStartTick(startM);
            long endTick = engine.MeasureStartTick(endM);
            double startFrame = engine.TickToFrame(startTick);
            double endFrame = engine.TickToFrame(endTick);
            frameTokens.Add($"{startM}/{Math.Round(startFrame * 10)}/{Math.Round(endFrame * 10)}/1");
        }
        frameTokens.Add(measureCount.ToString(CultureInfo.InvariantCulture));

        // --- fine文字の決定(数字系のみ、2026-08-03方針)。残差が±9フレームを超える場合はnullを返す ---
        char? ResolveNumericFine(double frameDiff)
        {
            long rounded = (long)Math.Round(frameDiff, MidpointRounding.AwayFromZero);
            if (rounded == 0) return '0';
            if (rounded is >= 1 and <= 9) return (char)('0' + rounded);
            if (rounded is >= -9 and <= -1) return (char)('A' + (-rounded - 1));
            return null;
        }

        // 位置(measure基準のtick)をbasePp(16刻み)+fine文字へ分解する。戻り値はnull=完全に表現不能
        // (RoundMisalignedPositions=falseで打ち切られた場合)。
        (int BasePp, char Fine)? ResolvePosition(int measure, long tick, string label, List<string> localWarnings)
        {
            long measureStart = engine.MeasureStartTick(measure);
            double fraction = (tick - measureStart) / (double)TicksPerMeasureNominal;
            double ppContinuous = fraction * 256.0;
            int basePp = (int)Math.Round(ppContinuous / 16.0) * 16;
            basePp = Math.Clamp(basePp, 0, 240);

            double targetFrame = engine.TickToFrame(tick);
            long baseTick = measureStart + (long)Math.Round(basePp / 256.0 * TicksPerMeasureNominal);
            double baseFrame = engine.TickToFrame(baseTick);
            double frameDiff = targetFrame - baseFrame;

            var fine = ResolveNumericFine(frameDiff);
            if (fine is not null) return (basePp, fine.Value);

            if (!options.RoundMisalignedPositions)
            {
                localWarnings.Add($"{label}(小節{measure})はグリッドに乗らない位置(残差{frameDiff:F2}フレーム)のため、削除しましたの。");
                return null;
            }
            long clamped = Math.Clamp((long)Math.Round(frameDiff, MidpointRounding.AwayFromZero), -9, 9);
            char clampedFine = clamped == 0 ? '0' : clamped > 0 ? (char)('0' + clamped) : (char)('A' + (-clamped - 1));
            localWarnings.Add($"{label}(小節{measure})はグリッドに乗らない位置(残差{frameDiff:F2}フレーム)のため、" +
                              $"最も近い表現可能な位置(残差{clamped}フレーム)へ丸めましたの。");
            return (basePp, clampedFine);
        }

        // --- $score本体 ---
        var byMeasure = new SortedDictionary<int, List<string>>();
        void AddToken(int measure, string token)
        {
            if (!byMeasure.TryGetValue(measure, out var list)) byMeasure[measure] = list = [];
            list.Add(token);
        }

        for (int laneIdx = 0; laneIdx < template.Lanes.Count && laneIdx < tab.Lanes.Count; laneIdx++)
        {
            var laneDef = template.Lanes[laneIdx];
            int fujiLane = laneDef.EffectiveFujiLane;
            int laneDigit = fujiLane % 16;
            int laneExtNibble = fujiLane / 16;
            string laneDigitHex = laneDigit.ToString("X1", CultureInfo.InvariantCulture);
            if (laneExtNibble > 15)
            {
                warnings.Add($"レーン'{laneDef.LaneId}'(fujiLane={fujiLane})はFUJI形式で表現できないレーン番号のため、" +
                             "このレーンのノート・フリーズは出力しませんでしたの。");
                continue;
            }

            foreach (var tick in tab.Lanes[laneIdx].Notes)
            {
                var (measure, _) = engine.TickToMeasurePosition(tick);
                var resolved = ResolvePosition(measure, tick, $"'{laneDef.LaneId}'のノート", warnings);
                if (resolved is null) continue;
                int ppRaw = resolved.Value.BasePp | laneExtNibble;
                AddToken(measure, $"{ppRaw:X2}{laneDigitHex}{resolved.Value.Fine}");
            }

            foreach (var f in tab.Lanes[laneIdx].Freezes)
            {
                if (laneExtNibble > 1)
                {
                    warnings.Add($"'{laneDef.LaneId}'(fujiLane={fujiLane})のフリーズはFUJI形式のフリーズ表現" +
                                 "(レーン拡張は+16の1段のみ対応)を超えるため出力しませんでしたの。");
                    continue;
                }
                var (measure, _) = engine.TickToMeasurePosition(f.StartTick);
                var startResolved = ResolvePosition(measure, f.StartTick, $"'{laneDef.LaneId}'のフリーズ始点", warnings);
                if (startResolved is null) continue;

                long measureStart = engine.MeasureStartTick(measure);
                double endPpContinuous = (f.EndTick - measureStart) / (double)TicksPerMeasureNominal * 256.0;
                double durPpContinuous = endPpContinuous - startResolved.Value.BasePp;
                int durDigit = (int)Math.Round(durPpContinuous / 16.0);
                if (durDigit < 0 || durDigit > 999)
                {
                    if (!options.RoundMisalignedPositions)
                    {
                        warnings.Add($"'{laneDef.LaneId}'のフリーズ(小節{measure})は長さが表現可能範囲を超えるため、" +
                                     "削除しましたの。");
                        continue;
                    }
                    int clampedDur = Math.Clamp(durDigit, 0, 999);
                    warnings.Add($"'{laneDef.LaneId}'のフリーズ(小節{measure})は長さが表現可能範囲(0〜999)を超える" +
                                 $"({durDigit})ため、{clampedDur}へ丸めましたの(終点位置がズレますわ)。");
                    durDigit = clampedDur;
                }

                double durPpBase = startResolved.Value.BasePp + durDigit * 16.0;
                long durBaseTick = measureStart + (long)Math.Round(durPpBase / 256.0 * TicksPerMeasureNominal);
                double targetEndFrame = engine.TickToFrame(f.EndTick);
                double durBaseFrame = engine.TickToFrame(durBaseTick);
                double endFrameDiff = targetEndFrame - durBaseFrame;
                var endFine = ResolveNumericFine(endFrameDiff);
                if (endFine is null)
                {
                    if (!options.RoundMisalignedPositions)
                    {
                        warnings.Add($"'{laneDef.LaneId}'のフリーズ終点(小節{measure})はグリッドに乗らない位置" +
                                     $"(残差{endFrameDiff:F2}フレーム)のため、削除しましたの。");
                        continue;
                    }
                    long clamped = Math.Clamp((long)Math.Round(endFrameDiff, MidpointRounding.AwayFromZero), -9, 9);
                    endFine = clamped == 0 ? '0' : clamped > 0 ? (char)('0' + clamped) : (char)('A' + (-clamped - 1));
                    warnings.Add($"'{laneDef.LaneId}'のフリーズ終点(小節{measure})はグリッドに乗らない位置" +
                                 $"(残差{endFrameDiff:F2}フレーム)のため、最も近い表現可能な位置へ丸めましたの。");
                }

                int startPpRaw = startResolved.Value.BasePp | laneExtNibble;
                string marker = laneExtNibble == 1 ? "9" : "8";
                string head = $"{(startPpRaw & 0xF0) / 16:X1}{marker}{laneDigitHex}{startResolved.Value.Fine}";
                string tail = $"{durDigit:D3}{endFine.Value}";
                AddToken(measure, $"{head}-{tail}");
            }
        }

        // 2026-08-22不具合修正(SkbExporterと同種): speed/boostの「始点終点オートスムージング出力」
        // (ValueEvent.LinkGridDivisionによるリンク、2026-07-30要望対応)がFUJIエクスポートには
        // 反映されておらず、リンクした2点だけが出力され中間点が生成されていなかった。
        // dos.txt出力・プレイテスト・プレビューと同じくValueEventSmoothing.ExpandLinkedEventsを経由する。
        foreach (var e in ValueEventSmoothing.ExpandLinkedEvents(tab.SpeedEvents)) AddSpeedBoostToken(e, "400", "速度変化");
        foreach (var e in ValueEventSmoothing.ExpandLinkedEvents(tab.BoostEvents)) AddSpeedBoostToken(e, "410", "ブースト変化");

        void AddSpeedBoostToken(ValueEvent e, string kindCode, string label)
        {
            var (measure, _) = engine.TickToMeasurePosition(e.Tick);
            long measureStart = engine.MeasureStartTick(measure);
            double fraction = (e.Tick - measureStart) / (double)TicksPerMeasureNominal;
            int slot = (int)Math.Round(fraction * 16.0);
            bool outOfRange = slot is < 0 or > 15;
            int clampedSlot = Math.Clamp(slot, 0, 15);
            long snappedTick = measureStart + (long)Math.Round(clampedSlot / 16.0 * TicksPerMeasureNominal);
            if (outOfRange || snappedTick != e.Tick)
            {
                if (!options.RoundMisalignedPositions)
                {
                    warnings.Add($"{label}(小節{measure}, tick={e.Tick})はFUJI形式のグリッド(1/16小節刻み)に" +
                                 "乗らないため、削除しましたの。");
                    return;
                }
                warnings.Add($"{label}(小節{measure}, tick={e.Tick})はFUJI形式のグリッド(1/16小節刻み)に" +
                             "乗らないため、最も近い位置へ丸めましたの。");
            }
            long vvvv = (long)Math.Round(e.Value * 1000, MidpointRounding.AwayFromZero);
            if (vvvv is < 0 or > 9999)
            {
                warnings.Add($"{label}(小節{measure})の値({e.Value})はFUJI形式で表現可能な範囲を超えるため、" +
                             "出力できる範囲へクランプしましたの。");
                vvvv = Math.Clamp(vvvv, 0, 9999);
            }
            AddToken(measure, $"{clampedSlot:X1}{kindCode}-{vvvv:D4}");
        }

        // --- 組み立て ---
        var sb = new StringBuilder();
        sb.Append("$frame=").Append(string.Join(",", frameTokens)).Append('\n');
        if (barcutEntries.Count > 0)
            sb.Append("$barcut=").Append(string.Join(",", barcutEntries.Select(b => $"{b.Measure}/{b.Skip}"))).Append('\n');
        sb.Append("$header=\n");
        sb.Append($"|difData={tab.KeyTypeId},{tab.DifficultyName},{Num(tab.InitialSpeed)}|\n");
        sb.Append($"|blankFrame={project.BlankFrame}|\n");
        sb.Append("$score=\n");
        foreach (var (measure, tokens) in byMeasure)
            sb.Append(measure.ToString("D4", CultureInfo.InvariantCulture)).Append(':').Append(string.Join(",", tokens)).Append('\n');

        return new FujiExportResult { Text = sb.ToString(), Warnings = warnings };
    }
}
