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

    // =====================================================================
    // 2026-07-25: 1224frztest.txt/1224frztest_dos.txtの全数比較で確定したfine文字体系の
    // 追加テスト。旧実装の「9−d」式が1〜9側で誤りだった点の回帰テストと、フリーズ始点・
    // 終点でのfine文字対応(旧実装は非対応/一部トークンを例外で握りつぶして全損していた)を検証する。
    // =====================================================================

    [Fact]
    public void FineDigitLowRange_1to9_IsPositiveFrameShift()
    {
        // 旧実装は全レンジで「9−d」式(1〜9側は実例未観測の外挿で誤り)。新実装はd=1〜9でそのまま+dフレーム。
        // 'D003'(PP=0xD0=208pp、fine='3') → 基準78f+3f=81f → 162tick。
        // 旧式なら 9−3=+6f → 84f → 168tick になり、この値は含まれないことも確認する。
        var r = Import("0000:D003,");
        var notes = r.Tab.Lanes[LaneIdx("aleft")].Notes;
        Assert.Contains(162 * T, notes);
        Assert.DoesNotContain(168 * T, notes);
    }

    [Fact]
    public void FineLetterExtendedRange_GHI_IsNegativeFrameShift()
    {
        // A〜Iで−1〜−9フレーム。G/H/Iは16進として無効なため旧実装では警告スキップだった新規範囲。
        // 'D00G'(PP=208pp、fine='G'=−7f) → 78f−7f=71f → 142tick。
        var r = Import("0000:D00G,");
        var notes = r.Tab.Lanes[LaneIdx("aleft")].Notes;
        Assert.Contains(142 * T, notes);
        Assert.Empty(r.Warnings.Where(w => w.Contains("不明なfine文字") || w.Contains("解釈できない")));
    }

    [Fact]
    public void FineT_And_FineX_AreFlatThreeFrameShifts()
    {
        // T=常に+3フレーム、X=常に−3フレーム(2026-07-25新規判明)。
        // 'E00T'(PP=0xE0=224pp、基準84f) → 87f → 174tick。'D00X'(PP=208pp、基準78f) → 75f → 150tick。
        var r = Import("0000:E00T,D00X,");
        var notes = r.Tab.Lanes[LaneIdx("aleft")].Notes;
        Assert.Contains(174 * T, notes);
        Assert.Contains(150 * T, notes);
    }

    [Fact]
    public void FreezeEnd_LetterSuffixTail_NoLongerSilentlyDropped()
    {
        // 旧実装はQQQQを無条件に16進として解釈しており、末尾がfine文字(非16進)のトークンは
        // FormatExceptionで例外送出→呼び出し元のtry/catchで「解釈できないトークン」警告に
        // まるごと化けてフリーズが始点・終点とも消えていた(2026-07-25回帰テスト)。
        // '0800-003T': 始点=pp0/fine0→0f→0tick。終点=残り3桁'003'をd=3とみなしpp=48、
        // fine='T'→基準18f+3f=21f→42tick。
        var r = Import("0000:0800-003T,");
        var freezes = r.Tab.Lanes[LaneIdx("aleft")].Freezes;
        var f = Assert.Single(freezes);
        Assert.Equal(0 * T, f.StartTick);
        Assert.Equal(42 * T, f.EndTick);
        Assert.Empty(r.Warnings.Where(w => w.Contains("無視") || w.Contains("解釈できない")));
    }

    [Fact]
    public void FreezeEnd_RSuffixAndSSuffixTail_CurrentlyShareSameGridFormula()
    {
        // フリーズ終点のRは実例2件のみでR式(12分)/S式(24分)が数値上一致する位置しかなく、
        // 現状はS式(24分グリッド)を暫定適用している(未確定事項、docs参照)。この挙動を
        // 固定するための回帰テスト: 'aleft'(digit0)側はS、'left'(digit4)側はRだが、
        // どちらもpp=48→終点20f→40tickという同一結果になることを確認する。
        var r = Import("0000:0800-003S,0840-003R,");
        var aleftFreeze = Assert.Single(r.Tab.Lanes[LaneIdx("aleft")].Freezes);
        var leftFreeze = Assert.Single(r.Tab.Lanes[LaneIdx("left")].Freezes);
        Assert.Equal(0 * T, aleftFreeze.StartTick);
        Assert.Equal(40 * T, aleftFreeze.EndTick);
        Assert.Equal(0 * T, leftFreeze.StartTick);
        Assert.Equal(40 * T, leftFreeze.EndTick);
    }

    [Fact]
    public void FreezeStart_NonZeroFineDigit_ShiftsStartOnly_NotEndBase()
    {
        // フリーズ始点のfine文字は始点位置のみに作用し、終点(QQQQが全て16進の場合)の
        // 基準位置(head先頭のPスロット)には影響しない(2026-07-25確定、始点と終点は独立計算)。
        // '0801-0030': 始点=pp0+fine'1'(+1f)→1f→2tick。終点=x(=0)/16+dur(0x30=48)/256を
        // そのままTickOfへ渡す従来通りの計算→36tick(始点の+1fシフトは反映されない)。
        var r = Import("0000:0801-0030,");
        var f = Assert.Single(r.Tab.Lanes[LaneIdx("aleft")].Freezes);
        Assert.Equal(2 * T, f.StartTick);
        Assert.Equal(36 * T, f.EndTick);
    }
}
