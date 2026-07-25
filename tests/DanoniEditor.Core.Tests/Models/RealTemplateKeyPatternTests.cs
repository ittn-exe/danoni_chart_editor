using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Models;

/// <summary>
/// リポジトリ直下の実テンプレート(./template/temp_*.json)に投入したキーパターン追加データ
/// (2026-08-06e要望対応、danoniplus本家danoni_constants.jsのkeyCtrlX_Y等から移植)が、
/// 全パターンについて破綻なくWithPatternで適用できることを検証する回帰テスト。
/// </summary>
public class RealTemplateKeyPatternTests
{
    private static string RealTemplateDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "template", "temp_5.json")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("リポジトリ直下の./templateが見つかりません");
        return Path.Combine(dir, "template");
    }

    [Fact]
    public void AllRealTemplates_ExtraPatterns_ApplyWithoutError_AndKeepLaneIdentityStable()
    {
        var repo = new TemplateRepository(RealTemplateDir());
        foreach (var id in repo.ListKeyTypeIds())
        {
            var tpl = repo.Get(id);
            for (int p = 1; p < tpl.PatternCount; p++)
            {
                var applied = tpl.WithPattern(p);
                Assert.Equal(tpl.Lanes.Count, applied.Lanes.Count);
                for (int i = 0; i < tpl.Lanes.Count; i++)
                {
                    // 素性(laneId/dataName/engineLaneNum)はパターンを変えても不変であるべき
                    Assert.Equal(tpl.Lanes[i].LaneId, applied.Lanes[i].LaneId);
                    Assert.Equal(tpl.Lanes[i].DataName, applied.Lanes[i].DataName);
                    Assert.Equal(tpl.Lanes[i].EngineLaneNum, applied.Lanes[i].EngineLaneNum);
                    // プレゼンテーション項目は最低限「値が入っている」ことだけ確認する
                    Assert.NotEmpty(applied.Lanes[i].KeyAssign);
                    Assert.True(applied.Lanes[i].ScrollDirection is "up" or "down");
                    Assert.False(string.IsNullOrWhiteSpace(applied.Lanes[i].NoteGraphic));
                }
            }
        }
    }

    [Fact]
    public void Template11_HasSideKeyLinkedPattern_MatchingDanoniplusKeyCtrl11_1()
    {
        var repo = new TemplateRepository(RealTemplateDir());
        var tpl = repo.Get("11");
        Assert.Equal(2, tpl.PatternCount); // パターン0(既定)+パターン1

        var applied = tpl.WithPattern(1);
        Assert.Equal(50, applied.Blank);

        // danoni_constants.jsのkeyCtrl11_1準拠: 物理キー自体はパターン0と同一(サイドキーの並び位置のみ変わる)
        var left = applied.Lanes.Single(l => l.LaneId == "left");
        Assert.Equal(["S"], left.KeyAssign);
        var sright = applied.Lanes.Single(l => l.LaneId == "sright");
        Assert.Equal(["→"], sright.KeyAssign);
        Assert.Equal(10.25, sright.PosIndex);
    }
}
