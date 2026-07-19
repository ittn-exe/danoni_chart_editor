using DanoniEditor.Core.Import;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// FUJI 23key対応(2026-07-18c)のテスト。fujiLaneNum(FUJI列順≠本体エンジン順)、
/// レーン16以降のPP下位ニブル拡張、フリーズ'9'フラグ、S(24分)トークンを検証する。
/// テンプレートはユーザー提供のtemp_23.json(fujiLaneNum付与済み)を使用。
/// </summary>
public class FujiImporter23keyTests
{
    private const long T = DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat / 48; // 旧48tick/拍基準からの換算係数(=35、2026-07-19g)
    private static FujiImportResult Import(string score)
    {
        var repo = TestFixtures.Repository();
        var text = $"""
        $version=3.050
        $template=template_23.txt
        $frame=0/0/9600/1,10
        $barcut=
        $score=
        {score}
        $header=
        |musicUrl=nosound.mp3|
        """;
        return new FujiImporter(repo.Get).Import(text, "23");
    }

    private static int LaneIdx(string laneId)
    {
        var t = TestFixtures.Repository().Get("23");
        return t.Lanes.Select((l, i) => (l, i)).First(x => x.l.LaneId == laneId).i;
    }

    [Fact]
    public void FujiColumn4_MapsToLeft_NotBleft()
    {
        // FUJI列4=left(本体エンジン順では4=bleft)。fujiLaneNumによる分離の検証
        var r = Import("0000:0040,");
        Assert.Contains(0 * T, r.Tab.Lanes[LaneIdx("left")].Notes);
        Assert.Empty(r.Tab.Lanes[LaneIdx("bleft")].Notes);
    }

    [Fact]
    public void PpLowNibble_ExtendsLaneBy16()
    {
        // '2160' = PP=0x20, FUJIレーン6+16=22(bright)。位置は0x20/256=1/8小節=24tick
        var r = Import("0000:2160,");
        var notes = r.Tab.Lanes[LaneIdx("bright")].Notes;
        Assert.Contains(24 * T, notes);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("2160"));
    }

    [Fact]
    public void FreezeFlag9_ExtendsLaneBy16()
    {
        // '0920-0120' = フラグ9 → FUJIレーン2+16=18(sright)のフリーズ
        var r = Import("0000:0920-0120,");
        Assert.Single(r.Tab.Lanes[LaneIdx("sright")].Freezes);
        Assert.Empty(r.Tab.Lanes[LaneIdx("aup")].Freezes);
    }

    [Fact]
    public void SToken_ShiftsToNext24thWithinSlot()
    {
        // 'S'=24分グリッド: 8分表は+2/3、8分裏は+1/3の16分シフト(dos照合で確定、2026-07-18e)
        // '00CS'→10.67pp=8tick, '10CS'→21.33pp=16tick, '20CS'→42.67pp=32tick
        var r = Import("0000:00CS,10CS,20CS,");
        var notes = r.Tab.Lanes[LaneIdx("sleft")].Notes.OrderBy(x => x).ToList();
        Assert.Equal([8 * T, 16 * T, 32 * T], notes);
        Assert.Empty(r.Warnings.Where(w => w.Contains("丸め")));
    }

    [Fact]
    public void RToken_RoundsToNearest12th_TieGoesUp()
    {
        // 'R'=12分グリッドへ四捨五入(dos照合で確定、2026-07-18e):
        // '90CR'(144)→149.33pp=112tick(後ろへ), '70CR'(112)→106.67pp=80tick(前へ),
        // 'A0CR'(160)→同距離タイ→170.67pp=128tick(後ろ優先)
        var r = Import("0000:90CR,70CR,A0CR,");
        var notes = r.Tab.Lanes[LaneIdx("sleft")].Notes.OrderBy(x => x).ToList();
        Assert.Equal([80 * T, 112 * T, 128 * T], notes);
        Assert.Empty(r.Warnings.Where(w => w.Contains("丸め")));
    }

    [Fact]
    public void FineDigit_IsFrameShift()
    {
        // fine桁d=(9−d)フレームシフト(2026-07-18e)。$frame=0/0/9600/1,10 → mlen=96f/小節、BPM150。
        // 'D00C'(PP=0xD0=208pp、digit C=12) → 基準frame=208/256×96=78f、シフト9−12=−3f → 75f
        // 1tick=96/192=0.5f → 75f=150tick。丸め誤差ゼロで一致する例を選んでいる。
        var r = Import("0000:D00C,");
        Assert.Contains(150 * T, r.Tab.Lanes[LaneIdx("aleft")].Notes);
    }
}
