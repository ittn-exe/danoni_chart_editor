using System.Globalization;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Analysis;

/// <summary>STREAM計算結果(analyze.jsの`calcStream`戻り値)。</summary>
public readonly record struct IttnStreamResult(double Val, int TotalNotes, double Apm);

/// <summary>VOLTAGE計算結果(analyze.jsの`calcVoltage`戻り値のうちhighlightsを除いた部分)。</summary>
public readonly record struct IttnVoltageResult(double Val, double PeakApm, double MaxSectionNotes);

/// <summary>CHORD計算結果(analyze.jsの`calcChord`戻り値のうちchordCounts/highlightsを除いた部分)。</summary>
public readonly record struct IttnChordResult(double Val, double AllChords, double Cpm);

/// <summary>SOF-LAN計算結果(analyze.jsの`calcSoflan`戻り値のうちlog/highlightsを除いた部分)。</summary>
public readonly record struct IttnSoflanResult(double Val, double SofTotal, double SofApm);

/// <summary>FREEZE計算結果(analyze.jsの`calcFreeze`戻り値のうちhighlightsを除いた部分)。</summary>
public readonly record struct IttnFreezeResult(double Val, int FrzArrCnt, double FrzFrmCnt, double FrzNtsCnt, int MaxActive, double FrzApm);

/// <summary>ONIGIRI計算結果(analyze.jsの`calcOnigiri`戻り値)。</summary>
public readonly record struct IttnOnigiriResult(double Val, double OniApm, int OniCnt);

/// <summary>Total値合成結果(analyze.jsの`calcTotalRating`戻り値)。</summary>
public readonly record struct IttnTotalRatingResult(double TotalLevel, double ToolScaleLevel, double AccFactor, double Bonus);

/// <summary>
/// 1譜面(1難易度タブ)分の解析結果。analyze.jsの`calculateFinal`が返すオブジェクトの
/// うち、baseRating/totalRating/toolScaleRating・レーダー6軸・JACK/ALT/MOV・
/// デバッグ表示に有用な内訳(raw)を保持する。UI専用のhighlights/segDebug/discardは含まない
/// (スコープ外。docs/progress_and_tbd_2026-07-24_add.md参照)。
/// フィールド名はJS版のプロパティ名にできるだけ対応させている(各フィールドのdocコメント参照)。
/// </summary>
public sealed record IttnAnalysisResult(
    /// <summary>analyze.jsの`baseRating`</summary>
    double BaseRating,
    /// <summary>analyze.jsの`totalRating`(= totalRating.TotalLevel)</summary>
    double TotalRating,
    /// <summary>analyze.jsの`toolScaleRating`(= totalRating.ToolScaleLevel)</summary>
    double ToolScaleRating,
    double Stream,
    double Voltage,
    double Chord,
    double Freeze,
    double Soflan,
    double Onigiri,
    double Jack,
    /// <summary>analyze.jsの`raw.altVAL`</summary>
    double Alt,
    /// <summary>analyze.jsの`raw.movVAL`</summary>
    double Mov,
    IttnAnalysisRawInfo Raw);

/// <summary>calculateFinalの`raw`オブジェクトに対応するデバッグ・内訳情報。</summary>
public sealed record IttnAnalysisRawInfo(
    int TotalNotes,
    double PlayFrame,
    double Apm,
    double PeakApm,
    double FrzApm,
    double ChordAll,
    double Cpm,
    double SofTotal,
    double SofApm,
    double OniApm,
    int OniCnt,
    double AltPeakPm,
    double AltTotalPm,
    double AltScore,
    double MovPeakPm,
    double MovTotalPm,
    double MovScore,
    int MovCount,
    int MovExcursions,
    int MovStretches,
    int MovFingers,
    double JackScore,
    double JackJpm,
    int JackMaxCombo,
    double AccFactor,
    double TotalBonus,
    double MustRate,
    double AllowMisses,
    double RealRcv,
    double RealDmg);

