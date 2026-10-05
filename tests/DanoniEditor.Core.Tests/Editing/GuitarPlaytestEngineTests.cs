using System.Linq;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Playtest;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// ギター系キー種(std_gt.js相当)のプレーテスト判定オーバーレイのテスト(2026-09-27要望対応)。
/// テスト専用テンプレート temp_n5g.json(TestData/EditingTemplate、本番の./template/temp_n5g.jsonと
/// 同一内容)を使う。レーン構成: 0=left,1=down,2=up,3=right(フレット),4=space(ピック)。
/// BPM120(1拍=30frame)、tick48=30frame基準(PlaytestEngineTestsと同じ換算、T=1tickあたり0.625frame)。
/// </summary>
public class GuitarPlaytestEngineTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48;
    private const int Left = 0, Down = 1, Up = 2, Right = 3, Pick = 4;

    private static (PlaytestEngine Base, GuitarPlaytestEngine Guitar, List<JudgeResult> Log) NewEngine(
        Action<ChartProject> setup, IEnumerable<string>? targetKeyTypeIds = null, GuitarSettings? settings = null)
    {
        var project = TestFixtures.NewProject(bpm: 120, keyTypeId: "n5g");
        setup(project);
        var timing = project.CreateTimingEngine();
        var engine = new PlaytestEngine(project.Tabs[0], timing, frzAttempt: 5);
        var log = new List<JudgeResult>();
        engine.Judged += r => log.Add(r);

        var template = TestFixtures.Repository().Get("n5g");
        settings ??= new GuitarSettings { TargetKeyTypeIds = [.. targetKeyTypeIds ?? ["n5g", "n9g"]] };
        var guitar = GuitarPlaytestEngine.TryCreate(template, engine, settings)
            ?? throw new InvalidOperationException("GuitarPlaytestEngine.TryCreateがnullを返した(テスト前提が崩れている)");
        return (engine, guitar, log);
    }

    [Fact]
    public void TryCreate_ReturnsNull_ForNonTargetKeyType()
    {
        var project = TestFixtures.NewProject(bpm: 120, keyTypeId: "5"); // "space"レーンを持たない5key
        var engine = new PlaytestEngine(project.Tabs[0], project.CreateTimingEngine(), frzAttempt: 5);
        var template = TestFixtures.Repository().Get("5");
        var settings = new GuitarSettings(); // 既定値(n5g/n9gのみ対象)
        Assert.Null(GuitarPlaytestEngine.TryCreate(template, engine, settings));
    }

    [Fact]
    public void TryCreate_ReturnsNull_WhenKeyTypeNotInTargetList()
    {
        var project = TestFixtures.NewProject(bpm: 120, keyTypeId: "n5g");
        var engine = new PlaytestEngine(project.Tabs[0], project.CreateTimingEngine(), frzAttempt: 5);
        var template = TestFixtures.Repository().Get("n5g");
        var settings = new GuitarSettings { TargetKeyTypeIds = [] }; // n5gが対象外
        Assert.Null(GuitarPlaytestEngine.TryCreate(template, engine, settings));
    }

    [Fact]
    public void PickDown_ChordMatch_ConsumesPickAndFretTogether()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);  // 30f、Leftフレットとの2音コード
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);  // 30f、ピック
        });

        guitar.PickDown(30.0, j => j == Left); // Leftフレットのみ保持(構成メンバーと一致)

        Assert.Equal(2, log.Count);
        Assert.All(log, r => Assert.Equal(PlayJudge.Ii, r.Judge));
        Assert.Equal(new[] { Left, Pick }, log.Select(r => r.Lane).OrderBy(l => l).ToArray());
        Assert.Equal(2, engine.Combo);
    }

    [Fact]
    public void PickDown_FingerMismatch_MissingFret_PendsWithoutImmediateResolution()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        });

        guitar.PickDown(30.0, j => false); // 構成メンバー(Left)を誰も押していない → 不一致

        Assert.Empty(log); // 即ミスにはせず保留(ピック先行の猶予)、この時点ではまだ何も確定しない
    }

    [Fact]
    public void PickDown_FingerMismatch_ExtraFretHeld_PendsWithoutImmediateResolution()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        });

        guitar.PickDown(30.0, j => j == Left || j == Right); // 余計にRightも押している → 不一致

        Assert.Empty(log);
    }

    [Fact]
    public void PickDown_OpenString_NoFretsRequired_ConsumesPickOnly()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T); // フレット側に同tickの音がない開放弦相当
        });

        guitar.PickDown(30.0, j => false); // フレットは何も押していない(正しい)

        var r = Assert.Single(log);
        Assert.Equal(Pick, r.Lane);
        Assert.Equal(PlayJudge.Ii, r.Judge);
    }

    [Fact]
    public void PickDown_OpenString_ButFretHeld_PendsWithoutImmediateResolution()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        });

        guitar.PickDown(30.0, j => j == Up); // 開放弦なのに関係ないフレットを押している → 不一致

        Assert.Empty(log);
    }

    [Fact]
    public void PickDown_NoPickTarget_DoesNothing()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T); // ピック側に判定対象が無い
        });

        guitar.PickDown(30.0, j => j == Left);

        Assert.Empty(log); // 空ピック(素通し、本家同様ペナルティなし)
    }

    [Fact]
    public void PickDown_ChordFreeze_HoldToEnd_BothLanesKita()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Freezes.Add(new FreezeNote(48 * T, 96 * T));  // 30f→60f
            p.Tabs[0].Lanes[Pick].Freezes.Add(new FreezeNote(48 * T, 96 * T));
        });

        guitar.PickDown(30.0, j => j == Left); // 両レーンのフリーズ始点をチョード成立で開始
        Assert.Empty(log); // 始点成立はまだ判定イベントにしない(終点で確定、PlaytestEngineと同じ仕様)

        guitar.Advance(60.0, j => j == Left); // 本番同様、PlaytestEngine.Advanceより先に呼ぶ
        engine.Advance(60.0); // 両レーンとも保持継続中のまま終点へ到達

        Assert.Equal(2, log.Count);
        Assert.All(log, r => Assert.Equal(PlayJudge.Kita, r.Judge));
        // FreezeComboはPlaytestEngine側でレーン独立に加算されるため、2レーン分のキター確定で2になる
        // (フリーズのコード判定自体は「両レーンともホールドを保持できたか」という一致判定であり、
        // コンボの数え方そのものはこのオーバーレイでは変更しない)。
        Assert.Equal(2, engine.FreezeCombo);
    }

    // ============================================================
    // Phase2(2026-09-27b要望対応): ピック先行の猶予・離し遅れ免除・ダブルピック許可
    // ============================================================

    [Fact]
    public void PickDown_MismatchThenFretCatchesUpWithinGrace_ResolvesOnAdvance()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        }, settings: new GuitarSettings { PickEarlyGraceFrames = 4 });

        guitar.PickDown(30.0, j => false); // 指板不一致 → 保留
        Assert.Empty(log);

        guitar.Advance(32.0, j => j == Left); // 猶予(4F)内にLeftが押された → 指板一致で確定
        Assert.Equal(2, log.Count);
        Assert.All(log, r => Assert.Equal(PlayJudge.Ii, r.Judge)); // |30-32|=2 → イイ窓
        Assert.Equal(2, engine.Combo);
    }

    [Fact]
    public void PickDown_MismatchBeyondGrace_GivesUpPending_ThenTimesOutNaturally()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T); // 30f
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        }, settings: new GuitarSettings { PickEarlyGraceFrames = 4 });

        guitar.PickDown(30.0, j => false); // 不一致 → 保留
        guitar.Advance(33.9, j => false); // まだ猶予内(4F未満) → 保留継続
        Assert.Empty(log);

        guitar.Advance(34.0, j => false); // 猶予(4F)超過 → 保留を諦める(確定はしない)
        Assert.Empty(log); // この時点では消費されない

        // 保留を諦めた後は通常のPlaytestEngine側の枠外タイムアウトに任される
        // (このオーバーレイはグループ一括ミスの再現までは行わないため、Left/Pickは各レーン独立にウワァンする)。
        engine.Advance(39.0); // |30-39|=9 > ショボーン窓(8) → 枠外
        Assert.Equal(2, log.Count);
        Assert.All(log, r => Assert.Equal(PlayJudge.Uwan, r.Judge));
    }

    [Fact]
    public void Repick_Allowed_NewPickOverwritesPendingAndCanResolveImmediately()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        }, settings: new GuitarSettings { AllowRepick = true, PickEarlyGraceFrames = 4 });

        guitar.PickDown(30.0, j => false); // 1回目: 不一致 → 保留
        Assert.Empty(log);

        guitar.PickDown(32.0, j => j == Left); // 2回目(打ち直し): 今度は指板一致 → その場で即確定
        Assert.Equal(2, log.Count);
        Assert.All(log, r => Assert.Equal(PlayJudge.Ii, r.Judge)); // |30-32|=2
        Assert.Equal(2, engine.Combo);
    }

    [Fact]
    public void Repick_Disallowed_IgnoresAdditionalPickWhilePending()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        }, settings: new GuitarSettings { AllowRepick = false, PickEarlyGraceFrames = 4 });

        guitar.PickDown(30.0, j => false); // 1回目: 不一致 → 保留
        guitar.PickDown(32.0, j => j == Left); // 2回目: 保留中の追加ピックは無視(成立もペナルティも起こさない)
        Assert.Empty(log);

        // 無視されただけで最初の保留自体は生きているため、猶予内であれば従来通り解決される
        guitar.Advance(33.0, j => j == Left);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void ReleaseExempt_RecentlyResolvedLaneHeldOver_ForgivenWithinMaxFrames()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Down].Notes.Add(48 * T);          // 30f、Down+Pickの2音コード
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Left].Notes.Add((48 + 8) * T);    // 35f(30f+5f)、今度はLeft+Pickのみ必要
            p.Tabs[0].Lanes[Pick].Notes.Add((48 + 8) * T);
        }, settings: new GuitarSettings { ReleaseExemptEnable = true, ReleaseExemptMaxFrames = 8 });

        guitar.PickDown(30.0, j => j == Down); // 1組目を解決(Down/Pickが「直近解決」として記録される)
        Assert.Equal(2, log.Count);
        log.Clear();

        // 2組目はLeftのみ必要。Downは押しっぱなし(弾き終わり指を離すのが遅れた想定)だが、
        // 直前解決からまだ5F(<=8F)しか経っていないため「余計な押下」として扱わず一致とみなす。
        guitar.PickDown(35.0, j => j == Down || j == Left);

        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { Left, Pick }, log.Select(r => r.Lane).OrderBy(l => l).ToArray());
    }

    [Fact]
    public void ReleaseExempt_BeyondMaxFrames_NoLongerForgiven()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Down].Notes.Add(48 * T);           // 30f
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Left].Notes.Add((48 + 16) * T);    // 40f(30f+10f)、猶予(8F)を超えている
            p.Tabs[0].Lanes[Pick].Notes.Add((48 + 16) * T);
        }, settings: new GuitarSettings { ReleaseExemptEnable = true, ReleaseExemptMaxFrames = 8 });

        guitar.PickDown(30.0, j => j == Down);
        Assert.Equal(2, log.Count);
        log.Clear();

        // 直前解決から10F(>8F)経っているため、もはやDownの押しっぱなしは免除されない → 不一致 → 保留
        guitar.PickDown(40.0, j => j == Down || j == Left);

        Assert.Empty(log);
    }

    [Fact]
    public void ReleaseExempt_Disabled_NeverForgivenEvenWithinWindow()
    {
        var (_, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Down].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Left].Notes.Add((48 + 8) * T); // 35f
            p.Tabs[0].Lanes[Pick].Notes.Add((48 + 8) * T);
        }, settings: new GuitarSettings { ReleaseExemptEnable = false });

        guitar.PickDown(30.0, j => j == Down);
        Assert.Equal(2, log.Count);
        log.Clear();

        // ReleaseExemptEnable=falseのため、猶予フレーム内でも免除しない
        guitar.PickDown(35.0, j => j == Down || j == Left);

        Assert.Empty(log);
    }

    [Fact]
    public void FreezeEndGrace_HoldoverAfterFreezeEnds_ForgivenWithinGraceFrames()
    {
        var (engine, guitar, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Down].Freezes.Add(new FreezeNote(48 * T, 96 * T));  // 30f→60f
            p.Tabs[0].Lanes[Pick].Freezes.Add(new FreezeNote(48 * T, 96 * T));
            p.Tabs[0].Lanes[Left].Notes.Add((96 + 8) * T);   // 65f(60f+5f)、今度はLeft+Pickのみ必要
            p.Tabs[0].Lanes[Pick].Notes.Add((96 + 8) * T);
        }, settings: new GuitarSettings { ReleaseExemptEnable = true, FreezeEndGraceFrames = 10 });

        guitar.PickDown(30.0, j => j == Down); // Down+Pickのフリーズ始点を成立させてホールド開始
        Assert.Empty(log);

        guitar.Advance(60.0, j => j == Down); // 本番同様、PlaytestEngine.Advanceより先に呼ぶ
        engine.Advance(60.0); // 終点到達、両レーンともキター(_freezeEndFrame[Down]=60を記録)
        Assert.Equal(2, log.Count);
        log.Clear();

        // フリーズ終端から5F(<=10F)後。Downを離すのが遅れていてもフリーズ終端猶予で免除される。
        guitar.PickDown(65.0, j => j == Down || j == Left);

        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { Left, Pick }, log.Select(r => r.Lane).OrderBy(l => l).ToArray());
    }

    // ============================================================
    // Phase4着手前の見直しで発覚した修正(2026-09-27c): ハンマリング(ピック非同時)ノートの判定
    // ============================================================

    [Fact]
    public void IsSimultaneousWithPick_True_WhenFretHeadSameTickAsPickHead()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T); // 同tick → 真のコード構成メンバー
        });

        Assert.True(guitar.IsSimultaneousWithPick(Left));
    }

    [Fact]
    public void IsSimultaneousWithPick_False_ForHammerOnlyFretNote()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            // Leftの先頭ターゲットは30f、Pickの先頭ターゲットは60f → tickが異なる
            // (ピックを介さず単体で判定される「ハンマリング」ノート相当)。
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Pick].Notes.Add(96 * T);
        });

        Assert.False(guitar.IsSimultaneousWithPick(Left));
    }

    [Fact]
    public void IsSimultaneousWithPick_False_WhenPickLaneHasNoTarget()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T); // ピック側に判定対象が無い
        });

        Assert.False(guitar.IsSimultaneousWithPick(Left));
    }

    [Fact]
    public void MembersAtTick_ReturnsAllFretLanesSharingThatTick()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Up].Notes.Add(48 * T);
            p.Tabs[0].Lanes[Down].Notes.Add(96 * T); // 別tick → メンバーではない
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        });

        Assert.Equal(new[] { Left, Up }, guitar.MembersAtTick(48 * T).OrderBy(j => j).ToArray());
    }

    [Fact]
    public void MembersAtTick_EmptyForOpenString()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T); // フレット側に同tickの音がない開放弦相当
        });

        Assert.Empty(guitar.MembersAtTick(48 * T));
    }

    [Fact]
    public void HasPickAt_TrueWhenPickHasUnresolvedArrowAtTick()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Pick].Notes.Add(48 * T);
        });

        Assert.True(guitar.HasPickAt(48 * T));
        Assert.False(guitar.HasPickAt(96 * T));
    }

    [Fact]
    public void HasPickAt_FalseForHammerOnlyFretTick()
    {
        var (_, guitar, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[Left].Notes.Add(48 * T); // フレット単体、ピックなし = ハンマリング
        });

        Assert.False(guitar.HasPickAt(48 * T));
    }
}
