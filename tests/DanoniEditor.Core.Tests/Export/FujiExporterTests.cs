using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Export;

/// <summary>FujiExporter(2026-08-03、FujiImporterの逆方向)のテスト。
/// グリッド(1/16小節、'0'〜'9'/'A'〜'I'の数字系fine文字のみ使用)に乗る位置についてはExport→Importの
/// 往復で完全一致することを軸に検証する('R'/'S'/'W'/'T'/'X'は確実性の低さから本エクスポータでは
/// 使用しない設計のため、それらが必要な位置は本テストの対象外)。</summary>
public class FujiExporterTests
{
    private static (ChartProject Project, DifficultyTab Tab, KeyTemplate Template) BuildSample(double bpm = 120)
    {
        var repo = TestFixtures.Repository();
        var template = repo.Get("5");
        var project = TestFixtures.NewProject(bpm);
        var tab = project.Tabs[0];
        tab.DifficultyName = "Normal";
        tab.InitialSpeed = 3.5;

        long measure = 4L * TimingEngine.TicksPerBeat;
        // 小節0の頭・1/4位置(16刻みグリッド上)にノート
        tab.Lanes[0].Notes.Add(0);
        tab.Lanes[0].Notes.Add(measure / 4);
        // 小節1の頭から半小節分のフリーズ
        tab.Lanes[1].Freezes.Add(new FreezeNote(measure, measure + measure / 2));
        tab.SpeedEvents.Add(new ValueEvent(0, 1.0));
        tab.BoostEvents.Add(new ValueEvent(measure / 2, 1.5));

        return (project, tab, template);
    }

    [Fact]
    public void Export_RoundtripsNotesFreezesAndSpeedsOnGrid()
    {
        var (project, tab, template) = BuildSample();
        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());
        Assert.Empty(result.Warnings.Where(w => w.Contains("グリッドに乗らない")));

        var imported = new FujiImporter(TestFixtures.Repository().Get).Import(result.Text, "5");