/// <summary>
/// このファイルは analyzer_and_viewer/analyze.js (2026-07-25時点) の共通ユーティリティ・
/// レーダー6軸(STREAM/VOLTAGE/CHORD/SOF-LAN/FREEZE/ONIGIRI)・Base/Total合成・
/// 統括関数calculateFinalを移植したもの。JACK/ALT/MOVはそれぞれ
/// IttnAnalyzerJack/IttnAnalyzerAlt/IttnAnalyzerMovに分離している。
/// 今後analyze.js側を更新した場合、このファイルへの片方向バックポート要否を確認すること
/// (docs/progress_and_tbd_2026-07-24_add.md §1-1「片方向運用」)。
///
/// ■忠実移植パートのスコープ外事項(analyzeDiscard/捨て予算解析、highlights、
/// BPM/tick基準へのスライディング窓置き換え)は本ファイルに含めていない
/// (docs/progress_and_tbd_2026-07-24_add.md §1-2/§2参照)。
/// </summary>
public static class IttnAnalyzer
{
    // =====================================================================
    // レーダー6軸
    // =====================================================================

    /// <summary>■STREAM: 譜面全体の平均密度(analyze.jsの`calcStream`)。</summary>
    public static IttnStreamResult CalcStream(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame, bool isDoubleFrz)
    {
        int totalNotes = 0;
        foreach (var e in timeline)
        {
            if (e.Type == IttnTimelineEventType.Normal) totalNotes++;
            else if (e.Type == IttnTimelineEventType.FreezeStart) totalNotes += isDoubleFrz ? 2 : 1;
        }
        double apm = totalNotes / (playFrame / 3600);
        return new IttnStreamResult(IttnAnalyzerCommon.FinalizeRadarValue(apm, IttnAnalyzerConfig.Stream.ApmAt100, IttnAnalyzerConfig.Stream.ApmAt200), totalNotes, apm);
    }

    /// <summary>■VOLTAGE: 局所最高密度(小節基準窓をスライドさせた最大値、analyze.jsの`calcVoltage`)。
    /// JS版の引数firstFrame/lastFrameは関数本体で未使用のため(highlights算出専用だがhighlights自体が
    /// スコープ外)、このポートでは省略している。
    /// 2026-07-25 BPM強化パート: 窓幅をSECTION_SIZE(240F固定)からSectionSizeMeasures(2小節、[仮])に
    /// 置き換え。窓境界の判定はtick基準(<paramref name="engine"/>で拍子を参照)で行うが、正規化
    /// (per-minute換算)は「実際に採用された窓の実測フレーム経過時間」を使う。理由: 元のSECTION_SIZEは
    /// 「窓の実フレーム幅」そのものが除数だった。小節基準に変えると窓のtick幅は一定でも実フレーム幅は
    /// テンポで変動するため、除数もその窓で実際に経過した実フレーム時間に置き換えるのが数量的に一貫する
    /// (テンポが速いほど同じ2小節でも実フレーム幅は短くなり、結果としてAPMがより敏感にBPMを反映する)。</summary>
    public static IttnVoltageResult CalcVoltage(IReadOnlyList<IttnTimelineEvent> timeline, bool isDoubleFrz, TimingEngine engine)
    {
        var notes = new List<(long Time, long Tick, double Weight)>();
        foreach (var e in timeline)
        {
            if (e.Type == IttnTimelineEventType.Normal) notes.Add((e.Time, e.Tick, 1));
            else if (e.Type == IttnTimelineEventType.FreezeStart) notes.Add((e.Time, e.Tick, isDoubleFrz ? 2 : 1));
        }
        if (notes.Count == 0) return new IttnVoltageResult(0, 0, 0);

        double maxW = 0;
        double wSum = 0;
        int end = 0;
        long bestStartFrame = notes[0].Time, bestEndFrame = notes[0].Time;
        for (int start = 0; start < notes.Count; start++)
        {
            long tickEnd = notes[start].Tick + IttnAnalyzerCommon.MeasureTicks(engine, notes[start].Tick, IttnAnalyzerConfig.Voltage.SectionSizeMeasures);
            while (end < notes.Count && notes[end].Tick < tickEnd) { wSum += notes[end].Weight; end++; }
            if (wSum > maxW)
            {
                maxW = wSum;
                bestStartFrame = notes[start].Time;
                bestEndFrame = notes[end - 1].Time; // end>startが保証される(自区間開始tick自身は必ず窓に入るため)
            }
            wSum -= notes[start].Weight;
        }

        double windowFrameSpan = Math.Max(1, bestEndFrame - bestStartFrame);
        double maxPeakApm = maxW * 3600 / windowFrameSpan;
        return new IttnVoltageResult(Math.Max(0, IttnAnalyzerCommon.FinalizeRadarValue(maxPeakApm, IttnAnalyzerConfig.Voltage.ApmAt100, IttnAnalyzerConfig.Voltage.ApmAt200)), maxPeakApm, maxW);
    }

