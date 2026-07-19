using DanoniEditor.Core.Models;
using DanoniEditor.Core.Playtest;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// プレイテスト判定エンジンのテスト(2026-07-17g)。BPM120(1拍=30frame)を使い、
/// tick48のノート=30frame基準で判定幅・コンボ規則・フリーズ判定を検証する。
/// 判定幅はdanoniplus本家g_judgObj(Normal)準拠、フリーズは仕様書12.2.1確定仕様。
/// </summary>
public class PlaytestEngineTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private static (PlaytestEngine Engine, List<JudgeResult> Log) NewEngine(
        Action<ChartProject> setup, double frzAttempt = 5, double offset = 0)
    {
        var project = TestFixtures.NewProject(bpm: 120);
        setup(project);
        var engine = new PlaytestEngine(project.Tabs[0], project.CreateTimingEngine(), frzAttempt, offset);
        var log = new List<JudgeResult>();
        engine.Judged += r => log.Add(r);
        return (engine, log);
    }

    // --- 矢印判定幅(±2/±4/±6/±8、枠外=ウワァン) ---

    [Theory]
    [InlineData(30.0, PlayJudge.Ii)]      // ズレ0
    [InlineData(28.0, PlayJudge.Ii)]      // -2(早押し境界)
    [InlineData(33.0, PlayJudge.Shakin)]  // +3
    [InlineData(35.0, PlayJudge.Matari)]  // +5
    [InlineData(37.0, PlayJudge.Shobon)]  // +7
    [InlineData(38.0, PlayJudge.Shobon)]  // +8(境界)
    public void ArrowJudge_ByFrameDelta(double inputFrame, PlayJudge expected)
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Notes.Add(48 * T)); // 30frame
        engine.KeyDown(0, inputFrame);
        var r = Assert.Single(log);
        Assert.Equal(expected, r.Judge);
    }

    [Fact]
    public void ArrowOutsideWindow_NotJudgedByKey_ThenUwanOnAdvance()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Notes.Add(48 * T));
        engine.KeyDown(0, 39.0); // +9: 枠外、空押し扱い(判定なし)
        Assert.Empty(log);
        engine.Advance(39.0); // 30+8 < 39 → 見逃しウワァン
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Uwan, r.Judge);
        Assert.Equal(0, engine.Combo);
    }

    // --- コンボ規則(本家準拠: イイ/シャキン加算、マターリ維持、ショボーン/ウワァンリセット) ---

    [Fact]
    public void Combo_IncrementKeepReset()
    {
        var (engine, _) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[0].Notes.Add(48 * T);   // 30f
            p.Tabs[0].Lanes[0].Notes.Add(96 * T);   // 60f
            p.Tabs[0].Lanes[0].Notes.Add(144 * T);  // 90f
            p.Tabs[0].Lanes[0].Notes.Add(192 * T);  // 120f
        });
        engine.KeyDown(0, 30);  // イイ → combo 1
        engine.KeyDown(0, 63);  // シャキン → combo 2
        Assert.Equal(2, engine.Combo);
        engine.KeyDown(0, 95);  // マターリ(+5) → 維持
        Assert.Equal(2, engine.Combo);
        engine.KeyDown(0, 127); // ショボーン(+7) → リセット
        Assert.Equal(0, engine.Combo);
        Assert.Equal(2, engine.MaxCombo);
    }

    // --- フリーズ判定(始点±4+終点まで押し続け=O.K.、frzAttempt猶予) ---

    [Fact]
    public void Freeze_HoldToEnd_Kita()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T))); // 30f→60f
        engine.KeyDown(0, 31);
        Assert.Empty(log); // 始点成立はまだ判定イベントにしない(終点で確定)
        engine.Advance(60);
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Kita, r.Judge);
        Assert.Equal(1, engine.FreezeCombo);
    }

    [Fact]
    public void Freeze_EarlyRelease_BeyondAttempt_Iknai()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T)), frzAttempt: 5);
        engine.KeyDown(0, 30);
        engine.KeyUp(0, 40);
        engine.Advance(44); // 猶予内(40+5=45まで)
        Assert.Empty(log);
        engine.Advance(46); // 猶予超過かつ終点前 → N.G.
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Iknai, r.Judge);
        Assert.Equal(0, engine.FreezeCombo);
    }

    [Fact]
    public void Freeze_ReleaseAndRepressWithinAttempt_StillKita()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T)), frzAttempt: 5);
        engine.KeyDown(0, 30);
        engine.KeyUp(0, 40);
        engine.KeyDown(0, 43); // 猶予内の再押下 → ホールド復帰
        engine.Advance(60);
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Kita, r.Judge);
    }

    [Fact]
    public void Freeze_ReleaseNearEnd_WithinAttempt_Kita()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T)), frzAttempt: 5);
        engine.KeyDown(0, 30);
        engine.KeyUp(0, 57); // 終点(60)の3frame前、猶予内に終点到達
        engine.Advance(60);
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Kita, r.Judge);
    }

    [Fact]
    public void Freeze_StartMissed_Iknai()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T)));
        engine.Advance(39); // 30+8 < 39
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Iknai, r.Judge);
    }

    [Fact]
    public void Freeze_LateStart_5to8Frames_Iknai()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(48 * T, 96 * T)));
        engine.KeyDown(0, 36); // +6: ±5〜8はN.G.(仕様書12.2.1)
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Iknai, r.Judge);
    }

    // --- オフセット(仕様書12.2 タイミング調整) ---

    [Fact]
    public void Offset_ShiftsJudgeTiming()
    {
        var (engine, log) = NewEngine(p => p.Tabs[0].Lanes[0].Notes.Add(48 * T), offset: 10); // 実効40f
        engine.KeyDown(0, 40);
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Ii, r.Judge);
    }

    // --- フリーズと矢印の同時近接(近い方を優先) ---

    [Fact]
    public void ArrowAndFreezeNearby_CloserOneWins()
    {
        var (engine, log) = NewEngine(p =>
        {
            p.Tabs[0].Lanes[0].Notes.Add(48 * T);                       // 30f
            p.Tabs[0].Lanes[0].Freezes.Add(new FreezeNote(53 * T, 96 * T)); // 33.125f
        });
        engine.KeyDown(0, 30); // 矢印の方が近い
        var r = Assert.Single(log);
        Assert.Equal(PlayJudge.Ii, r.Judge);
    }
}
