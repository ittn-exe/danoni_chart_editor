using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>2026-07-24: 「難易度タブを閉じるとクラッシュする」不具合の回帰テスト。
/// 原因はEditorDocument.CurrentTabIndexのsetterが「値そのものが変わらなければ何もしない」
/// ガードを持っており、末尾以外のタブを閉じた際は数値上インデックスが変わらないケースがあるため、
/// レイアウトキャッシュ(_layoutCache)が閉じたタブのテンプレートを指したまま残ってしまう点にあった。
/// NotifyTabsChangedはこのガードを経由せず無条件でキャッシュを破棄することを確認する。</summary>
public class EditorDocumentTabCloseTests
{
    private static ChartProject ThreeTabProject(DanoniEditor.Core.Models.TemplateRepository repo)
    {
        var project = new ChartProject
        {
            ProjectName = "test",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));   // index0: 5鍵
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));  // index1: 5鍵(これを閉じる)
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("23"), "C")); // index2: 23鍵(閉じた後に繰り上がる)
        return project;
    }

    [Fact]
    public void ClosingNonLastTab_InvalidatesLayoutCache_EvenWhenIndexNumberIsUnchanged()
    {
        var repo = TestFixtures.Repository();
        var project = ThreeTabProject(repo);
        var doc = new EditorDocument(project, repo);

        // タブ1(5鍵)をカレントにしてレイアウトを一度アクセスし、5鍵のレイアウトをキャッシュさせる
        doc.CurrentTabIndex = 1;
        Assert.Equal(5, doc.CurrentLayout.Template.KeyCount);

        // タブ1を閉じる(MainWindow.CloseCurrentTab_Clickと同じ手順)
        int idx = doc.CurrentTabIndex; // = 1
        ProjectOperations.RemoveTab(doc.Project, idx);
        // 削除後: [A(5鍵), C(23鍵)] の2件。新しいインデックスはMath.Min(1, 2-1) = 1 で、
        // 「数値としては閉じる前と同じ1」になる(これが不具合の引き金だった)。
        doc.NotifyTabsChanged(Math.Min(idx, doc.Project.Tabs.Count - 1));

        Assert.Equal("C", doc.CurrentTab.DifficultyName);
        Assert.Equal("23", doc.CurrentTab.KeyTypeId);
        // レイアウトキャッシュが新しいカレントタブ(23鍵)に合わせて作り直されていること。
        // 修正前はここが古いキャッシュ(5鍵)のまま返ってきて、描画側でtab.Lanes[i]の添字が
        // layout.Template.Lanes[i]の範囲を超えIndexOutOfRangeExceptionでクラッシュしていた。
        Assert.Equal(23, doc.CurrentLayout.Template.KeyCount);
        Assert.Equal(23, doc.CurrentTab.Lanes.Count);
    }

    [Fact]
    public void ClosingLastTab_StillWorks_AndLeavesValidIndex()
    {
        var repo = TestFixtures.Repository();
        var project = ThreeTabProject(repo);
        var doc = new EditorDocument(project, repo);

        doc.CurrentTabIndex = 2; // 末尾(23鍵)をカレントに
        Assert.Equal(23, doc.CurrentLayout.Template.KeyCount);

        int idx = doc.CurrentTabIndex; // = 2
        ProjectOperations.RemoveTab(doc.Project, idx);
        doc.NotifyTabsChanged(Math.Min(idx, doc.Project.Tabs.Count - 1));

        Assert.Equal("B", doc.CurrentTab.DifficultyName);
        Assert.Equal(5, doc.CurrentLayout.Template.KeyCount);
    }
}