    /// <summary>■CHORD: 同時押み。押し数が増えるほど1回あたりの重みが増加(analyze.jsの`calcChord`)。</summary>
    public static IttnChordResult CalcChord(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame)
    {
        var frameMap = new Dictionary<long, int>();
        foreach (var e in timeline)
        {
            if (e.Type is IttnTimelineEventType.SpeedChange or IttnTimelineEventType.FreezeEnd) continue;
            frameMap[e.Time] = frameMap.GetValueOrDefault(e.Time) + 1;
        }
        double chordAll = 0;
        foreach (var count in frameMap.Values)
        {
            if (count >= 2)
            {
                double weight = IttnAnalyzerConfig.Chord.BaseWeight, cur = IttnAnalyzerConfig.Chord.BaseIncrement;
                for (int n = 3; n <= count; n++) { cur += IttnAnalyzerConfig.Chord.LoopIncrement; weight += cur; }
                chordAll += weight;
            }
        }
        double cpm = chordAll / (playFrame / 3600);
        return new IttnChordResult(IttnAnalyzerCommon.FinalizeRadarValue(cpm, IttnAnalyzerConfig.Chord.CpmAt100, IttnAnalyzerConfig.Chord.CpmAt200), chordAll, cpm);
    }

    /// <summary>■SOF-LAN: 変速難。2種類の負荷を合算する(analyze.jsの`calcSoflan`)。
    ///   A) 非等速区間でノーツを叩き続ける常時負荷(速度偏差×ノーツ数)
    ///   B) 変速直後(TIME_WINDOW_B内)の読み直し負荷(変速幅×直後ノーツ数)
    /// 2026-07-25 BPM強化パート: B)の窓をTIME_WINDOW_B(120F固定)からTimeWindowBMeasures(1小節、[仮])に
    /// 置き換え。窓境界はtick基準(<paramref name="engine"/>で変速イベント時点の拍子を参照)で判定する。
    /// noteCountBは「窓内のノーツ数」という無次元カウントであり、A)と同様その後の重み付けに
    /// フレーム除数を使わないため(sofTotalはそのままAPM換算=playFrameで割るだけ)、この窓は
    /// per-minute正規化を伴わない(=VOLTAGE/ALT/MOVのような実測フレーム幅への置き換えは不要)。</summary>
    public static IttnSoflanResult CalcSoflan(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame, bool isDoubleFrz, TimingEngine engine)
    {
        double sofTotal = 0, currentSpeed = 1.0;

        for (int i = 0; i < timeline.Count; i++)
        {
            var evt = timeline[i];
            if (evt.Type == IttnTimelineEventType.SpeedChange)
            {
                if (evt.Value is not { } val || double.IsNaN(val)) continue;
                double oldSpeed = currentSpeed;
                currentSpeed = val;
                double noteCountB = 0;
                long targetEndTick = evt.Tick + IttnAnalyzerCommon.MeasureTicks(engine, evt.Tick, IttnAnalyzerConfig.Soflan.TimeWindowBMeasures);
                double speedDiff = Math.Abs(oldSpeed - currentSpeed);
                for (int j = i + 1; j < timeline.Count; j++)
                {
                    var nxt = timeline[j];
                    if (nxt.Type == IttnTimelineEventType.SpeedChange) break;
                    if (nxt.Tick > targetEndTick) break;
                    if (nxt.Type == IttnTimelineEventType.Normal) noteCountB++;
                    else if (nxt.Type == IttnTimelineEventType.FreezeStart) noteCountB += isDoubleFrz ? 2 : 1;
                }
                sofTotal += noteCountB * (speedDiff * IttnAnalyzerConfig.Soflan.CoeffB);
                continue;
            }
            if (evt.Type is IttnTimelineEventType.Normal or IttnTimelineEventType.FreezeStart)
            {
                if (currentSpeed == 1.0) continue;
                double noteWeight = (evt.Type == IttnTimelineEventType.FreezeStart && isDoubleFrz) ? 2 : 1;
                double dev = currentSpeed > 1.0
                    ? Math.Min(currentSpeed, IttnAnalyzerConfig.Soflan.SpeedCap) - 1.0
                    : Math.Abs(currentSpeed - 1.0) * IttnAnalyzerConfig.Soflan.SlowMult;
                sofTotal += noteWeight * (dev * IttnAnalyzerConfig.Soflan.CoeffA);
            }
        }
        double sofApm = sofTotal / (playFrame / 3600);
        return new IttnSoflanResult(IttnAnalyzerCommon.FinalizeRadarValue(sofApm, IttnAnalyzerConfig.Soflan.SofAt100, IttnAnalyzerConfig.Soflan.SofAt200), sofTotal, sofApm);
    }

