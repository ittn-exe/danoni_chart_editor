using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Import;

/// <summary>CustomKeyTemplateImporter(2026-08-03、CustomKeyTemplateExporterの逆方向)のテスト。
/// 主にExporterで書き出したテキストをImporterへ通し、再度Exporterへ通した結果が元のテキストと
/// 一致すること(ラウンドトリップ)を軸に検証する。</summary>
public class CustomKeyTemplateImporterTests
{
    private static KeyTemplate SingleLaneTemplate(IReadOnlyList<string> keyAssign, KeyboardLayout layout = KeyboardLayout.Us) => new()
    {
        KeyTypeId = "rt",
        KeyTypeName = "RoundtripTest",
        KeyCount = 5,
        Blank = 50,
        DivideCnt = 4,
        PosMax = 5,
        KeyboardLayout = layout,
        Lanes =
        [
            new LaneDef { LaneId = "left", DataName = "left", DisplayOrder = 0, KeyAssign = ["←"], ColorGroup = 0, PosIndex = 0, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 0 },
            new LaneDef { LaneId = "down", DataName = "down", DisplayOrder = 1, KeyAssign = ["↓"], ColorGroup = 0, PosIndex = 1, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = -90, EngineLaneNum = 1 },
            new LaneDef { LaneId = "up", DataName = "up", DisplayOrder = 2, KeyAssign = ["↑"], ColorGroup = 0, PosIndex = 2, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 90, EngineLaneNum = 2 },
            new LaneDef { LaneId = "right", DataName = "right", DisplayOrder = 3, KeyAssign = keyAssign, ColorGroup = 1, PosIndex = 3, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 180, EngineLaneNum = 3 },
            new LaneDef { LaneId = "space", DataName = "space", DisplayOrder = 4, KeyAssign = ["Space"], ColorGroup = 2, PosIndex = 4, ScrollDirection = "down", NoteGraphic = "onigiri", RotationAngle = 0, EngineLaneNum = 4 },
        ],
    };

    [Fact]
    public void Import_RoundtripsExportedText_SinglePattern()
    {
        var template = SingleLaneTemplate(["→", "E"]);
        var exported = CustomKeyTemplateExporter.Export(template);

        var result = CustomKeyTemplateImporter.Import(exported, KeyboardLayout.Us);
        var reExported = CustomKeyTemplateExporter.Export(result.Template);

        Assert.Equal(exported, reExported);
        Assert.Equal("rt", result.Template.KeyTypeId);
        Assert.Equal("RoundtripTest", result.Template.KeyTypeName);
        Assert.Equal(5, result.Template.Lanes.Count);
        Assert.Equal(["→", "E"], result.Template.Lanes[3].KeyAssign);
    }

    [Fact]
    public void Import_RoundtripsExportedText_MultiPattern()
    {
        var baseTemplate = SingleLaneTemplate(["S"]);
        var overrideLanes = new List<LanePatternOverride>
        {
            new() { KeyAssign = ["1"], ColorGroup = 1, PosIndex = 0, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 10 },
            new() { KeyAssign = ["2"], ColorGroup = 1, PosIndex = 1, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 20 },
            new() { KeyAssign = ["3"], ColorGroup = 1, PosIndex = 2, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 30 },
            new() { KeyAssign = ["4"], ColorGroup = 1, PosIndex = 3, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 40 },
            new() { KeyAssign = ["Space"], ColorGroup = 3, PosIndex = 4, ScrollDirection = "up", NoteGraphic = "giko", RotationAngle = 0 },
        };
        var template = new KeyTemplate
        {
            KeyTypeId = baseTemplate.KeyTypeId, KeyTypeName = baseTemplate.KeyTypeName, KeyCount = baseTemplate.KeyCount,
            Blank = baseTemplate.Blank, DivideCnt = baseTemplate.DivideCnt, PosMax = baseTemplate.PosMax,
            Lanes = baseTemplate.Lanes, KeyboardLayout = KeyboardLayout.Us,
            ExtraPatterns = [new KeyPattern { Blank = 60, DivideCnt = 3, PosMax = 4, LaneOverrides = overrideLanes }],
        };
        var exported = CustomKeyTemplateExporter.Export(template);

        var result = CustomKeyTemplateImporter.Import(exported, KeyboardLayout.Us);
        var reExported = CustomKeyTemplateExporter.Export(result.Template);

        Assert.Equal(exported, reExported);
        Assert.Equal(2, result.Template.PatternCount);
    }

