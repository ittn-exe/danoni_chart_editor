using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>
/// ProjectOperations.ApplyImport(DosImportResult)(2026-07-25、FUJI/SKB/タブファイルインポートと
/// インポート先の選択フローを統一)のテスト。dos.txtは1ファイルに複数難易度タブを含み得るため、
/// FUJI/SKB(常に1タブ)と異なりResult.Project.Tabsの「全件」が追加されることを確認する。
/// </summary>
public class ProjectOperationsDosImportTests
{
    /// <summary>BPM150・StartNumber100・難易度タブ2件(dos.txtのdifData 2行分)を持つソースプロジェクトから
    /// dos.txtテキストを組み立てる(DosExporterでラウンドトリップし、DosImporterの実際の出力形式に合わせる)。</summary>
    private static string BuildTwoTabDosText()
    {
        var repo = TestFixtures.Repository();
        var template = repo.Get("5");
        var project = new ChartProject
        {
            ProjectName = "song",
            MusicTitle = "曲名",
            BpmEvents = [new BpmEvent(0, 150)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 100,
            BlankFrame = 0,
        };
        var tab1 = DifficultyTab.CreateFor(template, "Normal");
        tab1.Lanes[0].Notes.Add(TimingEngine.TicksPerBeat); // 1拍目
        var tab2 = DifficultyTab.CreateFor(template, "Hard");
        tab2.Lanes[0].Notes.Add(TimingEngine.TicksPerBeat * 2);
        project.Tabs.Add(tab1);
        project.Tabs.Add(tab2);

        return new DosExporter(repo.Get).Export(project, includeEditorMetadata: true);
    }

    private static DosImportResult ImportTwoTabs()
    {
        var repo = TestFixtures.Repository();
        var text = BuildTwoTabDosText();
        return new DosImporter(repo.Get).Import(text, new DosImportOptions());
    }

    [Fact]
    public void ApplyImport_IntoEmptyProject_AdoptsTiming_AppendsAllTabs()
    {
        var result = ImportTwoTabs();
        Assert.Equal(2, result.Project.Tabs.Count); // 前提: dos.txt側は2タブ

        var target = new ChartProject(); // 空プロジェクト(既定BPM120)
        var warnings = ProjectOperations.ApplyImport(target, result);

        Assert.Empty(warnings.Where(w => w.Contains("プロジェクト側の設定を維持"))); // タイミング不一致警告は無い
        Assert.Equal(2, target.Tabs.Count);
        Assert.Equal("Normal", target.Tabs[0].DifficultyName);
        Assert.Equal("Hard", target.Tabs[1].DifficultyName);
        Assert.Equal(150, target.BpmEvents[0].Bpm); // タイミングを引き継ぐ
        Assert.Equal(100, target.StartNumber, 3);
    }

    [Fact]
    public void ApplyImport_IntoExistingProject_KeepsOwnTiming_AppendsAllTabsOnly()
    {
        var result = ImportTwoTabs();

        var target = new ChartProject { BpmEvents = [new BpmEvent(0, 120)] };
        target.Tabs.Add(new DifficultyTab { DifficultyName = "Existing" }); // 既存タブ1件

        var warnings = ProjectOperations.ApplyImport(target, result);

        Assert.Contains(warnings, w => w.Contains("プロジェクト側の設定を維持")); // BPM(120 vs 150)が異なるため警告あり
        Assert.Equal(3, target.Tabs.Count);
        Assert.Equal("Existing", target.Tabs[0].DifficultyName);
        Assert.Equal("Normal", target.Tabs[1].DifficultyName);
        Assert.Equal("Hard", target.Tabs[2].DifficultyName);
        Assert.Equal(120, target.BpmEvents[0].Bpm); // 既存プロジェクト側のタイミングを維持
    }
}