    /// <summary>■FREEZE: フリーズアロー難。保持しながら他を処理する負荷を積算する(analyze.jsの`calcFreeze`)。
    /// v2修正: 旧版はfreezeStart時に保持ペナルティが二重計上されていたバグを修正済みの版
    /// (analyze.js本体のコメント参照)。</summary>
    public static IttnFreezeResult CalcFreeze(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame, bool isDoubleFrz)
    {
        int frzArrCnt = 0;
        double frzFrmCnt = 0, frzTotalScore = 0;
        var activeFreezes = new Dictionary<string, long>();
        int maxActiveCount = 0;

        foreach (var evt in timeline)
        {
            if (evt.Type == IttnTimelineEventType.SpeedChange) continue;
            int activeCount = activeFreezes.Count;
            if (activeCount > maxActiveCount) maxActiveCount = activeCount;

            if (evt.Type == IttnTimelineEventType.FreezeStart)
            {
                frzArrCnt++;
                // 保持中の各フリーズに対して: 新たな始点を処理する負荷
                foreach (var _ in activeFreezes)
                {
                    frzTotalScore += IttnAnalyzerConfig.Freeze.HoldLoad;
                    if (isDoubleFrz) frzTotalScore += IttnAnalyzerConfig.Freeze.HoldLoad;
                }
                // 始点判定ON時: この始点自体の追加負荷
                if (isDoubleFrz) frzTotalScore += IttnAnalyzerConfig.Freeze.HoldLoad;
                activeFreezes[evt.Lane!] = evt.Time;
            }
            else if (evt.Type == IttnTimelineEventType.FreezeEnd)
            {
                if (activeFreezes.TryGetValue(evt.Lane!, out var startTime))
                {
                    frzFrmCnt += evt.Time - startTime;
                    activeFreezes.Remove(evt.Lane!);
                }
            }
            else if (evt.Type == IttnTimelineEventType.Normal)
            {
                foreach (var _ in activeFreezes) frzTotalScore += IttnAnalyzerConfig.Freeze.HoldLoad;
            }
        }
        frzTotalScore += frzArrCnt * IttnAnalyzerConfig.Freeze.ArrowBonus;
        double frzApm = frzTotalScore / (playFrame / 3600);
        return new IttnFreezeResult(IttnAnalyzerCommon.FinalizeRadarValue(frzApm, IttnAnalyzerConfig.Freeze.FpmAt100, IttnAnalyzerConfig.Freeze.FpmAt200),
            frzArrCnt, frzFrmCnt, frzTotalScore, maxActiveCount, frzApm);
    }

