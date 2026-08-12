using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>2026-08-08新設の回帰テスト: 「speed/boostのマイナスフレームを許容する」表示設定
/// (ChartProject.AllowNegativeFrameSpeedBoost)の追加を受けて調査した結果判明した、
/// TimingEngine.TickToFrameのtick&lt;0境界条件の不具合(全て同一フレームに潰れる)を修正した後、
/// 実際にtick&lt;0へ置いたspeed/boostイベントがdos.txt上で正しい負のフレームとして
/// 出力されることをエクスポート結果の文字列レベルで確認する。</summary>
public class DosNegativeFrameSpeedBoostTests
{
    private static ChartProject NewProjectWithNegativeSpeedBoost()
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, 120)], // 1拍=1680tick=30frame
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 100,
            BlankFrame = 0,
        };
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        // tick=-1680(1拍前) → frame = 100 - 30 = 70
        tab.SpeedEvents.Add(new ValueEvent(-TimingEngine.TicksPerBeat, 1.5));
        // tick=-1680*10(10拍前) → frame = 100 - 300 = -200
        tab.BoostEvents.Add(new ValueEvent(-10 * TimingEngine.TicksPerBeat, 0.5));
        p.Tabs.Add(tab);
        return p;
    }

    [Fact]
    public void Export_NegativeTickSpeedEvent_ProducesCorrectNegativeOffsetFrame()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithNegativeSpeedBoost());

        // 修正前は一律frame=100(tick0のフレーム)に潰れていた。修正後は70になる。
        Assert.Contains("speed_data=70,1.5", text);
    }

    [Fact]
    public void Export_NegativeTickBoostEvent_ProducesGenuinelyNegativeFrame()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProjectWithNegativeSpeedBoost());

        // StartNumber(100)より十分前に置いた場合、実際に負のフレーム番号として出力されることを確認する。
        Assert.Contains("boost_data=-200,0.5", text);
    }
}
