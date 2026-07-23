using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>
/// タブ単体エクスポート(ProjectSerializer.SerializeTabExport/DeserializeTabExport、
/// ProjectOperations.ApplyImport(TabExportResult)、2026-07-23、TBD 5)のテスト。
/// 合作用途で現在の難易度タブ1つだけをITTNエディタ形式で書き出し、
/// 合作相手が「開く」/D&Dでタブとして追加インポートできることを確認する。
/// </summary>
public class ProjectSerializerTabExportTests
{
    private static ChartProject NewProject()
    {
        var project = new ChartProject
        {
            ProjectName = "song",
            MusicTitle = "曲名",
            ArtistName = "アーティスト",
            BpmEvents = [new BpmEvent(0, 150)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 96,
            StartFrame = 96,
            BlankFrame = 0,
        };
        var tab = new DifficultyTab { DifficultyName = "Hard", KeyTypeId = "7" };
        tab.Lanes.Add(new LaneNotes());
        tab.Lanes[0].Notes.Add(1680);
        project.Tabs.Add(tab);
        return project;
    }

    [Fact]
    public void SerializeTabExport_RoundTrips_TabContentAndTiming()
    {
        var project = NewProject();
        var json = ProjectSerializer.SerializeTabExport(project, project.Tabs[0]);
        var result = ProjectSerializer.DeserializeTabExport(json);

        Assert.Equal("Hard", result.Tab.DifficultyName);
        Assert.Equal("7", result.Tab.KeyTypeId);
        Assert.Single(result.Tab.Lanes[0].Notes);
        Assert.Equal(1680, result.Tab.Lanes[0].Notes[0]);
        Assert.Equal(150, result.Source.BpmEvents[0].Bpm);
        Assert.Equal(96, result.Source.StartNumber);
    }

    [Fact]
    public void ApplyImport_IntoEmptyProject_AdoptsTiming()
    {
        var source = NewProject();
        var json = ProjectSerializer.SerializeTabExport(source, source.Tabs[0]);
        var result = ProjectSerializer.DeserializeTabExport(json);

        var target = new ChartProject(); // 空プロジェクト(既定BPM120)
        var warnings = ProjectOperations.ApplyImport(target, result);

        Assert.Empty(warnings);
        Assert.Single(target.Tabs);
        Assert.Equal("Hard", target.Tabs[0].DifficultyName);
        Assert.Equal(150, target.BpmEvents[0].Bpm); // タイミングを引き継ぐ
        Assert.Equal(96, target.StartNumber);
    }

    [Fact]
    public void ApplyImport_IntoExistingProject_KeepsOwnTiming_AppendsTabOnly()
    {
        var source = NewProject();
        var json = ProjectSerializer.SerializeTabExport(source, source.Tabs[0]);
        var result = ProjectSerializer.DeserializeTabExport(json);

        var target = new ChartProject { BpmEvents = [new BpmEvent(0, 120)] };
        target.Tabs.Add(new DifficultyTab { DifficultyName = "Normal" }); // 既存タブ1件

        var warnings = ProjectOperations.ApplyImport(target, result);

        Assert.NotEmpty(warnings); // BPM(120 vs 150)が異なるため警告あり
        Assert.Equal(2, target.Tabs.Count);
        Assert.Equal("Hard", target.Tabs[1].DifficultyName);
        Assert.Equal(120, target.BpmEvents[0].Bpm); // 既存プロジェクト側のタイミングを維持
    }

    [Fact]
    public void DeserializeTabExport_RejectsMultiTabPayload()
    {
        // SerializeTabExportは常にTabs=[tab]の1件しか作らないため、不正データは
        // 通常のプロジェクト全体保存(Serialize、2タブ)を手動でtabExport形式に改変して再現する。
        var project = NewProject();
        project.Tabs.Add(new DifficultyTab { DifficultyName = "Extra" });
        var normalJson = ProjectSerializer.Serialize(project); // タブ2件のプロジェクト全体
        var tampered = normalJson.Replace("\"project\":", "\"tabExport\":true,\"project\":");

        Assert.Throws<InvalidDataException>(() => ProjectSerializer.DeserializeTabExport(tampered));
    }
}
