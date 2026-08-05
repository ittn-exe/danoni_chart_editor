using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>2026-08-05不具合修正の回帰テスト: defaultFrzColorUse(dos-h0063)がtrueの間でも、
/// frzColorのHit(判定中、[2]/[3])は本体側で引き続き有効なため、frzColor自体の出力を
/// 抑止してはいけない(以前はdefaultFrzColorUse=trueの間frzColor{,N}を丸ごと出力抑止していた)。</summary>
public class DosFrzColorDefaultUseTests
{
    private static ChartProject NewProject(bool defaultFrzColorUse, List<string> frzColorOverride)
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject { ProjectName = "t", MusicTitle = "title" };
        if (defaultFrzColorUse) project.ExtraHeaders["defaultFrzColorUse"] = "true";
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        tab.FrzColorOverride = frzColorOverride;
        project.Tabs.Add(tab);
        return project;
    }

    [Fact]
    public void Export_DefaultFrzColorUseTrue_StillOutputsFrzColor_WithHitSlotsIntact()
    {
        var repo = TestFixtures.Repository();
        // [0]/[1](通常)は空欄、[2]/[3](判定中/Hit)のみ値ありのケース(ユーザー確定仕様上の想定パターン)
        var project = NewProject(defaultFrzColorUse: true, frzColorOverride: ["", "", "#333333", "#444444"]);

        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|frzColor=,,#333333,#444444|", text);
    }

    [Fact]
    public void Export_DefaultFrzColorUseFalse_OutputsFrzColorAsUsual()
    {
        var repo = TestFixtures.Repository();
        var project = NewProject(defaultFrzColorUse: false, frzColorOverride: ["#111111", "#222222", "#333333", "#444444"]);

        var text = new DosExporter(repo.Get).Export(project);

        Assert.Contains("|frzColor=#111111,#222222,#333333,#444444|", text);
    }
}
