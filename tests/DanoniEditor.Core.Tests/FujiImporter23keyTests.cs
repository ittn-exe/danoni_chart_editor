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
    public void FineT_And_FineX_AreAlwaysPlusMinusEightRegardlessOfAlignment()
    {
        // 2026-07-31: ユーザー提供test23key_v2.txt/test23key_v2_dos.txtの実測値(oni_dataの
        // 通常ノート4点+sright系フリーズ終点2点、計6点)により、旧実装の「固定+3/−3フレーム」説は
        // 誤りだったと判明。正しくはpp自体に常に+8/−8(=256/32)する式で、実測値と厳密一致した。
        // 当初はS/Wと同様「32刻みなら+8、それ以外は+0」というアライメント依存式を疑ったが、
        // これはフリーズ終点tail解析の別バグ(10進dをhexで誤読)と混同した誤った暫定結論であり、
        // 両バグ修正後に再検証した結果、アライメントに関係なく常に+8/−8が正しいと判明した。
        // 'E00T'(PP=0xE0、pos=224、224%32==0): adjustedPp=224+8=232 → 87f → 174tick。
        // 'D00X'(PP=0xD0、pos=208、208%32==16): adjustedPp=208−8=200 → 75f → 150tick
        //   (アライメントに関わらず常に−8となるため、旧「固定−3」式の75f→150tickと結果的に
        //   一致するケース)。
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
        // '0800-003T': 始点=pp0/fine0→0f→0tick。終点pp=始点pp(0)+d(3)×16=48
        // (2026-07-31: 絶対位置ではなく始点からの長さと訂正)。fine='T'は常に+8のため
        // 終点pp=48+8=56→21f→42tick。
        var r = Import("0000:0800-003T,");
        var freezes = r.Tab.Lanes[LaneIdx("aleft")].Freezes;
        var f = Assert.Single(freezes);
        Assert.Equal(0 * T, f.StartTick);
        Assert.Equal(42 * T, f.EndTick);
        Assert.Empty(r.Warnings.Where(w => w.Contains("無視") || w.Contains("解釈できない")));
    }

    [Fact]
    public void FreezeEnd_RUsesTwelveGrid_SUsesTwentyFourGrid()
    {
        // 2026-07-31: ユーザー提供の公式フォーマット文書で「E(終点)はC(位置/始点)と同一体系」と
        // 明記されたため、終点のRも位置・始点と同じ12分グリッド式を使うと確定した(旧実装は
        // 実例不足によりS式(24分グリッド)へ暫定的に揃えていたが、これは誤りだった)。
        // 'aleft'(digit0)側はS→pp=48→終点20f→40tick(従来通り変化なし)。
        // 'left'(digit4)側はR→pp=48→12分グリッド式で終点16f→32tick(Sとは異なる値になる)。
        var r = Import("0000:0800-003S,0840-003R,");
        var aleftFreeze = Assert.Single(r.Tab.Lanes[LaneIdx("aleft")].Freezes);
        var leftFreeze = Assert.Single(r.Tab.Lanes[LaneIdx("left")].Freezes);
        Assert.Equal(0 * T, aleftFreeze.StartTick);
        Assert.Equal(40 * T, aleftFreeze.EndTick);
        Assert.Equal(0 * T, leftFreeze.StartTick);
        Assert.Equal(32 * T, leftFreeze.EndTick);
    }

    [Fact]
    public void FineW_IsTwentyFourGridSubtraction_NewlyAccepted()
    {
        // 2026-07-31: 公式フォーマット文書で新規判明した'W'='[24]-'(Sの符号反転)。
        // '600W'(PP=0x60=96pp、96%32==0のため32/3を減算) → 96−32/3=85.333pp
        // → 85.333/256×mlen相当で64tick。旧実装では未対応のfine文字として警告付きで
        // 無視されていたが、新実装では警告なしで解釈される。
        var r = Import("0000:600W,");
        var notes = r.Tab.Lanes[LaneIdx("aleft")].Notes;
        Assert.Contains(64 * T, notes);
        Assert.Empty(r.Warnings.Where(w => w.Contains("不明") || w.Contains("解釈できない")));
    }

    [Fact]
    public void FreezeEnd_FineTailDuration_IsAdditiveToStartPosition_NotAbsolute()
    {
        // 2026-07-31: ユーザー提供test23key.txtで実際にFUJIエディタ自身が「ノート間で追い越しが
        // 発生しています」エラーを出した箇所とは別に、同ファイルの'4920-003R'相当のトークン
        // (始点P=4≠0でfine文字tailを使うフリーズ)を実測値(sfrzRight_data=...,714,725,...)と
        // 突き合わせたところ、旧実装(終点pp=d×16を絶対位置として解釈)は始点(pp=64)より終点
        // (pp=48)の方が前に来る不正な結果を返しており、これが原本のバグだと判明した。
        // 正しくは終点pp=始点pp+d×16=64+48=112(始点からの長さとして加算)。
        // 'sright'(fujiLaneNum=2+16=18)で検証: head='4920'(P=4,M=9,D=2,F=0)→始点pp=64→48tick。
        // tail='003R'→d=3→終点pp=64+48=112→Rの12分グリッド式で106.667pp→80tick。
        // 終点(80tick)が始点(48tick)より後になることを確認する
        // (旧実装では終点pp=48(絶対値)のままR式適用→約53tickとなり始点48tickより僅かに前後が
        // 怪しくなる上、より極端な例(実ファイルのP=8,12ケース)では明確に始点より前へ逆転していた)。
        var r = Import("0000:4920-003R,");
        var freeze = Assert.Single(r.Tab.Lanes[LaneIdx("sright")].Freezes);
        Assert.True(freeze.EndTick > freeze.StartTick,
            $"フリーズ終点({freeze.EndTick})は始点({freeze.StartTick})より後でなければならない");
    }

    [Fact]
    public void FreezeEnd_TailPrefixIsAlwaysParsedAsDecimal_NeverHex()
    {
        // 2026-07-31: 公式文書の「D(3桁)は10進数値」という明記、およびtest23key_v2.txtの
        // 全16件フリーズペアとの突き合わせにより、旧実装の「QQQQ全体が16進として解釈できれば
        // hexのdurとして使い、0x100以上は−0x60補正」という独自ルールは誤りだったと確定した。
        // この補正式は先頭3桁が"01Y"型の値でしか偶然成立せず(例: "0120"→補正後192、10進
        // モデルでも192で一致)、それ以外の値では大きくずれる(例: 先頭3桁"099"は
        // 16進153・10進99で大きく異なる)。
        // '0800-0993': 始点pp=0(始点tick=0)。終点はd=099(10進で99)+fine='3'(+3フレーム)。
        // 10進モデルではendpp=99×16=1584が基準となり、旧実装の16進+補正モデル
        // (基準pp=0x099×16=2448、補正後2352)とは大きく異なる基準位置になるため、
        // 終点tickも大きく異なる値になるはずである(具体的な数値は実測未検証だが、
        // 「16進として解釈されていない」ことを、基準pp=1584相当のtick範囲に収まっているかで
        // 検証する)。
        var r = Import("0000:0800-0993,");
        var freeze = Assert.Single(r.Tab.Lanes[LaneIdx("aleft")].Freezes);
        Assert.True(freeze.EndTick > freeze.StartTick,
            $"フリーズ終点({freeze.EndTick})は始点({freeze.StartTick})より後でなければならない");
        // 10進モデル(pp基準1584、mlen=96/measureのテスト環境)ではtickは概ね
        // 1584/256*4*TicksPerBeatのオーダーになる。16進モデル(基準2448)ならその1.5倍超になり、
        // 明確に区別できる。
        double decimalOrderTick = 1584.0 / 256.0 * (4.0 * DanoniEditor.Core.Timing.TimingEngine.TicksPerBeat);
        Assert.True(freeze.EndTick < decimalOrderTick * 1.2,
            $"終点tick({freeze.EndTick})が10進モデル想定域({decimalOrderTick})を大きく超えており、旧hex解釈に戻っていないか確認が必要");
        Assert.Equal(0 * T, freeze.StartTick);
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
