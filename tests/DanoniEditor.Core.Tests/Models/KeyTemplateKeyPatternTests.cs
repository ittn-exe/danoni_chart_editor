using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Models;

/// <summary>
/// KeyTemplateのキーパターン拡張(2026-08-06e要望対応、danoniplus本家の「キーパターン」概念)を検証する。
/// - パターン0(既定)は常に既存のLanes等をそのまま使う(後方互換)
/// - WithPatternで追加パターンを適用すると、KeyAssign/ColorGroup/PosIndex/ScrollDirection/
///   NoteGraphic/RotationAngleとBlank/DivideCnt/PosMaxが差し替わる
/// - LaneId/DataName等のレーンの素性はパターンに依存せず変わらない
/// - レーン件数不一致時はエラーになる
/// </summary>
public class KeyTemplateKeyPatternTests
{
    private static KeyTemplate BuildTemplate(int extraPatternCount = 1)
    {
        var lanes = new List<LaneDef>
        {
            new()
            {
                LaneId = "left", DataName = "left", DisplayOrder = 0,
                KeyAssign = ["Left"], ColorGroup = 0, PosIndex = 0,
                ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 0,
            },
            new()
            {
                LaneId = "right", DataName = "right", DisplayOrder = 1,
                KeyAssign = ["Right"], ColorGroup = 1, PosIndex = 1,
                ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 180, EngineLaneNum = 1,
            },
        };

        var extraPatterns = new List<KeyPattern>();
        for (int p = 0; p < extraPatternCount; p++)
        {
            extraPatterns.Add(new KeyPattern
            {
                Name = $"変則{p + 1}",
                Blank = 60 + p,
                DivideCnt = 1,
                PosMax = 2,
                LaneOverrides =
                [
                    new LanePatternOverride
                    {
                        KeyAssign = ["S"], ColorGroup = 2, PosIndex = 3,
                        ScrollDirection = "up", NoteGraphic = "onigiri", RotationAngle = 90,
                    },
                    new LanePatternOverride
                    {
                        KeyAssign = ["D"], ColorGroup = 3, PosIndex = 4,
                        ScrollDirection = "up", NoteGraphic = "onigiri", RotationAngle = 270,
                    },
                ],
            });
        }

        return new KeyTemplate
        {
            KeyTypeId = "2t", KeyTypeName = "2test", KeyCount = 2,
            Blank = 50, DivideCnt = 0, PosMax = 0,
            Lanes = lanes,
            ExtraPatterns = extraPatterns,
        };
    }

    [Fact]
    public void WithPattern_Zero_ReturnsSameInstance()
    {
        var tpl = BuildTemplate();
        Assert.Same(tpl, tpl.WithPattern(0));
    }

    [Fact]
    public void WithPattern_OutOfRange_ReturnsSameInstance()
    {
        var tpl = BuildTemplate(extraPatternCount: 1);
        Assert.Same(tpl, tpl.WithPattern(5));
        Assert.Same(tpl, tpl.WithPattern(-1));
    }

    [Fact]
    public void WithPattern_One_SwapsPresentationFieldsButKeepsIdentity()
    {
        var tpl = BuildTemplate();
        var applied = tpl.WithPattern(1);

        Assert.NotSame(tpl, applied);
        Assert.Equal(60, applied.Blank);
        Assert.Equal(1, applied.DivideCnt);
        Assert.Equal(2, applied.PosMax);

        Assert.Equal(2, applied.Lanes.Count);
        var left = applied.Lanes[0];
        Assert.Equal("left", left.LaneId); // 素性は不変
        Assert.Equal("left", left.DataName);
        Assert.Equal(0, left.EngineLaneNum);
        Assert.Equal(["S"], left.KeyAssign);
        Assert.Equal(2, left.ColorGroup);
        Assert.Equal(3, left.PosIndex);
        Assert.Equal("up", left.ScrollDirection);
        Assert.Equal("onigiri", left.NoteGraphic);
        Assert.Equal(90, left.RotationAngle);

        var right = applied.Lanes[1];
        Assert.Equal("right", right.LaneId);
        Assert.Equal(["D"], right.KeyAssign);

        // 元のテンプレート自体は変更されない(パターン0のまま)
        Assert.Equal(50, tpl.Blank);
        Assert.Equal(["Left"], tpl.Lanes[0].KeyAssign);
    }

    [Fact]
    public void PatternCount_ReflectsBaseAndExtraPatterns()
    {
        Assert.Equal(1, BuildTemplate(extraPatternCount: 0).PatternCount);
        Assert.Equal(3, BuildTemplate(extraPatternCount: 2).PatternCount);
    }

    [Fact]
    public void WithPattern_LaneCountMismatch_Throws()
    {
        var baseTpl = BuildTemplate(extraPatternCount: 0);
        var tpl = new KeyTemplate
        {
            KeyTypeId = baseTpl.KeyTypeId,
            KeyTypeName = baseTpl.KeyTypeName,
            KeyCount = baseTpl.KeyCount,
            Blank = baseTpl.Blank,
            DivideCnt = baseTpl.DivideCnt,
            PosMax = baseTpl.PosMax,
            Lanes = baseTpl.Lanes,
            ExtraPatterns =
            [
                new KeyPattern
                {
                    Blank = 60, DivideCnt = 1, PosMax = 2,
                    LaneOverrides =
                    [
                        new LanePatternOverride
                        {
                            KeyAssign = ["S"], ColorGroup = 0, PosIndex = 0,
                            ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0,
                        },
                    ], // レーン1件分しか無い(本体は2件)
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() => tpl.WithPattern(1));
    }
}
