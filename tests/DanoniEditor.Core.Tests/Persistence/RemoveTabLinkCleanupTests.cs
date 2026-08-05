using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>2026-08-06の回帰テスト: ProjectOperations.RemoveTabがタブリンク(LinkedTabId、2026-07-26)の
/// 解除まで行うこと。従来は呼び出し側(MainWindow.CloseTabAt)が事前に相手タブのLinkedTabIdをnullに
/// する作法に依存しており、他経路から削除すると削除済みタブのTabIdを指したままのリンクが残る
/// (エラーは出ないがリンク表示が無言で機能しなくなる)構造だった。</summary>
public class RemoveTabLinkCleanupTests
{
    private static ChartProject ThreeTabProject()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject { ProjectName = "t" };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "C"));
        return project;
    }

    [Fact]
    public void RemoveTab_ClearsPartnersLinkedTabId()
    {
        var project = ThreeTabProject();
        // タブBとタブCを相互リンク
        project.Tabs[1].LinkedTabId = project.Tabs[2].TabId;
        project.Tabs[2].LinkedTabId = project.Tabs[1].TabId;

        ProjectOperations.RemoveTab(project, 2); // タブCを削除

        Assert.Null(project.Tabs[1].LinkedTabId); // 相方の参照が宙に浮かない
    }

    [Fact]
    public void RemoveTab_LeavesUnrelatedLinksIntact()
    {
        var project = ThreeTabProject();
        project.Tabs[1].LinkedTabId = project.Tabs[2].TabId;
        project.Tabs[2].LinkedTabId = project.Tabs[1].TabId;
        var bId = project.Tabs[1].TabId;
        var cId = project.Tabs[2].TabId;

        ProjectOperations.RemoveTab(project, 0); // 無関係なタブAを削除

        // B↔Cのリンクはそのまま維持される
        Assert.Equal(cId, project.Tabs.First(t => t.TabId == bId).LinkedTabId);
        Assert.Equal(bId, project.Tabs.First(t => t.TabId == cId).LinkedTabId);
    }
}
