using DanoniEditor.Core.Analysis;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Analysis;

/// <summary>
/// ITTNアナライザー(難易度解析、忠実移植パート)の検証(2026-07-25)。
/// 期待値はanalyzer_and_viewer/analyze.jsの計算式を手計算で追って求めたもの
/// (JS版実行結果との突き合わせは別途AnalyzeEngine.csを使ったクロスバリデーションで行う)。
/// </summary>
public class IttnAnalyzerTests
{
    // =====================================================================
    // finalizeRadarValue(共通ユーティリティ)
    // =====================================================================

    [Theory]
    [InlineData(0, 0)]          // rawScore<=0 → 0
    [InlineData(50, 50)]        // 100点まで線形(base100=100)
    [InlineData(100, 100)]      // ちょうどbase100
    [InlineData(200, 200)]      // ちょうどbase200
    [InlineData(250, 225)]      // 200超のブレーキ(閾値200/除数2 + 閾値0/除数1)
    [InlineData(300, 250)]      // 同上、境界値
    [InlineData(500, 285)]      // 400/300/200/0の全ブレーキ帯を通過
    public void FinalizeRadarValue_PiecewiseMapping(double rawScore, double expected)
    {
        double actual = IttnAnalyzerCommon.FinalizeRadarValue(rawScore, base100: 100, base200: 200);
        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void FinalizeRadarValue_InvalidBase_ReturnsZero()
    {
        Assert.Equal(0, IttnAnalyzerCommon.FinalizeRadarValue(50, 0, 200));
        Assert.Equal(0, IttnAnalyzerCommon.FinalizeRadarValue(50, 100, 100)); // base200<=base100
    }

    // =====================================================================
    // レーダー6軸(小規模な合成タイムラインでの単体検証)
    // =====================================================================

    [Fact]
    public void CalcStream_TwoNotes_ComputesApmAndVal()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.Normal, "a"),
            new(600, 600, IttnTimelineEventType.Normal, "b"),
        };
        var result = IttnAnalyzer.CalcStream(timeline, playFrame: 600, isDoubleFrz: false);

        Assert.Equal(2, result.TotalNotes);
        Assert.Equal(12.0, result.Apm, precision: 6); // 2 / (600/3600) = 12
        Assert.Equal(IttnAnalyzerCommon.FinalizeRadarValue(12.0, 540, 1151), result.Val, precision: 6);
    }

    [Fact]
    public void CalcStream_FreezeStartCountsDoubleWhenIsDoubleFrz()
    {
        var timeline = new List<IttnTimelineEvent> { new(0, 0, IttnTimelineEventType.FreezeStart, "a") };
        var result = IttnAnalyzer.CalcStream(timeline, playFrame: 3600, isDoubleFrz: true);
        Assert.Equal(2, result.TotalNotes);
    }

    // =====================================================================
    // VOLTAGE(2026-07-25 BPM強化パート: SECTION_SIZEを2小節のtick窓へ置き換え)
    // =====================================================================

    /// <summary>BPM150(1拍=1680tick→24F、1小節(4/4)=96F)のテスト用エンジン。
    /// ユーザー承認済みの換算基準(240F≈2.5小節→2小節、他窓も同基準)に合わせている。</summary>
    private static TimingEngine Bpm150Engine() => new(0, [new BpmEvent(0, 150)]);

    private static TimingEngine BpmEngine(double bpm) => new(0, [new BpmEvent(0, bpm)]);

    private static long RoundFrame(double frame) => (long)Math.Round(frame, MidpointRounding.AwayFromZero);

    [Fact]
    public void CalcVoltage_NotesWithinWindow_TakesMaxSectionWeight()
    {
        // BPM150: 2小節(SECTION_SIZE_MEASURES)=8拍=8*1680=13440tick。
        // 3ノーツ(tick 0, 3500, 7000 = 概ね旧テストのframe 0/50/100相当)は全てこの窓に収まる。
        var engine = Bpm150Engine();
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.Normal, "a"),
            new(50, 3500, IttnTimelineEventType.Normal, "b"),
            new(100, 7000, IttnTimelineEventType.Normal, "c"),
        };
        var result = IttnAnalyzer.CalcVoltage(timeline, isDoubleFrz: false, engine);

        Assert.Equal(3, result.MaxSectionNotes);
        // 正規化は「実際に採用された窓の実測フレーム経過時間」(bestEnd-bestStart = 100-0 = 100F)を使う
        // (SECTION_SIZE固定240Fの代わり。IttnAnalyzer.CalcVoltageのdocコメント参照)。
        double expectedPeakApm = 3 * 3600 / 100.0;
        Assert.Equal(expectedPeakApm, result.PeakApm, precision: 6);
    }

    [Fact]
    public void CalcVoltage_SameTickPattern_HigherBpmYieldsHigherPeakApm()
    {
        // BPM強化の核心: 同じ「tick空間上のノーツ配置」(=同じ楽曲的パターン)でも、テンポが速いほど
        // 実時間あたりの密度は高くなるはず。VOLTAGEの窓が小節(tick)基準になったことで、
        // ノーツのグルーピング自体はBPMに依存しないが、per-minute正規化に使う実測フレーム幅は
        // BPMに反比例して縮むため、結果としてPeakApmはBPMに比例して増加する。
        var lowBpm = BpmEngine(100);
        var highBpm = BpmEngine(200);

        List<IttnTimelineEvent> BuildTimeline(TimingEngine engine)
        {
            long[] ticks = [0, 1680, 3360]; // 0拍, 1拍, 2拍(2小節=8拍の窓に確実に収まる)
            return ticks.Select(t => new IttnTimelineEvent(RoundFrame(engine.TickToFrame(t)), t, IttnTimelineEventType.Normal, "a")).ToList();
        }

        var lowResult = IttnAnalyzer.CalcVoltage(BuildTimeline(lowBpm), isDoubleFrz: false, lowBpm);
        var highResult = IttnAnalyzer.CalcVoltage(BuildTimeline(highBpm), isDoubleFrz: false, highBpm);

        Assert.Equal(3, lowResult.MaxSectionNotes);
        Assert.Equal(3, highResult.MaxSectionNotes);
        Assert.True(highResult.PeakApm > lowResult.PeakApm,
            $"BPM200のPeakApm({highResult.PeakApm})はBPM100({lowResult.PeakApm})より大きいはず(同じ楽曲的パターンでもテンポが速いほど実時間密度は高い)");
        // BPM200はBPM100のちょうど2倍のテンポなので、同tick幅の実フレーム幅はちょうど半分になり、
        // PeakApmはちょうど2倍になる。
        Assert.Equal(lowResult.PeakApm * 2, highResult.PeakApm, precision: 3);
    }

    [Fact]
    public void CalcChord_TwoSimultaneousNotes_UsesBaseWeight()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(100, 100, IttnTimelineEventType.Normal, "a"),
            new(100, 100, IttnTimelineEventType.Normal, "b"),
        };
        var result = IttnAnalyzer.CalcChord(timeline, playFrame: 3600);

        Assert.Equal(0.2, result.AllChords, precision: 6); // BASE_WEIGHT
        Assert.Equal(0.2, result.Cpm, precision: 6);
    }

    [Fact]
    public void CalcChord_ThreeSimultaneousNotes_AddsLoopIncrement()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(100, 100, IttnTimelineEventType.Normal, "a"),
            new(100, 100, IttnTimelineEventType.Normal, "b"),
            new(100, 100, IttnTimelineEventType.Normal, "c"),
        };
        var result = IttnAnalyzer.CalcChord(timeline, playFrame: 3600);

        // weight = BASE_WEIGHT(0.2) + (BASE_INCREMENT(0.2) + LOOP_INCREMENT(0.05)) = 0.45
        Assert.Equal(0.45, result.AllChords, precision: 6);
    }

    [Fact]
    public void CalcSoflan_SpeedChangeAffectsBothReadjustAndContinuousLoad()
    {
        // speedChange@tick0(→2.0) の直後(1小節=6720tick以内、BPM150で概ね旧テストの120F相当の
        // 位置にあたるtick3500)にNormal。読み直し負荷(B)と非等速区間の常時負荷(A)の両方が
        // この1ノートに乗る(analyze.jsの仕様通り、二重計上ではない)。
        // 2026-07-25 BPM強化パート: 窓判定はtick基準(TimeWindowBMeasures=1小節)に置き換えたが、
        // このノートはどちらの基準でも窓内に収まる位置のため、期待値(SofTotal)はJS版と同じ。
        var engine = Bpm150Engine();
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.SpeedChange, null, 2.0),
            new(50, 3500, IttnTimelineEventType.Normal, "a"),
        };
        var result = IttnAnalyzer.CalcSoflan(timeline, playFrame: 3600, isDoubleFrz: false, engine);

        // B: noteCountB=1, speedDiff=1.0 → 1*(1.0*1.0)=1.0
        // A: dev=min(2.0,3.0)-1.0=1.0 → 1*(1.0*1.0)=1.0
        Assert.Equal(2.0, result.SofTotal, precision: 6);
    }

    [Fact]
    public void CalcSoflan_ConstantSpeedOne_NoLoad()
    {
        var engine = Bpm150Engine();
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.Normal, "a"),
            new(100, 7000, IttnTimelineEventType.Normal, "b"),
        };
        var result = IttnAnalyzer.CalcSoflan(timeline, playFrame: 3600, isDoubleFrz: false, engine);
        Assert.Equal(0, result.SofTotal);
    }

    [Fact]
    public void CalcSoflan_ReadjustWindow_IsTickBased_NotBpmSensitive()
    {
        // BPM強化の核心(SOFLAN版): 変速直後の読み直し窓(TimeWindowBMeasures=1小節)は
        // tick基準になったため、「同じ楽曲的位置(1小節=6720tick未満)のノート」はBPMが変わっても
        // 常に窓内としてカウントされる(=musically consistent)。旧JS版のTIME_WINDOW_B=120F固定では、
        // 同じ位置でもBPMが遅ければ120Fを超えて窓外になり得た(=wall-clock依存で不整合だった)。
        long noteTick = 6000; // 1小節(6720tick)未満 = どのBPMでも窓内
        foreach (var bpm in new[] { 80.0, 150.0, 300.0 })
        {
            var engine = BpmEngine(bpm);
            var timeline = new List<IttnTimelineEvent>
            {
                new(0, 0, IttnTimelineEventType.SpeedChange, null, 2.0),
                new(RoundFrame(engine.TickToFrame(noteTick)), noteTick, IttnTimelineEventType.Normal, "a"),
            };
            var result = IttnAnalyzer.CalcSoflan(timeline, playFrame: 3600, isDoubleFrz: false, engine);
            // B(1.0) + A(1.0) = 2.0、どのBPMでも同じ(tick基準窓のBPM非依存性の確認)
            Assert.Equal(2.0, result.SofTotal, precision: 6);
        }
    }

    [Fact]
    public void CalcFreeze_SingleFreeze_AddsArrowBonusOnly()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.FreezeStart, "a"),
            new(180, 180, IttnTimelineEventType.FreezeEnd, "a"),
        };
        var result = IttnAnalyzer.CalcFreeze(timeline, playFrame: 3600, isDoubleFrz: false);

        Assert.Equal(1, result.FrzArrCnt);
        Assert.Equal(180, result.FrzFrmCnt);
        Assert.Equal(2, result.FrzNtsCnt); // ARROW_BONUS only (保持中の他イベント無し)
    }

    [Fact]
    public void CalcFreeze_NormalNoteDuringHold_AddsHoldLoad()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.FreezeStart, "a"),
            new(60, 60, IttnTimelineEventType.Normal, "b"),
            new(180, 180, IttnTimelineEventType.FreezeEnd, "a"),
        };
        var result = IttnAnalyzer.CalcFreeze(timeline, playFrame: 3600, isDoubleFrz: false);

        // HOLD_LOAD(3, 保持中のnormal処理) + ARROW_BONUS(2) = 5
        Assert.Equal(5, result.FrzNtsCnt);
        Assert.Equal(1, result.MaxActive);
    }

    [Fact]
    public void CalcOnigiri_CountsOnlyOniLanes_NormalizedByLaneCount()
    {
        var timeline = new List<IttnTimelineEvent>
        {
            new(0, 0, IttnTimelineEventType.Normal, "oni"),
            new(60, 60, IttnTimelineEventType.Normal, "oni"),
            new(120, 120, IttnTimelineEventType.Normal, "left"), // オニレーンではない → 無視
        };
        var result = IttnAnalyzer.CalcOnigiri(timeline, playFrame: 3600, oniLaneIds: ["oni"], isDoubleFrz: false);

        Assert.Equal(2, result.OniCnt);
        Assert.Equal(2.0, result.OniApm, precision: 6); // 2件 / 1レーン / (3600/3600)
    }

    [Fact]
    public void CalcOnigiri_NoOniLanes_ReturnsZero()
    {
        var timeline = new List<IttnTimelineEvent> { new(0, 0, IttnTimelineEventType.Normal, "left") };
        var result = IttnAnalyzer.CalcOnigiri(timeline, playFrame: 3600, oniLaneIds: [], isDoubleFrz: false);
        Assert.Equal(0, result.Val);
    }

    // =====================================================================
    // Base / Total 合成
    // =====================================================================

    [Fact]
    public void CalcBaseRating_SingleElement_DividesByBaseScale()
    {
        double result = IttnAnalyzer.CalcBaseRating([130]);
        Assert.Equal(130 / 1.3, result, precision: 6);
    }

    [Fact]
    public void CalcBaseRating_SubElementsAddDiminishingBonus()
    {
        // max=100, sub=50 → bonus = 50*(50/100)*0.15 = 3.75 → (100+3.75)/1.3
        double result = IttnAnalyzer.CalcBaseRating([100, 50]);
        Assert.Equal((100 + 3.75) / 1.3, result, precision: 6);
    }

    [Fact]
    public void CalcTotalRating_NoSpecialElements_AccFactorFromMustRate()
    {
        // mustRate=90(BASE_MUST_RATE) → joltFactor=1 → accFactor=1、bonus=0
        var result = IttnAnalyzer.CalcTotalRating(baseRating: 100, mustRate: 90, altVal: 0, movVal: 0, jackVal: 0);
        Assert.Equal(1.0, result.AccFactor, precision: 6);
        Assert.Equal(0, result.Bonus);
        Assert.Equal(100, result.TotalLevel, precision: 6);
        Assert.Equal(20, result.ToolScaleLevel, precision: 6); // TOOL_SCALE_DIV=5
    }

    [Fact]
    public void CalcTotalRating_AccFactorClampedToRange()
    {
        // mustRate=0 → joltFactor = 1+(0-90)*0.03 = 1-2.7 = -1.7 → ACC_MINでクランプ
        var result = IttnAnalyzer.CalcTotalRating(baseRating: 100, mustRate: 0, altVal: 0, movVal: 0, jackVal: 0);
        Assert.Equal(0.5, result.AccFactor, precision: 6);
    }

    // =====================================================================
    // 統括(calculateFinal相当)・フルタブのスモークテスト
    // =====================================================================

    [Fact]
    public void CalculateFinal_EmptyTimeline_ReturnsNull()
    {
        var result = IttnAnalyzer.CalculateFinal(
            timeline: [], keyTypeId: "5", oniLaneIds: [], isDoubleFrz: false,
            gaugeBorder: "x", gaugeRecoveryRaw: 6, gaugeDamageRaw: 40, gaugeInitLifePercent: 25, maxLifeVal: 1000,
            engine: Bpm150Engine());
        Assert.Null(result);
    }

    [Fact]
    public void FullDifficultyTab_5Key_ProducesSaneResult()
    {
        var repo = TestFixtures.Repository();
        var template = repo.Get("5");
        var project = new ChartProject
        {
            ProjectName = "smoke",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        var tab = DifficultyTab.CreateFor(template, "Normal");
        tab.DifDataExtra = "x,6,40,25";

        long beat = TimingEngine.TicksPerBeat;
        // 5レーン(left/down/up/right/space)に8分音符ストリームを敷き詰める(4小節ぶん、簡易な合成譜面)
        for (int i = 0; i < 64; i++)
        {
            long tick = i * beat / 2;
            int laneIdx = i % tab.Lanes.Count;
            tab.Lanes[laneIdx].Notes.Add(tick);
        }
        // フリーズを1本
        tab.Lanes[0].Freezes.Add(new FreezeNote(beat * 4, beat * 6));
        // 変速を1回
        tab.SpeedEvents.Add(new ValueEvent(beat * 8, 1.5));

        var result = IttnAnalyzer.Analyze(project, tab, template);

        Assert.NotNull(result);
        Assert.True(result!.BaseRating > 0, $"baseRating should be positive, was {result.BaseRating}");
        Assert.True(result.TotalRating > 0, $"totalRating should be positive, was {result.TotalRating}");
        Assert.True(result.ToolScaleRating > 0);
        Assert.True(result.Stream > 0);
        Assert.True(result.Onigiri >= 0); // 5keyのspaceレーンはisONIGIRI(ストリームの一部として自動的にノートが乗る)
        Assert.True(result.Raw.TotalNotes > 0);
        Assert.True(result.Raw.MustRate is >= 0 and <= 100);
    }

    [Fact]
    public void FullDifficultyTab_11Key_AltAndMovNonNegative()
    {
        // ALT/MOV対応キー種(11key)で上段(sleft等)と下段(left等)を交互に配置し、
        // 例外を投げずALT/MOVが非負の値を返すことを確認する(詳細な数値検証はクロスバリデーションで行う)。
        var repo = TestFixtures.Repository();
        // "11"はTestData/EditingTemplateに無い可能性があるため、実テンプレート./templateを直接読む
        var templateDir = FindRealTemplateDir();
        var template = KeyTemplate.Load(Path.Combine(templateDir, "temp_11.json"));

        var project = new ChartProject
        {
            ProjectName = "smoke11",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        var tab = DifficultyTab.CreateFor(template, "Normal");
        tab.DifDataExtra = "x,6,40,25";

        long beat = TimingEngine.TicksPerBeat;
        int leftIdx = template.Lanes.ToList().FindIndex(l => l.LaneId == "left");
        int sleftIdx = template.Lanes.ToList().FindIndex(l => l.LaneId == "sleft");
        for (int i = 0; i < 32; i++)
        {
            long tick = i * beat / 2;
            bool useUp = i % 2 == 0;
            tab.Lanes[useUp ? sleftIdx : leftIdx].Notes.Add(tick);
        }

        var result = IttnAnalyzer.Analyze(project, tab, template);

        Assert.NotNull(result);
        Assert.True(result!.Alt >= 0);
        Assert.True(result.Mov >= 0);
        Assert.True(result.BaseRating > 0);
        Assert.True(result.TotalRating > 0);
    }

    // =====================================================================
    // ALT/MOV PEAK_WINDOW・SIMUL_WINDOW(2026-07-25 BPM強化パート)のBPM感度
    // =====================================================================

    /// <summary>11keyでALT/MOVを誘発する同一tick配置のright/sright交互譜面を組み、指定BPMで解析する。
    /// right(down_rightグループ、down scroll)とsright(up_rightグループ、up scroll)は同じ右手側(lr=right)
    /// だが異なるグループ(手のポジション)に属するため、交互配置で実際の手移動イベント(MOV)と
    /// 上下逆スクロールの視認イベント(ALT)の両方を誘発する。
    /// (left/sleftは11keyではグループが左手/右手で完全に分かれてしまい、片手内での実移動が
    /// 一度も発生しないため、MOVのBPM感度検証には不向きと判明。right/srightは同じ右手内での
    /// グループ間移動になるため、ShiftCooldown(120F固定・別スコープ)の対象外である大きな距離
    /// (実測: down_right/up_rightのグループ重心間距離≈7.7u > ExcMinDist=3.0u)が確保でき、
    /// 交互配置のたびに必ずコストが計上される)。</summary>
    private static IttnAnalysisResult Build11KeyAltMovResult(double bpm)
    {
        var templateDir = FindRealTemplateDir();
        var template = KeyTemplate.Load(Path.Combine(templateDir, "temp_11.json"));
        var project = new ChartProject
        {
            ProjectName = "bpmSensitivity11",
            BpmEvents = [new BpmEvent(0, bpm)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        var tab = DifficultyTab.CreateFor(template, "Normal");
        tab.DifDataExtra = "x,6,40,25";

        long beat = TimingEngine.TicksPerBeat;
        int rightIdx = template.Lanes.ToList().FindIndex(l => l.LaneId == "right");
        int srightIdx = template.Lanes.ToList().FindIndex(l => l.LaneId == "sright");
        for (int i = 0; i < 32; i++)
        {
            long tick = i * beat / 2;
            bool useUp = i % 2 == 0;
            tab.Lanes[useUp ? srightIdx : rightIdx].Notes.Add(tick);
        }

        var result = IttnAnalyzer.Analyze(project, tab, template);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void FullDifficultyTab_11Key_AltPeakPm_IsBpmSensitive_SameTickPattern()
    {
        // ALTのPEAK_WINDOW(8小節)は、同一tick配置(=同一楽曲的パターン)のままBPMだけを変えると、
        // 窓の実測フレーム幅が変わるためAltPeakPm(局所max成分のper-minute値)もそれに応じて変化する
        // はず(旧JS版の固定600Fフレーム窓では、この感度は「実時間として」しか現れなかった)。
        var low = Build11KeyAltMovResult(100);
        var high = Build11KeyAltMovResult(200);

        Assert.NotEqual(low.Raw.AltPeakPm, high.Raw.AltPeakPm);
        Assert.True(high.Raw.AltPeakPm > low.Raw.AltPeakPm,
            $"BPM200のAltPeakPm({high.Raw.AltPeakPm})はBPM100({low.Raw.AltPeakPm})より大きいはず(同一tick配置でもテンポが速いほど実時間密度は高い)");
    }

    [Fact]
    public void FullDifficultyTab_11Key_MovPeakPm_IsBpmSensitive_SameTickPattern()
    {
        // MOVのPEAK_WINDOW(8小節)についても同様。SIMUL_WINDOW(2拍)の候補探索窓もtick基準になった
        // ため、出張候補の拾い方自体はBPMに依存しない(グルーピングはBPM非依存、正規化のみBPM依存)。
        var low = Build11KeyAltMovResult(100);
        var high = Build11KeyAltMovResult(200);

        Assert.NotEqual(low.Raw.MovPeakPm, high.Raw.MovPeakPm);
        Assert.True(high.Raw.MovPeakPm > low.Raw.MovPeakPm,
            $"BPM200のMovPeakPm({high.Raw.MovPeakPm})はBPM100({low.Raw.MovPeakPm})より大きいはず(同一tick配置でもテンポが速いほど実時間密度は高い)");
    }

    /// <summary>リポジトリの実テンプレートディレクトリ(./template)を、テスト実行ディレクトリから
    /// 上位に向かって探索する(TestData/EditingTemplateには11keyが無いため、11key依存のテストのみ
    /// これを使う)。</summary>
    private static string FindRealTemplateDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "template")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("実テンプレートディレクトリ(template)が見つかりません");
        return Path.Combine(dir, "template");
    }
}
