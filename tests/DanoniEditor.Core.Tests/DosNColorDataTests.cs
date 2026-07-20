using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>ncolor_data(色編集モード、2026-07-23)のエクスポート/インポートのテスト。
/// テスト用5keyテンプレート(TestData/EditingTemplate/temp_5.json)はengineLaneNumが
/// レーン0〜4=そのままdisplayOrderと一致する(left=0, down=1, ...)。</summary>
public class DosNColorDataTests
{
    private const long T = TimingEngine.TicksPerBeat / 48;

    private static ChartProject NewProject()
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        p.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "Normal"));
        return p;
    }

    [Fact]
    public void Export_NoteColor_WritesFrameEngineLaneNumAndColor()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0]; // engineLaneNum=0
        lane.Notes.Add(48 * T); // ちょうど1拍目
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        long expectedFrame = (long)Math.Round(project.CreateTimingEngine().TickToFrame(48 * T));
        Assert.Contains($"|ncolor_data={expectedFrame},0,#ff0000|", text);
    }

    [Fact]
    public void Export_FreezeColors_WritesNormalAndNormalBarSeparately()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[1]; // engineLaneNum=1
        lane.Freezes.Add(new FreezeNote(0, 48 * T));
        lane.ColorOverrides.Add(new NColorEntry(0, "#00ff00", "#0000ff"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("1:Normal,#00ff00", text);
        Assert.Contains("1:NormalBar,#0000ff", text);
    }

    [Fact]
    public void Export_NoColorOverrides_OmitsNColorData()
    {
        var project = NewProject();
        project.Tabs[0].Lanes[0].Notes.Add(48 * T);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.DoesNotContain("ncolor_data", text);
    }

    [Fact]
    public void RoundTrip_NoteColor_Restores()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[2]; // engineLaneNum=2
        lane.Notes.Add(96 * T);
        lane.ColorOverrides.Add(new NColorEntry(96 * T, "#abcdef", null));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[2].ColorOverrides;
        var entry = Assert.Single(restored);
        Assert.Equal(96 * T, entry.Tick);
        Assert.Equal("#abcdef", entry.Color);
        Assert.Null(entry.BandColor);
    }

    [Fact]
    public void RoundTrip_FreezeColors_Restores()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[3]; // engineLaneNum=3
        lane.Freezes.Add(new FreezeNote(48 * T, 192 * T));
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#111111", "#222222"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[3].ColorOverrides;
        var entry = Assert.Single(restored);
        Assert.Equal(48 * T, entry.Tick);
        Assert.Equal("#111111", entry.Color);
        Assert.Equal("#222222", entry.BandColor);
    }

    [Fact]
    public void Import_RangeColorNo_IsIgnoredWithWarning()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData + "\n|ncolor_data=12,0...3,#ffffff|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        Assert.All(back.Project.Tabs[0].Lanes, l => Assert.Empty(l.ColorOverrides));
        Assert.Contains(back.Warnings, w => w.Contains("ncolor_data"));
    }

    [Fact]
    public void Import_CommentRow_IsIgnoredWithoutWarning()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        // コメント行(ColorNo="-")+正規の個別色変化行を1行ずつ(手書き想定の複数行形式)
        var text = difData + "\n|left_data=12|\n|ncolor_data=\n12,-,テストコメント\n12,0,#ff0000\n|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var entry = Assert.Single(back.Project.Tabs[0].Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
        Assert.DoesNotContain(back.Warnings, w => w.Contains("ncolor_data"));
    }

    // --- 2026-07-24: ncolor_data永続状態モデルへの再設計テスト ---
    // (本家仕様: ncolor_dataは「指定フレーム以降ずっと持続する」永続的な色状態変更のため、
    // 1オーバーライド=1行の単純な出力では「着色ノートの後ろに置いた無着色ノートまで意図せず
    // 着色される」問題が起きる。DosExporter/DosImporterを状態差分/状態復元方式へ書き換えた)。

    private static int CountNColorEntries(string text)
    {
        var line = text.Split('\n').Single(l => l.Contains("|ncolor_data="));
        var body = line.Trim().TrimStart('|').Substring("ncolor_data=".Length).TrimEnd('|');
        var tokens = body.Split(',', StringSplitOptions.TrimEntries);
        return tokens.Length / 3;
    }

    [Fact]
    public void Export_ConsecutiveSameColorNotes_EmitsOnlyOneTransition()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.AddRange([48 * T, 96 * T, 144 * T]);
        foreach (var t in lane.Notes)
            lane.ColorOverrides.Add(new NColorEntry(t, "#ff0000", null));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        // 3ノート全て同色なので、状態変化点は先頭ノートの1件だけになるはず
        Assert.Equal(1, CountNColorEntries(text));
        long expectedFrame = (long)Math.Round(project.CreateTimingEngine().TickToFrame(48 * T));
        Assert.Contains($"|ncolor_data={expectedFrame},0,#ff0000|", text);
    }

    [Fact]
    public void Export_ColoredPlainColored_EmitsRevertToDefaultBetween()
    {
        var project = NewProject();
        var tab = project.Tabs[0];
        var lane = tab.Lanes[0];
        lane.Notes.AddRange([48 * T, 96 * T, 144 * T]);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null));
        // 96*T(2番目)は無着色のまま
        lane.ColorOverrides.Add(new NColorEntry(144 * T, "#00ff00", null));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        // 着色→無着色→着色の3区間、それぞれで状態が変わるため3件出力されるはず
        Assert.Equal(3, CountNColorEntries(text));
        var laneDef = repo.Get("5").Lanes[0];
        string defaultHex = ColorDefaults.ResolveSetColorHex(tab, project, laneDef.ColorGroup);
        long frame2 = (long)Math.Round(project.CreateTimingEngine().TickToFrame(96 * T));
        Assert.Contains($"{frame2},0,{defaultHex}", text);
    }

    [Fact]
    public void RoundTrip_ColoredPlainColored_MiddleNoteStaysUncolored()
    {
        // 2026-07-24修正前の不具合: 着色ノートの後ろに置いた無着色ノートまでdos.txt上は
        // 意図せず着色されてしまっていた。この往復テストで「間のノートには一切
        // ColorOverridesが付かない」ことを保証する。
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.AddRange([48 * T, 96 * T, 144 * T]);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null));
        lane.ColorOverrides.Add(new NColorEntry(144 * T, "#00ff00", null));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[0].ColorOverrides;
        Assert.Equal(2, restored.Count);
        Assert.Equal("#ff0000", restored.Single(e => e.Tick == 48 * T).Color);
        Assert.Equal("#00ff00", restored.Single(e => e.Tick == 144 * T).Color);
        Assert.DoesNotContain(restored, e => e.Tick == 96 * T);
    }

    [Fact]
    public void Import_TransitionBeforeNotes_AppliesToBothLaterNotesWithoutExactTickMatch()
    {
        // ノート自身のtickに一致しない色変化点(手書き想定)でも、以後のノートへ持続的に
        // 適用されることを確認する(2026-07-24: 以前はtick一致必須でこのケースは
        // 警告付きスキップされていた)。
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData + "\n|left_data=30,60|\n|ncolor_data=1,0,#ff0000|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[0].ColorOverrides;
        Assert.Equal(2, restored.Count);
        Assert.All(restored, e => Assert.Equal("#ff0000", e.Color));
    }

    [Fact]
    public void Import_OrphanedTransition_NoObjectsInLane_WarnsAndCreatesNoOverride()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        // engineLaneNum=2のレーンには一切ノート/フリーズを置かない
        var text = difData + "\n|ncolor_data=12,2,#ff0000|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        Assert.All(back.Project.Tabs[0].Lanes, l => Assert.Empty(l.ColorOverrides));
        Assert.Contains(back.Warnings, w => w.Contains("ncolor_data"));
    }

    // --- 2026-07-24: allFlg(即時適用/全体色変化)対応テスト ---

    [Fact]
    public void Export_AllFlagNote_AppendsAllToken()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.Add(48 * T);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null, AllFlag: true));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        long expectedFrame = (long)Math.Round(project.CreateTimingEngine().TickToFrame(48 * T));
        Assert.Contains($"|ncolor_data={expectedFrame},0,#ff0000,all|", text);
    }

    [Fact]
    public void Export_NonAllFlagNote_OmitsAllToken()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.Add(48 * T);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null, AllFlag: false));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        long expectedFrame = (long)Math.Round(project.CreateTimingEngine().TickToFrame(48 * T));
        Assert.Contains($"|ncolor_data={expectedFrame},0,#ff0000|", text);
        Assert.DoesNotContain(",all", text);
    }

    [Fact]
    public void Export_RevertToDefaultAfterAllFlagNote_DoesNotCarryAllFlag()
    {
        // 基本色への自動復帰は、直前が即時適用(all)であっても復帰行自体は個別色変化として出力する
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.AddRange([48 * T, 96 * T]);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null, AllFlag: true));
        // 96*Tは無着色のまま(基本色への復帰行が自動生成される)

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);
        var line = text.Split('\n').Single(l => l.Contains("|ncolor_data="));

        // "all"トークンの出現は1回だけ(先頭の着色行のみ)のはず
        Assert.Equal(1, line.Split(',').Count(t => t.TrimEnd('|') == "all"));
    }

    [Fact]
    public void RoundTrip_AllFlagNote_Restores()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0];
        lane.Notes.Add(48 * T);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#ff0000", null, AllFlag: true));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var entry = Assert.Single(back.Project.Tabs[0].Lanes[0].ColorOverrides);
        Assert.Equal("#ff0000", entry.Color);
        Assert.True(entry.AllFlag);
    }

    [Fact]
    public void Import_HandWrittenAllFlagRow_IsParsedAndAppliedGoingForward()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData + "\n|left_data=12,24|\n|ncolor_data=12,0,#ff0000,all|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[0].ColorOverrides;
        Assert.Equal(2, restored.Count);
        Assert.All(restored, e => { Assert.Equal("#ff0000", e.Color); Assert.True(e.AllFlag); });
    }

    // --- 2026-07-24: Hit/Shadow系トラック(ArrowShadow/NormalShadow/Hit/HitBar/HitShadow)対応テスト ---

    [Fact]
    public void Export_NoteShadowColor_WritesArrowShadowTargetPattern()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[0]; // engineLaneNum=0
        lane.Notes.Add(48 * T);
        lane.ColorOverrides.Add(new NColorEntry(48 * T, null, null, ShadowColor: "#ff00ff"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("0:ArrowShadow,#ff00ff", text);
    }

    [Fact]
    public void Export_FreezeHitColors_WritesHitHitBarHitShadowTargetPatterns()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[1]; // engineLaneNum=1
        lane.Freezes.Add(new FreezeNote(0, 48 * T));
        lane.ColorOverrides.Add(new NColorEntry(0, null, null,
            HitColor: "#101010", HitBarColor: "#202020", HitShadowColor: "#303030"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("1:Hit,#101010", text);
        Assert.Contains("1:HitBar,#202020", text);
        Assert.Contains("1:HitShadow,#303030", text);
    }

    [Fact]
    public void Export_FreezeNormalShadowColor_WritesNormalShadowTargetPattern()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[1];
        lane.Freezes.Add(new FreezeNote(0, 48 * T));
        lane.ColorOverrides.Add(new NColorEntry(0, null, null, ShadowColor: "#abcabc"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("1:NormalShadow,#abcabc", text);
    }

    [Fact]
    public void RoundTrip_NoteColorAndShadow_RestoresBothInOneEntry()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[2];
        lane.Notes.Add(96 * T);
        lane.ColorOverrides.Add(new NColorEntry(96 * T, "#ff0000", null, ShadowColor: "#00ff00"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[2].ColorOverrides;
        var entry = Assert.Single(restored); // Arrow+ArrowShadowが1エントリへ合成されるはず
        Assert.Equal("#ff0000", entry.Color);
        Assert.Equal("#00ff00", entry.ShadowColor);
    }

    [Fact]
    public void RoundTrip_FreezeAllSixTracks_RestoresIntoOneEntry()
    {
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[3];
        lane.Freezes.Add(new FreezeNote(48 * T, 192 * T));
        lane.ColorOverrides.Add(new NColorEntry(48 * T, "#111111", "#222222",
            ShadowColor: "#333333", HitColor: "#444444", HitBarColor: "#555555", HitShadowColor: "#666666"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restored = back.Project.Tabs[0].Lanes[3].ColorOverrides;
        var entry = Assert.Single(restored); // 6トラック全てが1つのNColorEntryへ合成されるはず
        Assert.Equal("#111111", entry.Color);
        Assert.Equal("#222222", entry.BandColor);
        Assert.Equal("#333333", entry.ShadowColor);
        Assert.Equal("#444444", entry.HitColor);
        Assert.Equal("#555555", entry.HitBarColor);
        Assert.Equal("#666666", entry.HitShadowColor);
    }

    [Fact]
    public void RoundTrip_OnlyHitColorSet_OtherFreezeFieldsStayNull()
    {
        // 一部トラックのみ差分がある場合、無関係なフィールドは合成エントリでもnullのままのはず
        var project = NewProject();
        var lane = project.Tabs[0].Lanes[3];
        lane.Freezes.Add(new FreezeNote(48 * T, 192 * T));
        lane.ColorOverrides.Add(new NColorEntry(48 * T, null, null, HitColor: "#444444"));

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var entry = Assert.Single(back.Project.Tabs[0].Lanes[3].ColorOverrides);
        Assert.Null(entry.Color);
        Assert.Null(entry.BandColor);
        Assert.Null(entry.ShadowColor);
        Assert.Equal("#444444", entry.HitColor);
        Assert.Null(entry.HitBarColor);
        Assert.Null(entry.HitShadowColor);
    }

    [Fact]
    public void Import_UnsupportedTargetPattern_IsIgnoredWithWarning()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        // "Frz"のような略記や本家仕様上その他の未対応TargetPatternは無視されるべき
        var text = difData + "\n|left_data=12|\n|ncolor_data=12,0:Frz,#ff0000|\n" +
                   "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        Assert.All(back.Project.Tabs[0].Lanes, l => Assert.Empty(l.ColorOverrides));
        Assert.Contains(back.Warnings, w => w.Contains("ncolor_data"));
    }
}