    [Theory]
    [InlineData("@", KeyboardLayout.Jis)]
    [InlineData("[", KeyboardLayout.Jis)]
    [InlineData("]", KeyboardLayout.Jis)]
    [InlineData("[", KeyboardLayout.Us)]
    [InlineData("]", KeyboardLayout.Us)]
    [InlineData("`", KeyboardLayout.Us)]
    [InlineData("<", KeyboardLayout.Us)]
    [InlineData(";", KeyboardLayout.Us)]
    public void Import_RoundtripsLayoutDependentSymbolKeys(string symbol, KeyboardLayout layout)
    {
        var template = SingleLaneTemplate([symbol], layout);
        var exported = CustomKeyTemplateExporter.Export(template);

        var result = CustomKeyTemplateImporter.Import(exported, layout);

        Assert.Equal([symbol], result.Template.Lanes[3].KeyAssign);
        Assert.Equal(exported, CustomKeyTemplateExporter.Export(result.Template));
    }

    [Fact]
    public void Import_AutoDetectsKeyTypeIdWhenNotSpecified()
    {
        var template = SingleLaneTemplate(["E"]);
        var exported = CustomKeyTemplateExporter.Export(template);

        var result = CustomKeyTemplateImporter.Import(exported, KeyboardLayout.Us);

        Assert.Equal("rt", result.Template.KeyTypeId);
    }

    [Fact]
    public void Import_ThrowsWhenNoRecognizedHeadersPresent()
    {
        Assert.Throws<InvalidDataException>(() => CustomKeyTemplateImporter.Import("not a valid custom key definition", KeyboardLayout.Us));
    }

    [Fact]
    public void Import_MissingRequiredHeader_FillsPlaceholderAndMarksUnresolved()
    {
        // 2026-08-03要望対応: div{X}等が無い実例(本体の標準キー種データを一部省略・継承する
        // 形で書かれたテキスト)でも、以前のように例外で中断せず、取得できる部分だけ取り込んだ上で
        // divideCnt/posMax(全レーン共通のパターン単位項目)を仮の値(0)で埋め、FieldStatusへ記録する。
        var text = "|keyCtrl5g=F1,F2,F3,F4,Enter/ShiftRight|\n|chara5g=a,b,c,d,e|\n|color5g=0,0,0,0,0|\n" +
                   "|pos5g=0,1,2,3,4|\n|stepRtn5g=0,45,135,180,giko|\n|blank5g=57.5|";
        var result = CustomKeyTemplateImporter.Import(text, KeyboardLayout.Jis);

        Assert.Contains(result.Warnings, w => w.Contains("div5g"));
        Assert.Contains((0, "divideCnt"), result.FieldStatus.UnresolvedPatternFields);
        Assert.Contains((0, "posMax"), result.FieldStatus.UnresolvedPatternFields);
        Assert.Equal(0, result.Template.DivideCnt);
        Assert.Equal(0, result.Template.PosMax);
        // 取得できた項目(chara/color/pos/stepRtn/blank)はそのまま反映される
        Assert.Equal("a", result.Template.Lanes[0].DataName);
        Assert.Equal(57.5, result.Template.Blank);
    }

    [Fact]
    public void Import_ShorthandPatternReferenceWithoutResolver_FillsPlaceholderAndMarksUnresolved()
    {
        // 2026-08-03要望対応: 本体の「他キー種/パターンの値を丸ごと再利用する」略記("5_0"等)は、
        // 解決用テンプレートが渡されていなくても例外で中断せず、仮の値で埋めて処理を継続する。
        // keyAssign(keyCtrl)自体は画面構成に直接関わらないためFieldStatusの対象外(プレースホルダ["?"])。
        var text = "|keyCtrltest=F1,F2,F3,F4,Enter/ShiftRight$5_0|\n|charatest=a,b,c,d,e$a,b,c,d,e|\n" +
                   "|colortest=0,0,0,0,0$0,0,0,0,0|\n|postest=0,1,2,3,4$0,1,2,3,4|\n" +
                   "|divtest=5,5$5,5|\n|blanktest=50$50|\n" +
                   "|scrolltest=Default::1,1,1,1,1$Default::1,1,1,1,1|\n|stepRtntest=0,45,135,180,giko$0,45,135,180,giko|";
        var result = CustomKeyTemplateImporter.Import(text, KeyboardLayout.Us);

        Assert.Equal(2, result.Template.PatternCount);
        Assert.Contains(result.Warnings, w => w.Contains("5_0"));
        var pattern1 = result.Template.WithPattern(1);
        Assert.All(pattern1.Lanes, l => Assert.Equal(["?"], l.KeyAssign));
        // colorGroup等、パターン1の他の項目は略記を使っていないため正常に取り込まれる(未確定にならない)
        Assert.Empty(result.FieldStatus.UnresolvedLaneFields.Where(f => f.PatternIndex == 1));
    }