    /// <summary>■ONIGIRI: おにぎりレーンの密度(レーン数で正規化、analyze.jsの`calcOnigiri`)。
    /// JS版はKEY_MAP側のisONIGIRIフラグでオニレーンを特定するが、当エディタ版はKeyTemplateの
    /// <see cref="LaneDef.IsOnigiri"/>を直接使う(両者の判定基準はnoteGraphic==="onigiri"で完全に一致、
    /// key_map.jsonの全キー種で確認済み)ため、呼び出し側でオニレーンID集合を渡す形にしている。</summary>
    public static IttnOnigiriResult CalcOnigiri(IReadOnlyList<IttnTimelineEvent> timeline, double playFrame,
        IReadOnlyCollection<string> oniLaneIds, bool isDoubleFrz)
    {
        if (oniLaneIds.Count == 0) return new IttnOnigiriResult(0, 0, 0);
        var oniLaneSet = oniLaneIds as ISet<string> ?? new HashSet<string>(oniLaneIds);

        int rawOniCnt = 0;
        foreach (var e in timeline)
        {
            if (e.Lane is not null && oniLaneSet.Contains(e.Lane))
            {
                if (e.Type == IttnTimelineEventType.Normal) rawOniCnt++;
                else if (e.Type == IttnTimelineEventType.FreezeStart) rawOniCnt += isDoubleFrz ? 2 : 1;
            }
        }
        if (rawOniCnt == 0) return new IttnOnigiriResult(0, 0, 0);
        double correctedOniCnt = (double)rawOniCnt / oniLaneSet.Count;
        double oniApm = correctedOniCnt / (playFrame / 3600);
        return new IttnOnigiriResult(IttnAnalyzerCommon.FinalizeRadarValue(oniApm, IttnAnalyzerConfig.Onigiri.ApmAt100, IttnAnalyzerConfig.Onigiri.ApmAt200), oniApm, rawOniCnt);
    }

    // =====================================================================
    // Base / Total 合成
    // =====================================================================

    /// <summary>■Base値: レーダー要素からの合成(analyze.jsの`calcBaseRating`)。
    /// 最大要素が難易度の主成分。他要素は「最大値との比率×SUB_BONUS_RATE」で逓減ボーナスとして寄与する。</summary>
    public static double CalcBaseRating(IReadOnlyList<double> activeElements)
    {
        if (activeElements.Count == 0) return 0;
        double maxVal = activeElements.Max();
        int maxIdx = -1;
        for (int i = 0; i < activeElements.Count; i++) { if (activeElements[i] == maxVal) { maxIdx = i; break; } }

        double totalBonus = 0;
        for (int idx = 0; idx < activeElements.Count; idx++)
        {
            if (idx == maxIdx) continue;
            double val = activeElements[idx];
            double ratio = maxVal > 0 ? val / maxVal : 0;
            totalBonus += val * ratio * IttnAnalyzerConfig.Total.SubBonusRate;
        }
        return (maxVal + totalBonus) / IttnAnalyzerConfig.Total.BaseScale;
    }

    /// <summary>■飽和ボーナス関数: g(v) = K × v / (v + x0)(analyze.jsの`satBonus`)。</summary>
    public static double SatBonus(double val, double k, double x0) => val > 0 ? (k * val) / (val + x0) : 0;

