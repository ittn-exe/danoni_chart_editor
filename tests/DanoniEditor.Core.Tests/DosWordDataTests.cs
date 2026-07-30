using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>歌詞表示(word_data/wordRev_data、仕様dos-e0003-wordData、2026-07-23、TBD 4)の
/// エクスポート/インポートのテスト。</summary>
public class DosWordDataTests
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
    public void Export_LyricsEntry_WritesFramePositionAndText()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(48 * T, 0, WordEntryKind.Lyrics, "テスト歌詞"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|word_data=", text);
        Assert.Contains("30,0,テスト歌詞", text); // BPM120: 1拍(48*T tick)=30frame(60fps/(120/60))
    }

    [Fact]
    public void Export_ControlEntryWithFadeFrame_WritesFourFields()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Control, "[fadein]", 60));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("0,0,[fadein],60", text);
    }

    [Fact]
    public void Export_ControlEntryWithoutFadeFrame_WritesThreeFields()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Control, "[center]"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|word_data=0,0,[center]|", text); // FadeFrame無し=3項目のみ
    }

    [Fact]
    public void Export_CommentEntry_WritesHyphenPosition()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Comment, "テストコメント"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("0,-,テストコメント", text);
    }

    [Fact]
    public void Export_ReverseLane_WritesToWordRevData()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞(Rev)", IsReverse = true };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Lyrics, "リバース歌詞"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|wordRev_data=", text);
        Assert.DoesNotContain("|word_data=", text);
    }

    [Fact]
    public void Export_MultipleLanesSameReverseFlag_MergesIntoOneDataNameByFrame()
    {
        var project = NewProject();
        var lane1 = new WordLane { Name = "Aメロ" };
        lane1.Entries.Add(new WordEntry(96 * T, 0, WordEntryKind.Lyrics, "二番目"));
        var lane2 = new WordLane { Name = "サビ" };
        lane2.Entries.Add(new WordEntry(0, 0, WordEntryKind.Lyrics, "一番目"));
        project.Tabs[0].WordLanes.Add(lane1);
        project.Tabs[0].WordLanes.Add(lane2);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        var body = text[(text.IndexOf("|word_data=") + "|word_data=".Length)..];
        var firstRowIdx = body.IndexOf("一番目");
        var secondRowIdx = body.IndexOf("二番目");
        Assert.True(firstRowIdx >= 0 && secondRowIdx >= 0 && firstRowIdx < secondRowIdx); // frame昇順でマージ
    }

    [Fact]
    public void Export_NoWordLanes_OmitsWordData()
    {
        var project = NewProject();
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.DoesNotContain("word_data", text);
    }

    [Fact]
    public void RoundTrip_LyricsAndControlAndComment_PreservesAllFields()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Control, "[fadein]", 60));
        lane.Entries.Add(new WordEntry(48 * T, 0, WordEntryKind.Lyrics, "歌詞テキスト"));
        lane.Entries.Add(new WordEntry(96 * T, 1, WordEntryKind.Comment, "コメントです"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restoredLane = Assert.Single(back.Project.Tabs[0].WordLanes);
        Assert.False(restoredLane.IsReverse);
        Assert.Equal(3, restoredLane.Entries.Count);

        var control = restoredLane.Entries.Single(e => e.Kind == WordEntryKind.Control);
        Assert.Equal("[fadein]", control.Text);
        Assert.Equal(60, control.FadeFrame);

        var lyrics = restoredLane.Entries.Single(e => e.Kind == WordEntryKind.Lyrics);
        Assert.Equal("歌詞テキスト", lyrics.Text);
        Assert.Equal(48 * T, lyrics.Tick);

        var comment = restoredLane.Entries.Single(e => e.Kind == WordEntryKind.Comment);
        Assert.Equal("コメントです", comment.Text);
    }

    [Fact]
    public void RoundTrip_ReverseLane_ImportsWithIsReverseTrue()
    {
        var project = NewProject();
        var lane = new WordLane { Name = "歌詞(Rev)", IsReverse = true };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Lyrics, "リバース歌詞"));
        project.Tabs[0].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var restoredLane = Assert.Single(back.Project.Tabs[0].WordLanes);
        Assert.True(restoredLane.IsReverse);
        Assert.Equal("リバース歌詞", restoredLane.Entries.Single().Text);
    }

    [Fact]
    public void Import_HandWrittenMultiLineFormat_ParsesCorrectly()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData +
            "\n|word_data=\n1436,0,[fadein]\n1436,0,歌詞テキスト\n1638,0,[fadeout],120\n|\n" +
            "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var lane = Assert.Single(back.Project.Tabs[0].WordLanes);
        Assert.Equal(3, lane.Entries.Count);
        Assert.Contains(lane.Entries, e => e.Kind == WordEntryKind.Lyrics && e.Text == "歌詞テキスト");
        Assert.Contains(lane.Entries, e => e.Kind == WordEntryKind.Control && e.Text == "[fadein]" && e.FadeFrame is null);
        Assert.Contains(lane.Entries, e => e.Kind == WordEntryKind.Control && e.Text == "[fadeout]" && e.FadeFrame == 120);
    }

    [Fact]
    public void Import_PackedMultiEntryLine_ParsesAllEntriesWithoutFadeFrame()
    {
        // 2026-07-30確認: danoni_main.js(makeSpriteWordData)は1行に複数の(Frame,Position,Text)組を
        // カンマ区切りで詰め込む書式も許容する(この場合FadeFrameは付与されない)。
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData +
            "\n|word_data=\n0,0,一番目,30,1,二番目,60,0,三番目\n|\n" +
            "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var lane = Assert.Single(back.Project.Tabs[0].WordLanes);
        Assert.Equal(3, lane.Entries.Count);
        Assert.Contains(lane.Entries, e => e.Text == "一番目" && e.Position == 0);
        Assert.Contains(lane.Entries, e => e.Text == "二番目" && e.Position == 1);
        Assert.Contains(lane.Entries, e => e.Text == "三番目" && e.Position == 0);
        Assert.All(lane.Entries, e => Assert.Null(e.FadeFrame));
    }

    [Fact]
    public void Import_PackedMultiEntryLine_CommentGroupStopsRestOfLine()
    {
        // 本家準拠: Position="-"のグループが現れた時点で、その行の残りの処理を打ち切る。
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var text = difData +
            "\n|word_data=\n0,0,前半,30,-,コメント,60,0,無視される\n|\n" +
            "|de_schemaVersion=1|\n|de_startNumber=0|\n|de_bpm=0,120|\n";

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        var lane = Assert.Single(back.Project.Tabs[0].WordLanes);
        Assert.Equal(2, lane.Entries.Count);
        Assert.Contains(lane.Entries, e => e.Kind == WordEntryKind.Lyrics && e.Text == "前半");
        Assert.Contains(lane.Entries, e => e.Kind == WordEntryKind.Comment && e.Text == "コメント");
        Assert.DoesNotContain(lane.Entries, e => e.Text == "無視される");
    }

    [Fact]
    public void Import_NoWordDataParam_LeavesWordLanesEmpty()
    {
        var repo = TestFixtures.Repository();
        var difData = "|difData=5,Normal,3.5|";
        var back = new DosImporter(repo.Get).Import(difData, new DosImportOptions());

        Assert.Empty(back.Project.Tabs[0].WordLanes);
    }

    [Fact]
    public void RoundTrip_SecondTab_UsesWordSuffix()
    {
        var project = NewProject();
        project.Tabs.Add(DifficultyTab.CreateFor(TestFixtures.Repository().Get("5"), "Hard"));
        var lane = new WordLane { Name = "歌詞" };
        lane.Entries.Add(new WordEntry(0, 0, WordEntryKind.Lyrics, "2譜面目の歌詞"));
        project.Tabs[1].WordLanes.Add(lane);

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);

        Assert.Contains("|word2_data=", text);
        Assert.DoesNotContain("|word_data=", text); // タブ1側にはレーンが無い

        var back = new DosImporter(repo.Get).Import(text, new DosImportOptions());
        Assert.Empty(back.Project.Tabs[0].WordLanes);
        Assert.Single(back.Project.Tabs[1].WordLanes);
    }
}
