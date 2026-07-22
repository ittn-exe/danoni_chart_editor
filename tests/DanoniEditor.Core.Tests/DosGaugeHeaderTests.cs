using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>customGauge/gaugeXXX(仕様dos-h0053/dos-h0022)出力のテスト(2026-08-01、GaugeEditorWindow)。</summary>
public class DosGaugeHeaderTests
{
    private static ChartProject NewProject(int tabCount = 2)
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "title",
            BpmEvents = [new BpmEvent(0, 120)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        for (int i = 0; i < tabCount; i++)
            p.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), $"Diff{i}"));
        return p;
    }

    [Fact]
    public void InheritKeyword_WritesCustomGaugeWithSuffixPerTab()
    {
        var project = NewProject();
        project.Tabs[0].Gauge = new GaugeConfig { InheritKeyword = "survival" };
        project.Tabs[1].Gauge = new GaugeConfig { InheritKeyword = "border" };

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|customGauge=survival|", text);
        Assert.Contains("|customGauge2=border|", text);
    }

    [Fact]
    public void ExplicitEntries_WritesNameVarFlagAndOptionalDisplayName()
    {
        var project = NewProject(tabCount: 1);
        project.Tabs[0].Gauge = new GaugeConfig
        {
            Entries =
            [
                new GaugeListEntry("_Original", false, "Original"),
                new GaugeListEntry("Escape", true),
            ],
        };

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|customGauge=_Original::F::Original,Escape::V|", text);
    }

    [Fact]
    public void TabWithNullGauge_WritesNoCustomGaugeHeaderForThatTab()
    {
        var project = NewProject();
        project.Tabs[0].Gauge = new GaugeConfig { InheritKeyword = "survival" };
        // project.Tabs[1].Gauge は未設定(null)のまま

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|customGauge=survival|", text);
        Assert.DoesNotContain("customGauge2", text);
    }

    [Fact]
    public void GaugeParams_JoinsPerTabCsvWithDollarSign_EmptySegmentsAllowedForFallback()
    {
        var project = NewProject(tabCount: 3);
        project.GaugeParams["Heavy"] = new GaugeParamSet { PerTabCsv = ["2,50,50,100", "", "1,40,40,90"] };

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|gaugeHeavy=2,50,50,100$$1,40,40,90|", text);
    }

    [Fact]
    public void GaugeParams_AllEmptyForAName_WritesNoHeaderAtAll()
    {
        var project = NewProject(tabCount: 2);
        project.GaugeParams["Unused"] = new GaugeParamSet { PerTabCsv = ["", ""] };

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.DoesNotContain("gaugeUnused", text);
    }

    [Fact]
    public void RawOverrideText_TakesPriorityOverStructuredGaugeConfig()
    {
        var project = NewProject(tabCount: 1);
        project.Tabs[0].Gauge = new GaugeConfig { InheritKeyword = "survival" };
        project.GaugeParams["Heavy"] = new GaugeParamSet { PerTabCsv = ["2,50,50,100"] };
        project.GaugeRawOverrideText = "|customGauge=_Original::F::Original,Escape::V|\n|gaugeEscape=x,0,50,25|";

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        // 直接入力の内容がそのまま出力される
        Assert.Contains("|customGauge=_Original::F::Original,Escape::V|", text);
        Assert.Contains("|gaugeEscape=x,0,50,25|", text);
        // UI設定(継承キーワード/GaugeParams)側の内容は出力されない
        Assert.DoesNotContain("customGauge=survival", text);
        Assert.DoesNotContain("gaugeHeavy", text);
    }

    [Fact]
    public void RawOverrideText_WhitespaceOnly_IsTreatedAsUnset()
    {
        var project = NewProject(tabCount: 1);
        project.Tabs[0].Gauge = new GaugeConfig { InheritKeyword = "survival" };
        project.GaugeRawOverrideText = "   \n  ";

        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|customGauge=survival|", text);
    }
}
