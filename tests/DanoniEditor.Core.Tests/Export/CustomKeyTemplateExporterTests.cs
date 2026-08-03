using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests.Export;

/// <summary>CustomKeyTemplateExporter(2026-08-02、本体互換カスタムキー定義の書き出し)のテスト。
/// テスト用テンプレート(TestData/EditingTemplate/temp_5.json、keyAssign="S/D/F/J/Space"、
/// 全レーンscrollDirection="down")を基準に、stepRtnの数値/文字列混在・scrollの1/-1変換等を検証する。
/// 矢印記号("←"等)→英語キー名変換は、テスト用フィクスチャに矢印記号のレーンが無いため、
/// 別途このテストファイル内で組み立てた合成テンプレートで検証する。</summary>
public class CustomKeyTemplateExporterTests
{
    private static KeyTemplate Load5Key() => TestFixtures.Repository().Get("5");

    [Fact]
    public void Export_UsesKeyTypeIdAsHeaderSuffix()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        Assert.Contains("|keyCtrl5=", text);
        Assert.Contains("|keyName5=5key|", text);
    }

    [Fact]
    public void Export_KeyCtrl_PassesThroughNonArrowKeyAssignAsIs()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        // temp_5.json(テスト用)のkeyAssignは S/D/F/J/Space の順(既に本体互換の値のため変換不要)
        Assert.Contains("|keyCtrl5=S,D,F,J,Space|", text);
    }

    [Fact]
    public void Export_StepRtn_UsesAngleForArrowAndGraphicNameOtherwise()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        // left/down/up/right=arrow(角度0,-90,90,180)、space=onigiri(NoteGraphic名)
        Assert.Contains("|stepRtn5=0,-90,90,180,onigiri|", text);
    }

    [Fact]
    public void Export_Scroll_MapsDownToOneAndUpToMinusOne()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        // テスト用temp_5.jsonは全レーンscrollDirection="down"(2026-08-02: 本体仕様上"名前::値"が必須)
        Assert.Contains("|scroll5=Default::1,1,1,1,1|", text);
    }

    [Fact]
    public void Export_Chara_UsesLaneDataName()
    {
        // 2026-08-02: charaは本体が{chara}_dataという変数名で譜面データを読むため、
        // dos.txt側のDataNameと一致させる必要がある(省略時デフォルト名は一致しないため不可)。
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        Assert.Contains("|chara5=left,down,up,right,space|", text);
    }

    [Fact]
    public void Export_OmitsKeyHelpWhenNotProvided()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template);
        Assert.DoesNotContain("keyHelpJa", text);
        Assert.DoesNotContain("keyHelpEn", text);
    }

    [Fact]
    public void Export_IncludesKeyHelpWhenProvided()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template, keyHelpJa: "せつめい", keyHelpEn: "desc");
        Assert.Contains("|keyHelpJa5=せつめい|", text);
        Assert.Contains("|keyHelpEn5=desc|", text);
    }

    [Fact]
    public void Export_WhitespaceOnlyKeyHelp_IsTreatedAsOmitted()
    {
        var template = Load5Key();
        var text = CustomKeyTemplateExporter.Export(template, keyHelpJa: "   ", keyHelpEn: null);
        Assert.DoesNotContain("keyHelpJa", text);
        Assert.DoesNotContain("keyHelpEn", text);
    }

    private static KeyTemplate SyntheticArrowTemplate() => new()
    {
        KeyTypeId = "zz",
        KeyTypeName = "テスト用",
        KeyCount = 5,
        Blank = 50,
        DivideCnt = 4,
        PosMax = 5,
        Lanes =
        [
            new LaneDef { LaneId = "left", DataName = "left", DisplayOrder = 0, KeyAssign = ["←"], ColorGroup = 0, PosIndex = 0, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 0 },
            new LaneDef { LaneId = "down", DataName = "down", DisplayOrder = 1, KeyAssign = ["↓"], ColorGroup = 0, PosIndex = 1, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = -90, EngineLaneNum = 1 },
            new LaneDef { LaneId = "up", DataName = "up", DisplayOrder = 2, KeyAssign = ["↑"], ColorGroup = 0, PosIndex = 2, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 90, EngineLaneNum = 2 },
            new LaneDef { LaneId = "right", DataName = "right", DisplayOrder = 3, KeyAssign = ["→"], ColorGroup = 0, PosIndex = 3, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 180, EngineLaneNum = 3 },
            new LaneDef { LaneId = "space", DataName = "space", DisplayOrder = 4, KeyAssign = ["Space"], ColorGroup = 2, PosIndex = 4, ScrollDirection = "down", NoteGraphic = "onigiri", RotationAngle = 0, EngineLaneNum = 4 },
        ],
    };

    [Fact]
    public void Export_ConvertsKnownArrowSymbolsToEngineKeyNames()
    {
        var text = CustomKeyTemplateExporter.Export(SyntheticArrowTemplate());
        Assert.Contains("|keyCtrlzz=Left,Down,Up,Right,Space|", text);
    }

    [Fact]
    public void Export_MultipleKeyAssignInOneLane_JoinsWithSlash()
    {
        var template = SyntheticArrowTemplate();
        var lanes = template.Lanes.ToList();
        lanes[4] = lanes[4] switch { var l => new LaneDef
        {
            LaneId = l.LaneId, DataName = l.DataName, DisplayOrder = l.DisplayOrder,
            KeyAssign = ["Space", "E"], ColorGroup = l.ColorGroup, PosIndex = l.PosIndex,
            ScrollDirection = l.ScrollDirection, NoteGraphic = l.NoteGraphic,
            RotationAngle = l.RotationAngle, EngineLaneNum = l.EngineLaneNum,
        } };
        var multi = new KeyTemplate
        {
            KeyTypeId = template.KeyTypeId, KeyTypeName = template.KeyTypeName, KeyCount = template.KeyCount,
            Blank = template.Blank, DivideCnt = template.DivideCnt, PosMax = template.PosMax, Lanes = lanes,
        };
        var text = CustomKeyTemplateExporter.Export(multi);
        Assert.Contains("|keyCtrlzz=Left,Down,Up,Right,Space/E|", text);
    }

    private static KeyTemplate SyntheticNumpadTemplate() => new()
    {
        KeyTypeId = "9t",
        KeyTypeName = "テンキー式9key",
        KeyCount = 9,
        Blank = 50,
        DivideCnt = 9,
        PosMax = 9,
        Lanes =
        [
            new LaneDef { LaneId = "left", DataName = "left", DisplayOrder = 0, KeyAssign = ["Num7"], ColorGroup = 0, PosIndex = 0, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 45, EngineLaneNum = 0 },
            new LaneDef { LaneId = "down", DataName = "down", DisplayOrder = 1, KeyAssign = ["Num8"], ColorGroup = 0, PosIndex = 1, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 90, EngineLaneNum = 1 },
            new LaneDef { LaneId = "up", DataName = "up", DisplayOrder = 2, KeyAssign = ["Num9"], ColorGroup = 0, PosIndex = 2, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 135, EngineLaneNum = 2 },
            new LaneDef { LaneId = "right", DataName = "right", DisplayOrder = 3, KeyAssign = ["Num4"], ColorGroup = 0, PosIndex = 3, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 3 },
            new LaneDef { LaneId = "space", DataName = "space", DisplayOrder = 4, KeyAssign = ["Num5"], ColorGroup = 2, PosIndex = 4, ScrollDirection = "down", NoteGraphic = "onigiri", RotationAngle = 0, EngineLaneNum = 4 },
            new LaneDef { LaneId = "sleft", DataName = "sleft", DisplayOrder = 5, KeyAssign = ["Num6"], ColorGroup = 0, PosIndex = 5, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 180, EngineLaneNum = 5 },
            new LaneDef { LaneId = "sdown", DataName = "sdown", DisplayOrder = 6, KeyAssign = ["Num1"], ColorGroup = 0, PosIndex = 6, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = -45, EngineLaneNum = 6 },
            new LaneDef { LaneId = "sup", DataName = "sup", DisplayOrder = 7, KeyAssign = ["Num2"], ColorGroup = 0, PosIndex = 7, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = -90, EngineLaneNum = 7 },
            new LaneDef { LaneId = "sright", DataName = "sright", DisplayOrder = 8, KeyAssign = ["Num3"], ColorGroup = 0, PosIndex = 8, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = -135, EngineLaneNum = 8 },
        ],
    };

    [Fact]
    public void Export_ConvertsNumpadShorthandLabelsToEngineNumpadNames()
    {
        // 2026-08-02: KeyLabelMapperの短縮表記("Num5"等)は本体g_kCdNの命名("Numpad5"等)と
        // 食い違うため変換が必要。無変換だとgetKeyCtrlValが解決できずNaNになる不具合があった。
        var text = CustomKeyTemplateExporter.Export(SyntheticNumpadTemplate());
        Assert.Contains(
            "|keyCtrl9t=Numpad7,Numpad8,Numpad9,Numpad4,Numpad5,Numpad6,Numpad1,Numpad2,Numpad3|",
            text);
    }

    [Fact]
    public void Export_ConvertsNumpadOperatorShorthandLabels()
    {
        var template = SyntheticNumpadTemplate();
        var lanes = template.Lanes.ToList();
        lanes[4] = new LaneDef
        {
            LaneId = lanes[4].LaneId, DataName = lanes[4].DataName, DisplayOrder = lanes[4].DisplayOrder,
            KeyAssign = ["Num+", "Num-", "Num*", "Num/", "Num."], ColorGroup = lanes[4].ColorGroup,
            PosIndex = lanes[4].PosIndex, ScrollDirection = lanes[4].ScrollDirection,
            NoteGraphic = lanes[4].NoteGraphic, RotationAngle = lanes[4].RotationAngle,
            EngineLaneNum = lanes[4].EngineLaneNum,
        };
        var modified = new KeyTemplate
        {
            KeyTypeId = template.KeyTypeId, KeyTypeName = template.KeyTypeName, KeyCount = template.KeyCount,
            Blank = template.Blank, DivideCnt = template.DivideCnt, PosMax = template.PosMax, Lanes = lanes,
        };
        var text = CustomKeyTemplateExporter.Export(modified);
        Assert.Contains("NumpadAdd/NumpadSubtract/NumpadMultiply/NumpadDivide/NumpadDecimal", text);
    }

    private static KeyTemplate SyntheticSymbolTemplate() => new()
    {
        KeyTypeId = "sym",
        KeyTypeName = "記号キーテスト",
        KeyCount = 12,
        Blank = 50,
        DivideCnt = 11,
        PosMax = 12,
        Lanes =
        [
            new LaneDef { LaneId = "l0", DataName = "l0", DisplayOrder = 0, KeyAssign = ["<"], ColorGroup = 0, PosIndex = 0, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 0 },
            new LaneDef { LaneId = "l1", DataName = "l1", DisplayOrder = 1, KeyAssign = [">"], ColorGroup = 0, PosIndex = 1, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 1 },
            new LaneDef { LaneId = "l2", DataName = "l2", DisplayOrder = 2, KeyAssign = [";"], ColorGroup = 0, PosIndex = 2, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 2 },
            new LaneDef { LaneId = "l3", DataName = "l3", DisplayOrder = 3, KeyAssign = [":"], ColorGroup = 0, PosIndex = 3, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 3 },
            new LaneDef { LaneId = "l4", DataName = "l4", DisplayOrder = 4, KeyAssign = ["@"], ColorGroup = 0, PosIndex = 4, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 4 },
            new LaneDef { LaneId = "l5", DataName = "l5", DisplayOrder = 5, KeyAssign = ["["], ColorGroup = 0, PosIndex = 5, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 5 },
            new LaneDef { LaneId = "l6", DataName = "l6", DisplayOrder = 6, KeyAssign = ["]"], ColorGroup = 0, PosIndex = 6, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 6 },
            new LaneDef { LaneId = "l7", DataName = "l7", DisplayOrder = 7, KeyAssign = ["-"], ColorGroup = 0, PosIndex = 7, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 7 },
            new LaneDef { LaneId = "l8", DataName = "l8", DisplayOrder = 8, KeyAssign = ["="], ColorGroup = 0, PosIndex = 8, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 8 },
            new LaneDef { LaneId = "l9", DataName = "l9", DisplayOrder = 9, KeyAssign = ["/"], ColorGroup = 0, PosIndex = 9, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 9 },
            new LaneDef { LaneId = "l10", DataName = "l10", DisplayOrder = 10, KeyAssign = ["\\"], ColorGroup = 0, PosIndex = 10, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 10 },
            new LaneDef { LaneId = "l11", DataName = "l11", DisplayOrder = 11, KeyAssign = ["`"], ColorGroup = 0, PosIndex = 11, ScrollDirection = "down", NoteGraphic = "arrow", RotationAngle = 0, EngineLaneNum = 11 },
        ],
    };

    [Fact]
    public void Export_ConvertsPunctuationSymbolsToEngineKeyNames()
    {
        // 2026-08-02不具合修正: "<"等の記号キーが無変換のまま出力され、本体のKeyCtrlCodeList
        // (公式wiki)が期待するKeyboardEvent.code名と食い違って反応しない不具合があった。
        var text = CustomKeyTemplateExporter.Export(SyntheticSymbolTemplate());
        Assert.Contains(
            "|keyCtrlsym=Comma,Period,Semicolon,Quote,BracketLeft,BracketLeft,BracketRight,Minus,Equal,Slash,Backslash,Backquote|",
            text);
    }

    [Fact]
    public void Export_JisLayout_ShiftsBracketAndBackslashMappings()
    {
        // 2026-08-02要望対応: JIS配列では"["→BracketRight、"]"→Backslashへ1つずつずれる
        // (本体KeyCtrlCodeList: JIS @→BracketLeft/[→BracketRight/]→Backslash)。
        // "@"は元々JIS配列専用のラベルのため変化せず、"\"はJIS配列に対応する記号キーが無いため
        // 引き続きUS配列の意味(Backslash)のまま扱う。
        var template = SyntheticSymbolTemplate();
        var jis = new KeyTemplate
        {
            KeyTypeId = template.KeyTypeId, KeyTypeName = template.KeyTypeName, KeyCount = template.KeyCount,
            Blank = template.Blank, DivideCnt = template.DivideCnt, PosMax = template.PosMax, Lanes = template.Lanes,
            KeyboardLayout = KeyboardLayout.Jis,
        };
        var text = CustomKeyTemplateExporter.Export(jis);
        Assert.Contains(
            "|keyCtrlsym=Comma,Period,Semicolon,Quote,BracketLeft,BracketRight,Backslash,Minus,Equal,Slash,Backslash,`|",
            text);
    }

    [Fact]
    public void Export_ConvertsEscAndCtrlAbbreviationsToEngineNames()
    {
        // Esc→Escape、Ctrl→Control(本体略称に"Ctrl"は無く"Control"/"ControlLeft"のみ有効)。
        // Alt/Shift/Tab/Backspace/Delete/Insert/Home/End/PageUp/PageDownは略称がそのまま
        // 本体互換のため変換不要(この2つだけが要変換)。
        var template = SyntheticSymbolTemplate();
        var lanes = template.Lanes.ToList();
        lanes[0] = new LaneDef
        {
            LaneId = lanes[0].LaneId, DataName = lanes[0].DataName, DisplayOrder = lanes[0].DisplayOrder,
            KeyAssign = ["Esc"], ColorGroup = lanes[0].ColorGroup, PosIndex = lanes[0].PosIndex,
            ScrollDirection = lanes[0].ScrollDirection, NoteGraphic = lanes[0].NoteGraphic,
            RotationAngle = lanes[0].RotationAngle, EngineLaneNum = lanes[0].EngineLaneNum,
        };
        lanes[1] = new LaneDef
        {
            LaneId = lanes[1].LaneId, DataName = lanes[1].DataName, DisplayOrder = lanes[1].DisplayOrder,
            KeyAssign = ["Ctrl"], ColorGroup = lanes[1].ColorGroup, PosIndex = lanes[1].PosIndex,
            ScrollDirection = lanes[1].ScrollDirection, NoteGraphic = lanes[1].NoteGraphic,
            RotationAngle = lanes[1].RotationAngle, EngineLaneNum = lanes[1].EngineLaneNum,
        };
        var modified = new KeyTemplate
        {
            KeyTypeId = template.KeyTypeId, KeyTypeName = template.KeyTypeName, KeyCount = template.KeyCount,
            Blank = template.Blank, DivideCnt = template.DivideCnt, PosMax = template.PosMax, Lanes = lanes,
        };
        var text = CustomKeyTemplateExporter.Export(modified);
        Assert.Contains("|keyCtrlsym=Escape,Control,", text);
    }

    // =====================================================================
    // キーパターン(ExtraPatterns)対応(2026-08-02要望対応)
    // =====================================================================

    /// <summary>SyntheticArrowTemplate()に、値を全て変えたパターン1件を追加した2パターン版。
    /// KeyAssignは矢印記号→"S/D/F/J/Space"(既に本体互換)、ColorGroup/PosIndex/ScrollDirection/
    /// NoteGraphic/RotationAngleも全レーンぶん変更し、Blank/DivideCnt/PosMaxも変える(すべての
    /// フィールドがパターンごとに正しく$区切りされることを検証するため、あえて全部変える)。</summary>
    private static KeyTemplate TwoPatternTemplate()
    {
        var baseTemplate = SyntheticArrowTemplate();
        var overrideLanes = new List<LanePatternOverride>
        {
            new() { KeyAssign = ["S"], ColorGroup = 1, PosIndex = 0, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 10 },
            new() { KeyAssign = ["D"], ColorGroup = 1, PosIndex = 1, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 20 },
            new() { KeyAssign = ["F"], ColorGroup = 1, PosIndex = 2, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 30 },
            new() { KeyAssign = ["J"], ColorGroup = 1, PosIndex = 3, ScrollDirection = "up", NoteGraphic = "arrow", RotationAngle = 40 },
            new() { KeyAssign = ["Space"], ColorGroup = 3, PosIndex = 4, ScrollDirection = "up", NoteGraphic = "giko", RotationAngle = 0 },
        };
        return new KeyTemplate
        {
            KeyTypeId = baseTemplate.KeyTypeId, KeyTypeName = baseTemplate.KeyTypeName, KeyCount = baseTemplate.KeyCount,
            Blank = baseTemplate.Blank, DivideCnt = baseTemplate.DivideCnt, PosMax = baseTemplate.PosMax,
            Lanes = baseTemplate.Lanes,
            ExtraPatterns =
            [
                new KeyPattern { Blank = 60, DivideCnt = 3, PosMax = 4, LaneOverrides = overrideLanes },
            ],
        };
    }

    [Fact]
    public void Export_SinglePattern_OutputsWithoutDollarSeparator()
    {
        // ExtraPatterns未使用のテンプレートは、これまで通り$を含まない単一値のまま出力される
        // (2026-08-02のキーパターン対応で既存の出力を変えないことの確認)。
        var text = CustomKeyTemplateExporter.Export(SyntheticArrowTemplate());
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public void Export_MultiPattern_KeyCtrl_JoinsPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|keyCtrlzz=Left,Down,Up,Right,Space$S,D,F,J,Space|", text);
    }

    [Fact]
    public void Export_MultiPattern_Color_JoinsPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|colorzz=0,0,0,0,2$1,1,1,1,3|", text);
    }

    [Fact]
    public void Export_MultiPattern_Pos_JoinsPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|poszz=0,1,2,3,4$0,1,2,3,4|", text);
    }

    [Fact]
    public void Export_MultiPattern_Scroll_JoinsPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        // パターン0は全レーンdown(=1)、パターン1は全レーンup(=-1)(2026-08-02: "名前::値"必須)
        Assert.Contains("|scrollzz=Default::1,1,1,1,1$Default::-1,-1,-1,-1,-1|", text);
    }

    [Fact]
    public void Export_MultiPattern_StepRtn_JoinsPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|stepRtnzz=0,-90,90,180,onigiri$10,20,30,40,giko|", text);
    }

    [Fact]
    public void Export_MultiPattern_DivDivMaxBlank_JoinPatternsWithDollar()
    {
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        // 2026-08-02不具合修正: divMax{X}という独立ヘッダーは本体側が読まないため出力しない。
        // div{X}の値自体を"div,divMax"のカンマ2つ組(divはDivideCnt+1)で1ヘッダーに出力する。
        Assert.Contains("|divzz=5,5$4,4|", text);
        Assert.DoesNotContain("divMaxzz", text);
        Assert.Contains("|blankzz=50$60|", text);
    }

    [Fact]
    public void Export_MultiPattern_Chara_RepeatsSameLaneDataNamesPerPattern()
    {
        // charaはエディタのモデル上パターン非依存(LaneDef.DataName)のため、
        // 全パターンで同じ値を$区切りで繰り返す(本体側は本来パターンごとの指定に対応しているが、
        // 「変わりようが無い」ことを明示するため単一値へ省略しない方針、2026-08-02確定仕様)。
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|charazz=left,down,up,right,space$left,down,up,right,space|", text);
    }

    [Fact]
    public void Export_MultiPattern_KeyName_StaysSingleValue()
    {
        // keyNameはパターン非依存の項目(本体仕様上X_Y形式が存在しない)のため$分割しない。
        var text = CustomKeyTemplateExporter.Export(TwoPatternTemplate());
        Assert.Contains("|keyNamezz=テスト用|", text);
        Assert.DoesNotContain("keyNamezz=テスト用$", text);
    }

    [Fact]
    public void Export_ThreePattern_JoinsAllPatternsInOrder()
    {
        var two = TwoPatternTemplate();
        var thirdPattern = new KeyPattern
        {
            Blank = 70,
            DivideCnt = 2,
            PosMax = 3,
            LaneOverrides =
            [
                new() { KeyAssign = ["1"], ColorGroup = 5, PosIndex = 0, ScrollDirection = "down", NoteGraphic = "iyo", RotationAngle = 0 },
                new() { KeyAssign = ["2"], ColorGroup = 5, PosIndex = 1, ScrollDirection = "down", NoteGraphic = "iyo", RotationAngle = 0 },
                new() { KeyAssign = ["3"], ColorGroup = 5, PosIndex = 2, ScrollDirection = "down", NoteGraphic = "iyo", RotationAngle = 0 },
                new() { KeyAssign = ["4"], ColorGroup = 5, PosIndex = 3, ScrollDirection = "down", NoteGraphic = "iyo", RotationAngle = 0 },
                new() { KeyAssign = ["5"], ColorGroup = 5, PosIndex = 4, ScrollDirection = "down", NoteGraphic = "iyo", RotationAngle = 0 },
            ],
        };
        var three = new KeyTemplate
        {
            KeyTypeId = two.KeyTypeId, KeyTypeName = two.KeyTypeName, KeyCount = two.KeyCount,
            Blank = two.Blank, DivideCnt = two.DivideCnt, PosMax = two.PosMax, Lanes = two.Lanes,
            ExtraPatterns = [.. two.ExtraPatterns, thirdPattern],
        };
        var text = CustomKeyTemplateExporter.Export(three);
        Assert.Contains("|keyCtrlzz=Left,Down,Up,Right,Space$S,D,F,J,Space$1,2,3,4,5|", text);
        Assert.Contains("|divzz=5,5$4,4$3,3|", text);
    }
}