    [Fact]
    public void Import_ResolvesShorthandPatternReferenceViaTemplateResolver()
    {
        // 2026-08-03: 標準キー種のテンプレート(temp_5.json等)がtemplateResolver経由で渡されれば、
        // 本体側の「他キー種/パターンの値を丸ごと再利用する」略記("5_0"等)を解決できることを検証する。
        var baseForRef = SingleLaneTemplate(["E"]);
        var refTemplate = new KeyTemplate
        {
            KeyTypeId = "5", KeyTypeName = "Five", KeyCount = baseForRef.KeyCount,
            Blank = baseForRef.Blank, DivideCnt = baseForRef.DivideCnt, PosMax = baseForRef.PosMax,
            Lanes = baseForRef.Lanes, KeyboardLayout = baseForRef.KeyboardLayout,
        };
        KeyTemplate Resolver(string keyTypeId) => keyTypeId == "5" ? refTemplate : throw new FileNotFoundException(keyTypeId);

        var text = "|keyCtrltest=F1,F2,F3,F4,Space$5_0|\n|charatest=a,b,c,d,e$5_0|\n" +
                   "|colortest=0,0,0,0,1$5_0|\n|postest=0,1,2,3,4$5_0|\n" +
                   "|divtest=5,5$5_0|\n|blanktest=50$5_0|\n" +
                   "|scrolltest=Default::1,1,1,1,1$5_0|\n|stepRtntest=0,45,135,180,giko$5_0|";

        var result = CustomKeyTemplateImporter.Import(text, KeyboardLayout.Us, templateResolver: Resolver);

        Assert.Equal(2, result.Template.PatternCount);
        var pattern1 = result.Template.WithPattern(1);
        Assert.Equal(refTemplate.Lanes.Select(l => l.KeyAssign), pattern1.Lanes.Select(l => l.KeyAssign));
        Assert.Equal(refTemplate.DivideCnt, pattern1.DivideCnt);
        Assert.Equal(refTemplate.PosMax, pattern1.PosMax);
        Assert.Equal(refTemplate.Blank, pattern1.Blank);
        Assert.Contains(result.Warnings, w => w.Contains("5_0") && w.Contains("keyCtrl"));
    }

    [Fact]
    public void Import_ShorthandReferenceWithMissingTemplate_FillsPlaceholderAndWarns()
    {
        // 2026-08-03要望対応: 参照先テンプレートが見つからない場合も、以前のように例外で中断せず、
        // keyCtrl(パターン1)だけプレースホルダ(["?"])で埋めて処理を継続する(他の項目は
        // 略記を使っていないため通常通り取り込まれる)。
        KeyTemplate Resolver(string keyTypeId) => throw new FileNotFoundException($"テンプレートが見つかりません: temp_{keyTypeId}.json");

        var text = "|keyCtrltest=F1,F2,F3,F4,Space$5_0|\n|charatest=a,b,c,d,e$a,b,c,d,e|\n" +
                   "|colortest=0,0,0,0,1$0,0,0,0,1|\n|postest=0,1,2,3,4$0,1,2,3,4|\n" +
                   "|divtest=5,5$5,5|\n|blanktest=50$50|\n" +
                   "|scrolltest=Default::1,1,1,1,1$Default::1,1,1,1,1|\n|stepRtntest=0,45,135,180,giko$0,45,135,180,giko|";

        var result = CustomKeyTemplateImporter.Import(text, KeyboardLayout.Us, templateResolver: Resolver);

        Assert.Contains(result.Warnings, w => w.Contains("5_0") && w.Contains("見つかりません"));
        var pattern1 = result.Template.WithPattern(1);
        Assert.All(pattern1.Lanes, l => Assert.Equal(["?"], l.KeyAssign));
        Assert.Equal(5, pattern1.DivideCnt + 1); // divの値(5,5)は正常に取り込まれている
    }

    [Fact]
    public void Import_WarnsWhenCharaHeaderMissing()
    {
        var template = SingleLaneTemplate(["E"]);
        var exported = CustomKeyTemplateExporter.Export(template);
        // chara行を除去(手編集で省略されたケースを模擬)
        var withoutChara = string.Join("\n", exported.Split('\n').Where(l => !l.Contains("|charart=")));

        var result = CustomKeyTemplateImporter.Import(withoutChara, KeyboardLayout.Us);

        Assert.Contains(result.Warnings, w => w.Contains("chara"));
        Assert.Equal("lane1", result.Template.Lanes[0].DataName);
    }
}