    /// <summary>■Total値: Base × Acc係数 × (1 + 特殊要素ボーナス)(analyze.jsの`calcTotalRating`)。</summary>
    public static IttnTotalRatingResult CalcTotalRating(double baseRating, double mustRate, double altVal, double movVal, double jackVal)
    {
        double joltFactor = 1 + (mustRate - IttnAnalyzerConfig.Total.BaseMustRate) * IttnAnalyzerConfig.Total.GaugeScale;
        double accFactor = Math.Min(IttnAnalyzerConfig.Total.AccMax, Math.Max(IttnAnalyzerConfig.Total.AccMin, joltFactor));

        double bonus = SatBonus(altVal, IttnAnalyzerConfig.Total.KAlt, IttnAnalyzerConfig.Total.SatX0) + SatBonus(movVal, IttnAnalyzerConfig.Total.KMov, IttnAnalyzerConfig.Total.SatX0) + SatBonus(jackVal, IttnAnalyzerConfig.Total.KJack, IttnAnalyzerConfig.Total.SatX0);

        double finalRating = baseRating * accFactor * (1 + bonus);
        return new IttnTotalRatingResult(finalRating, finalRating / IttnAnalyzerConfig.Total.ToolScaleDiv, accFactor, bonus);
    }

    // =====================================================================
    // 統括
    // =====================================================================