        Assert.Equal(tab.Lanes[0].Notes.OrderBy(x => x), imported.Tab.Lanes[0].Notes.OrderBy(x => x));
        Assert.Equal(tab.Lanes[1].Freezes.Select(f => (f.StartTick, f.EndTick)),
                     imported.Tab.Lanes[1].Freezes.Select(f => (f.StartTick, f.EndTick)));
        Assert.Equal(tab.SpeedEvents.Select(e => (e.Tick, e.Value)), imported.Tab.SpeedEvents.Select(e => (e.Tick, e.Value)));
        Assert.Equal(tab.BoostEvents.Select(e => (e.Tick, e.Value)), imported.Tab.BoostEvents.Select(e => (e.Tick, e.Value)));
    }

    [Fact]
    public void Export_RoundtripsDifDataAndBlankFrame()
    {
        var (project, tab, template) = BuildSample(bpm: 175);
        project.BlankFrame = 250;
        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());

        var imported = new FujiImporter(TestFixtures.Repository().Get).Import(result.Text, "5");

        Assert.Equal("Normal", imported.Tab.DifficultyName);
        Assert.Equal(3.5, imported.Tab.InitialSpeed);
        Assert.Equal(250, imported.BlankFrame);
        // FUJI形式はフレーム値を0.1フレーム単位の整数(×10)で保持するため、BPMは差分から近似的に
        // 逆算される(完全一致ではなく僅かな誤差が生じるのはFUJI形式自体の量子化によるもの)。
        Assert.Equal(175, imported.BpmEvents[0].Bpm, 1);
    }

    [Fact]
    public void Export_MisalignedNote_RoundsWithLiteralFineCharByDefault()
    {
        var (project, tab, template) = BuildSample();
        long measure = 4L * TimingEngine.TicksPerBeat;
        // 16刻みグリッド(measure/16単位)から3フレーム分ズレた位置を作る(数字系fine文字で表現可能な範囲)
        var engine = project.CreateTimingEngine();
        long gridTick = measure / 16 * 3; // 3/16小節
        double gridFrame = engine.TickToFrame(gridTick);
        long shiftedTick = engine.FrameToTick(gridFrame + 2) is var t && t > 0 ? (long)Math.Round(t) : gridTick + 1;
        tab.Lanes[0].Notes.Add(shiftedTick);

        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());

        // 元のグリッド位置ノート2つ + ズレたノート1つ = 3つ、丸めが発生しても例外にはならないことを確認
        var imported = new FujiImporter(TestFixtures.Repository().Get).Import(result.Text, "5");
        Assert.Equal(3, imported.Tab.Lanes[0].Notes.Count);
    }

    [Fact]
    public void Export_BarcutRoundtripsTimeSignature()
    {
        var (project, tab, template) = BuildSample();
        // 小節2を12/16(skip=4)へカットし、小節3で4/4へ復帰
        project.TimeSignatures.Add(new TimeSignatureEvent(2, 12, 16));
        project.TimeSignatures.Add(new TimeSignatureEvent(3, 4, 4));

        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());
        Assert.Contains("$barcut=2/4", result.Text);

        var imported = new FujiImporter(TestFixtures.Repository().Get).Import(result.Text, "5");
        var importedSig = imported.TimeSignatures.First(s => s.MeasureIndex == 2);
        Assert.Equal(12, importedSig.Numerator);
        Assert.Equal(16, importedSig.Denominator);
    }

    /// <summary>2026-08-22不具合修正の回帰テスト(SkbExporterと同種の不具合、第三者報告): speed/boostの
    /// 「始点終点オートスムージング出力」(ValueEvent.LinkGridDivisionによるリンク、2026-07-30要望対応)が、
    /// dos.txt出力・プレイテスト・プレビューでは機能する一方、FUJIエクスポートだけリンクした2点のみが
    /// 出力され中間点が生成されていなかった。FujiExporterがValueEventSmoothing.ExpandLinkedEventsを
    /// 経由していなかったことが原因。修正後は中間点も出力されることを確認する。</summary>
    [Fact]
    public void Export_LinkedSpeedEvents_IncludesAutoGeneratedIntermediatePoints()
    {
        var (project, tab, template) = BuildSample();
        tab.SpeedEvents.Clear();
        long measure = 4L * TimingEngine.TicksPerBeat;
        // 1小節(measure)離れた2点をdivision=4(4分刻み、FUJIの1/16小節グリッドにも乗る)でリンク。
        // 始点(tick0)と終点(tick=measure)の間に1/4小節刻みの中間点が3つ生成されるはず。
        tab.SpeedEvents.Add(new ValueEvent(0, 1.0, LinkGridDivision: 4));
        tab.SpeedEvents.Add(new ValueEvent(measure, 2.0));

        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());
        Assert.Empty(result.Warnings.Where(w => w.Contains("グリッドに乗らない")));

        var imported = new FujiImporter(TestFixtures.Repository().Get).Import(result.Text, "5");

        // 始点・終点・中間3点の計5件になっているはず(修正前は始点・終点の2件のみだった)。
        Assert.Equal(5, imported.Tab.SpeedEvents.Count);
        var values = imported.Tab.SpeedEvents.OrderBy(e => e.Tick).Select(e => e.Value).ToList();
        Assert.Equal(1.0, values[0]);
        Assert.Equal(1.25, values[1], 3);
        Assert.Equal(1.5, values[2], 3);
        Assert.Equal(1.75, values[3], 3);
        Assert.Equal(2.0, values[4]);
    }

    /// <summary>2026-08-23新設: BPMの「始点終点リンク(直線ランプ)」のFUJIエクスポート回帰テスト。
    /// FUJI形式は$frameの各セグメントでstartFrame/endFrameを直接指定するため、リンク区間を細かい
    /// 小節境界の離散ステップへ分解すれば近似できる(ValueEventSmoothing.ExpandLinkedBpmEvents)。
    /// division=1(全音符=1小節ぶん)でリンクし、4小節すべての境界がちょうどステップと一致するように
    /// 組むことで、丸め/重複削除が発生せず5セグメントぶんの$frameトークンが生成されることを確認する。</summary>
    [Fact]
    public void Export_LinkedBpmEvents_GeneratesIntermediateFrameSegments()
    {
        var (project, tab, template) = BuildSample();
        long measure = 4L * TimingEngine.TicksPerBeat;
        // measureCountはノート等(BPMイベントではない)の最大tickから決まるため、ランプの終端小節まで
        // カバーするノートを置いておく(置かないとmeasureCountがランプより手前で終わってしまう)。
        tab.Lanes[0].Notes.Add(measure * 4);
        project.BpmEvents.Clear();
        project.BpmEvents.Add(new DanoniEditor.Core.Timing.BpmEvent(0, 100, LinkGridDivision: 1));
        project.BpmEvents.Add(new DanoniEditor.Core.Timing.BpmEvent(measure * 4, 200));

        var result = FujiExporter.Export(project, tab, template, new FujiExportOptions());

        var frameLine = result.Text.Split('\n').Single(l => l.StartsWith("$frame="));
        var tokens = frameLine["$frame=".Length..].Split(',');
        // 0〜4小節目の5セグメント(0/1/2/3/4小節目始まり)+末尾のmeasureCountトークン=6トークン。
        Assert.Equal(6, tokens.Length);
    }
}
