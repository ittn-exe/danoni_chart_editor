using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>2026-08-04不具合修正: 再生開始ライン(旧ChartProject.PlaybackStartFrame)を
/// タブごとに独立させた(DifficultyTab.PlaybackStartFrame)ことに伴う移行ロジックのテスト。
/// ユーザー確定仕様:
/// ・旧形式プロジェクト(ChartProject.PlaybackStartFrameのみが値を持つ)を開いた場合、
///   タブが表示されるたびにそのタブ自身の値として旧値が確定する。
/// ・プロジェクトを開いてから保存するまでの間に一度も表示されなかったタブは、保存時点
///   (EditorDocument.PrepareForSave)で「既に値が入っている既存タブの中で最もインデックスが
///   若いタブの値」を使う。</summary>
public class PlaybackStartFramePerTabMigrationTests
{
    private static ChartProject LegacyThreeTabProject(double legacyValue)
    {
        var project = new ChartProject
        {
            ProjectName = "test",
            BpmEvents = [new BpmEvent(0, 120)],
            PlaybackStartFrame = legacyValue, // 旧形式: プロジェクト直下に値がある
        };
        var repo = TestFixtures.Repository();
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "C"));
        return project;
    }

    [Fact]
    public void OpeningLegacyProject_ImmediatelyAssignsValueToInitiallyVisibleTab()
    {
        var project = LegacyThreeTabProject(123.5);
        var doc = new EditorDocument(project, TestFixtures.Repository());

        // 開いた直後に表示されているタブ(index0)は即座に旧値を引き継ぐ
        Assert.Equal(123.5, project.Tabs[0].PlaybackStartFrame);
        // まだ表示していない他タブは未設定のまま
        Assert.Null(project.Tabs[1].PlaybackStartFrame);
        Assert.Null(project.Tabs[2].PlaybackStartFrame);
    }

    [Fact]
    public void SwitchingToAnotherTab_AssignsLegacyValueOnFirstVisit_ButNotOnSubsequentVisits()
    {
        var project = LegacyThreeTabProject(200.0);
        var doc = new EditorDocument(project, TestFixtures.Repository());

        doc.CurrentTabIndex = 1;
        Assert.Equal(200.0, project.Tabs[1].PlaybackStartFrame);

        // タブ1で独自に値を変更(明示的な設定)
        var engine = project.CreateTimingEngine();
        project.Tabs[1].PlaybackStartFrame = engine.TickToFrame(999);

        // タブ0へ戻ってからもう一度タブ1へ戻っても、既に確定済みの独自の値は上書きされない
        doc.CurrentTabIndex = 0;
        doc.CurrentTabIndex = 1;
        Assert.Equal(engine.TickToFrame(999), project.Tabs[1].PlaybackStartFrame);
    }

    [Fact]
    public void ClearingPlaybackStartOnActivatedTab_DoesNotFallBackToLegacyValue()
    {
        // BackSpaceでのクリア操作を模したテスト: 一度表示されたタブは旧値が実体としてコピーされているため、
        // nullへクリアした後は(旧フォールバック方式と違い)legacy値が再度滲み出さないことを確認する。
        var project = LegacyThreeTabProject(300.0);
        var doc = new EditorDocument(project, TestFixtures.Repository());

        Assert.Equal(300.0, doc.CurrentTab.PlaybackStartFrame);
        doc.CurrentTab.PlaybackStartFrame = null; // BackSpaceクリア相当
        Assert.Null(doc.CurrentTab.PlaybackStartFrame);

        // タブを切り替えて戻ってきても復活しない(このタブは既にactivated済みのため再代入されない)
        doc.CurrentTabIndex = 1;
        doc.CurrentTabIndex = 0;
        Assert.Null(doc.CurrentTab.PlaybackStartFrame);
    }

    [Fact]
    public void PrepareForSave_NeverVisitedTabs_InheritLowestIndexedTabsValue()
    {
        var project = LegacyThreeTabProject(400.0);
        var doc = new EditorDocument(project, TestFixtures.Repository());

        // タブ0のみ表示(=旧値を引き継ぎ済み)。タブ1・2は一度も表示していない。
        Assert.Equal(400.0, project.Tabs[0].PlaybackStartFrame);
        Assert.Null(project.Tabs[1].PlaybackStartFrame);
        Assert.Null(project.Tabs[2].PlaybackStartFrame);

        doc.PrepareForSave();

        // 未表示タブは既存タブ(タブ0)の値を継承する
        Assert.Equal(400.0, project.Tabs[1].PlaybackStartFrame);
        Assert.Equal(400.0, project.Tabs[2].PlaybackStartFrame);
        // 旧フィールドはクリアされる
        Assert.Null(project.PlaybackStartFrame);
    }

    [Fact]
    public void PrepareForSave_IsIdempotent_AndNoOpForNewFormatProjects()
    {
        var project = LegacyThreeTabProject(500.0);
        var doc = new EditorDocument(project, TestFixtures.Repository());
        doc.PrepareForSave();
        doc.PrepareForSave(); // 2回目は何もしない(旧値は既にnull)

        Assert.Equal(500.0, project.Tabs[0].PlaybackStartFrame);
        Assert.Equal(500.0, project.Tabs[1].PlaybackStartFrame);
        Assert.Equal(500.0, project.Tabs[2].PlaybackStartFrame);

        // 新形式(旧フィールドが最初からnull)のプロジェクトではPrepareForSaveは何もしない
        var newProject = new ChartProject { ProjectName = "new", BpmEvents = [new BpmEvent(0, 120)] };
        var repo = TestFixtures.Repository();
        newProject.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        var newDoc = new EditorDocument(newProject, repo);
        newDoc.PrepareForSave();
        Assert.Null(newProject.Tabs[0].PlaybackStartFrame);
    }

    [Fact]
    public void Clone_DoesNotCarryOverPlaybackStartFrame_ByDefault()
    {
        var repo = TestFixtures.Repository();
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "A");
        tab.PlaybackStartFrame = 42.0;

        var clone = tab.Clone();

        Assert.Null(clone.PlaybackStartFrame);
        Assert.Equal(42.0, tab.PlaybackStartFrame); // 元タブは変化しない
    }

    [Fact]
    public void Clone_CarriesOverPlaybackStartFrame_WhenRequested()
    {
        var repo = TestFixtures.Repository();
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "A");
        tab.PlaybackStartFrame = 42.0;

        var clone = tab.Clone(carryOverPlaybackStart: true);

        Assert.Equal(42.0, clone.PlaybackStartFrame);
    }
}