    /// <summary>
    /// ■統括: 1譜面の全指標を計算して結果オブジェクトを返す(analyze.jsの`calculateFinal`の移植)。
    /// </summary>
    /// <param name="timeline">解析対象のタイムライン(<see cref="IttnAnalyzerTimelineBuilder.Build"/>で構築)。</param>
    /// <param name="keyTypeId">キー種ID(例: "7"、"11L")。ONIGIRI/ALT/MOVのkey_map参照に使う。</param>
    /// <param name="oniLaneIds">おにぎりレーンのLaneId集合(空ならONIGIRI軸は0のまま=hasOnigiriFeature相当false)。</param>
    /// <param name="isDoubleFrz">frzStartjdgUse(始点判定)の有無。</param>
    /// <param name="gaugeBorder">ノルマ設定("x"=ライフ制、それ以外は境界%の生値文字列)。</param>
    /// <param name="gaugeRecoveryRaw">回復量の生値(dos.txtのgaugeXXXにそのまま書く値)。</param>
    /// <param name="gaugeDamageRaw">ダメージ量の生値。</param>
    /// <param name="gaugeInitLifePercent">初期ライフ(%)。</param>
    /// <param name="maxLifeVal">ライフ最大値(本体既定1000)。</param>
    /// <param name="engine">タイミングエンジン(2026-07-25 BPM強化パートで追加。VOLTAGE/ALT/MOV/SOFLANの
    /// 小節・拍基準スライディング窓の境界判定に使う。STREAM/CHORD/FREEZE/ONIGIRI/JACKはこれを使わない)。</param>
    public static IttnAnalysisResult? CalculateFinal(
        IReadOnlyList<IttnTimelineEvent> timeline,
        string keyTypeId,
        IReadOnlyCollection<string> oniLaneIds,
        bool isDoubleFrz,
        string gaugeBorder,
        double gaugeRecoveryRaw,
        double gaugeDamageRaw,
        double gaugeInitLifePercent,
        double maxLifeVal,
        TimingEngine engine)
    {
        if (timeline.Count == 0) return null;
        long firstFrame = timeline[0].Time;
        long lastFrame = timeline[^1].Time;
        double playFrame = Math.Max(1, lastFrame - firstFrame);

        var infoStream = CalcStream(timeline, playFrame, isDoubleFrz);
        if (infoStream.TotalNotes == 0) return null; // speedChangeのみの譜面等を弾く(0除算防止)
        var infoVoltage = CalcVoltage(timeline, isDoubleFrz, engine);
        var infoChord = CalcChord(timeline, playFrame);
        var infoSoflan = CalcSoflan(timeline, playFrame, isDoubleFrz, engine);
        var infoFreeze = CalcFreeze(timeline, playFrame, isDoubleFrz);

        bool hasOnigiriFeature = oniLaneIds.Count > 0;
        double valOnigiri = 0, oniApm = 0;
        int oniCnt = 0;
        if (hasOnigiriFeature)
        {
            var info = CalcOnigiri(timeline, playFrame, oniLaneIds, isDoubleFrz);
            valOnigiri = info.Val;
            oniApm = info.OniApm;
            oniCnt = info.OniCnt;
        }

        var activeElements = new List<double> { infoStream.Val, infoVoltage.Val, infoChord.Val, infoFreeze.Val, infoSoflan.Val };
        if (hasOnigiriFeature) activeElements.Add(valOnigiri);
        double baseRating = CalcBaseRating(activeElements);

        // --- ゲージ計算: クリアに必要な最低回復数から要求精度を求める ---
        // 2026-07-25 移植方針: analyze.jsのgetGaugeSettings(customGauge/gaugeXXXの複雑な解決チェーン)は
        // 移植せず、DifficultyTab.DifDataExtraが「rawSettings[3..6]が直接埋まっている」場合の
        // 主経路(getGaugeSettingsの最初のif分岐)とみなして呼び出し側で解決してから渡す方式にした
        // (この経路ではJS版でもcurrentGauge[1]がnull=isLifeSystemはborder==='x'のみで決まるため、
        // 呼び出し側にgaugeForV相当のパラメータは不要)。customGauge/gaugeXXXチェーンは今回のスコープ外。
        double maxL = maxLifeVal > 0 ? maxLifeVal : 1000;
        bool isLifeSystem = gaugeBorder == "x";
        double startRate = double.IsNaN(gaugeInitLifePercent) ? 25 : gaugeInitLifePercent;
        double initLife = GaugeCalculator.PercentToReal(startRate, maxL);

        var mode = isLifeSystem ? GaugeCalculator.CalcMode.Fix : GaugeCalculator.CalcMode.Vary;
        double realRcv = GaugeCalculator.ToRealValue(gaugeRecoveryRaw, mode, maxL, infoStream.TotalNotes);
        double realDmg = GaugeCalculator.ToRealValue(gaugeDamageRaw, mode, maxL, infoStream.TotalNotes);

        double lifeBorder = gaugeBorder == "x" ? 0
            : (double.TryParse(gaugeBorder, NumberStyles.Float, CultureInfo.InvariantCulture, out var borderNum) ? borderNum * 10 : double.NaN);

        var altLevel = IttnAnalyzerAlt.Calculate(timeline, keyTypeId, engine);
        var movLevel = IttnAnalyzerMov.Calculate(timeline, keyTypeId, engine);
        var infoJack = IttnAnalyzerJack.Calculate(timeline, playFrame);

        // GaugeCalculator.GetAccuracyは本体のgetAccuracy(=analyze.jsのjudgeRateSeed/minRecovery計算)と
        // 数式的に同一(border-init+dmg*allCntの計算がmax(...,0)でガードされる点のみ異なる。
        // 通常の非自明なゲージ設定ではこの差異は生じない)。
        var acc = GaugeCalculator.GetAccuracy(lifeBorder, realRcv, realDmg, initLife, infoStream.TotalNotes);
        double mustRate = acc.IsValid ? IttnAnalyzerCommon.Clamp(acc.RatePercent, 0, 100) : 0;
        double allowMisses = acc.IsValid ? Math.Max(0, acc.AllowableMiss) : 0;

        var totalRating = CalcTotalRating(baseRating, mustRate, altLevel.ValAlt, movLevel.ValMov, infoJack.Val);

        return new IttnAnalysisResult(
            baseRating,
            totalRating.TotalLevel,
            totalRating.ToolScaleLevel,
            infoStream.Val,
            infoVoltage.Val,
            infoChord.Val,
            infoFreeze.Val,
            infoSoflan.Val,
            valOnigiri,
            infoJack.Val,
            altLevel.ValAlt,
            movLevel.ValMov,
            new IttnAnalysisRawInfo(
                infoStream.TotalNotes,
                playFrame,
                infoStream.Apm,
                infoVoltage.PeakApm,
                infoFreeze.FrzApm,
                infoChord.AllChords,
                infoChord.Cpm,
                infoSoflan.SofTotal,
                infoSoflan.SofApm,
                oniApm,
                oniCnt,
                altLevel.AltPeakPm,
                altLevel.AltTotalPm,
                altLevel.TotalAltScore,
                movLevel.MovPeakPm,
                movLevel.MovTotalPm,
                movLevel.TotalMovScore,
                movLevel.MoveCount,
                movLevel.ExcursionCount,
                movLevel.StretchCount,
                movLevel.FingerCount,
                infoJack.TotalJackScore,
                infoJack.Jpm,
                infoJack.MaxCombo,
                totalRating.AccFactor,
                totalRating.Bonus,
                mustRate,
                allowMisses,
                realRcv,
                realDmg));
    }

