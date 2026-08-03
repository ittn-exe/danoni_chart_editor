using DanoniEditor.Core.Export;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>「dosロック」(DifficultyTab.ExcludeFromDosExport、2026-08-02要望対応)のテスト。
/// dosロック中のタブはdifData一覧・データブロックのどちらからも除外され、残りのタブの
/// サフィックス採番は詰め直される(除外後の並びに対して連番を振り直す)ことを検証する。</summary>
public class DosExcludeFromExportTests
{
    private static ChartProject NewProject()
    {
        var repo = TestFixtures.Repository();
        var p = new ChartProject { ProjectName = "t", MusicTitle = "title" };

        var normal = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        normal.Lanes[0].Notes.Add(0);

        var hard = DifficultyTab.CreateFor(repo.Get("5"), "Hard");
        hard.ExcludeFromDosExport = true;
        hard.Lanes[0].Notes.Add(0);

        var another = DifficultyTab.CreateFor(repo.Get("5"), "Another");
        another.Lanes[0].Notes.Add(0);

        p.Tabs.AddRange([normal, hard, another]);
        return p;
    }

    [Fact]
    public void Export_ExcludedTab_IsOmittedFromDifData()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProject());
        // difDataは"キー種,難易度名,速度"の$区切り。ロック中のHardは含まれず、
        // Normal/Anotherの2件だけが残る。
        Assert.Contains("|difData=5,Normal,3.5$5,Another,3.5|", text);
        Assert.DoesNotContain("Hard", text);
    }

    [Fact]
    public void Export_ExcludedTab_RenumbersRemainingTabSuffixes()
    {
        var repo = TestFixtures.Repository();
        var text = new DosExporter(repo.Get).Export(NewProject());
        // 除外前の並びはNormal(無サフィックス)/Hard(2)/Another(3)だが、
        // Hardが除外されるとAnotherは詰まって2番目=無サフィックスの次(2)になる。
        Assert.Contains("left_data=", text);   // Normal(先頭、無サフィックス)
        Assert.Contains("left2_data=", text);  // Another(詰め後、2番目)
        Assert.DoesNotContain("left3_data=", text); // 3番目は存在しない(タブは2件だけ残る)
    }

    [Fact]
    public void Export_AllTabsExcluded_Throws()
    {
        var repo = TestFixtures.Repository();
        var p = NewProject();
        foreach (var t in p.Tabs) t.ExcludeFromDosExport = true;
        Assert.Throws<InvalidOperationException>(() => new DosExporter(repo.Get).Export(p));
    }

    [Fact]
    public void Export_ExcludedFirstTab_DoesNotLeakSetColorOverrideIntoHeader()
    {
        // 2026-07-24仕様の「1タブ目=setColor共通値の実体」は、dosロック除外後の並びで
        // 判定されるべき(除外された実タブ1件目のSetColorOverrideがヘッダーに漏れないこと)。
        var repo = TestFixtures.Repository();
        var p = new ChartProject { ProjectName = "t", MusicTitle = "title" };

        var lockedFirst = DifficultyTab.CreateFor(repo.Get("5"), "WIP");
        lockedFirst.ExcludeFromDosExport = true;
        lockedFirst.SetColorOverride = ["#111111", "#111111", "#111111", "#111111", "#111111", "#111111"];

        var included = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        included.Lanes[0].Notes.Add(0);

        p.Tabs.AddRange([lockedFirst, included]);

        var text = new DosExporter(repo.Get).Export(p);
        // includedタブ(除外後の実質1タブ目)はSetColorOverride未設定のため、setColorヘッダー自体が
        // 出力されないのが正しい(ロック中タブの#111111が漏れて出力されてしまうのが不具合)。
        Assert.DoesNotContain("setColor", text);
    }

    [Fact]
    public void Export_ExcludedFirstTab_DoesNotLeakSetColorOverrideIntoNColorDefaultResolution()
    {
        // ColorDefaults.ResolveSetColorHexの「1タブ目」フォールバックが、dosロック除外後の
        // 並び(tabs[0]=included)ではなくproject.Tabs[0](=lockedFirst)を見てしまうと、
        // ncolor_dataの既定色判定がズレて本来出力されるべき差分行が消えてしまう不具合になる。
        var repo = TestFixtures.Repository();
        var p = new ChartProject { ProjectName = "t", MusicTitle = "title" };

        var lockedFirst = DifficultyTab.CreateFor(repo.Get("5"), "WIP");
        lockedFirst.ExcludeFromDosExport = true;
        lockedFirst.SetColorOverride = ["#111111", "#111111", "#111111", "#111111", "#111111", "#111111"];

        var included = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        included.Lanes[0].Notes.Add(0);
        // colorGroup0(既定色は本来"#99ffff")のノートへ、lockedFirstの上書き値と同じ"#111111"を指定。
        // もしlockedFirstが誤って既定色の基準に使われていると「既定色と一致」判定されてしまい、
        // ncolor_dataの差分行が出力されなくなる。
        included.Lanes[0].ColorOverrides.Add(new NColorEntry(0, "#111111", null));

        p.Tabs.AddRange([lockedFirst, included]);

        var text = new DosExporter(repo.Get).Export(p);
        Assert.Contains("ncolor_data=", text);
        Assert.Contains("#111111", text);
    }
}