    /// <summary>
    /// エディタのネイティブモデル(ChartProject/DifficultyTab)から直接解析するエントリポイント。
    /// タイムライン構築(<see cref="IttnAnalyzerTimelineBuilder"/>)・ゲージ設定解決
    /// (<see cref="DifficultyTab.DifDataExtra"/>)・maxLifeVal/frzStartjdgUseの取得
    /// (<see cref="ChartProject.ExtraHeaders"/>)をまとめて行う。
    /// </summary>
    public static IttnAnalysisResult? Analyze(ChartProject project, DifficultyTab tab, KeyTemplate template)
    {
        var engine = project.CreateTimingEngine();
        var timeline = IttnAnalyzerTimelineBuilder.Build(tab, template, engine);

        var oniLaneIds = template.Lanes.Where(l => l.IsOnigiri).Select(l => l.LaneId).ToList();

        bool isDoubleFrz = project.ExtraHeaders.TryGetValue("frzStartjdgUse", out var frzFlag) && frzFlag == "true";
        double maxLifeVal = project.ExtraHeaders.TryGetValue("maxLifeVal", out var mlv)
            && double.TryParse(mlv, NumberStyles.Float, CultureInfo.InvariantCulture, out var mlvVal)
            ? mlvVal : 1000;

        var (border, rcv, dmg, initPct) = ResolveGaugeSettings(tab);

        return CalculateFinal(timeline, tab.KeyTypeId, oniLaneIds, isDoubleFrz, border, rcv, dmg, initPct, maxLifeVal, engine);
    }

    /// <summary>
    /// DifDataExtra("border,recovery,damage,initLife%"形式)からゲージ設定4項目を解決する。
    /// analyze.jsのgetGaugeSettingsの最初のif分岐(rawSettings[3]が非空の場合)に対応する
    /// (customGauge/gaugeXXXの解決チェーンは移植していない。上記IttnAnalyzer.CalculateFinalの
    /// コメント参照)。未設定時はgetGaugeSettingsの最終フォールバックと同じ既定値['x',6,40,25]を使う。
    /// </summary>
    private static (string Border, double Rcv, double Dmg, double InitPct) ResolveGaugeSettings(DifficultyTab tab)
    {
        if (!string.IsNullOrWhiteSpace(tab.DifDataExtra))
        {
            var parts = tab.DifDataExtra.Split(',');
            string border = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : "x";
            double rcv = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 6;
            double dmg = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 40;
            double initPct = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var i) ? i : 25;
            return (border, rcv, dmg, initPct);
        }
        return ("x", 6, 40, 25);
    }
}
