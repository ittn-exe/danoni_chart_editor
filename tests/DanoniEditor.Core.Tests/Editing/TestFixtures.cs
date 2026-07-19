using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// Editing層テスト共通フィクスチャ。実テンプレート(./template)には依存せず、
/// テスト専用の5key相当テンプレート(TestData/EditingTemplate/temp_5.json)を使う。
/// </summary>
internal static class TestFixtures
{
    private static string TemplateDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "TestData", "EditingTemplate")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("TestData/EditingTemplate が見つかりません");
        return Path.Combine(dir, "TestData", "EditingTemplate");
    }

    public static TemplateRepository Repository() => new(TemplateDir());

    /// <summary>BPM120・拍子4/4・5keyタブ1つを持つ最小プロジェクト</summary>
    public static ChartProject NewProject(double bpm = 120)
    {
        var repo = Repository();
        var template = repo.Get("5");
        var project = new ChartProject
        {
            ProjectName = "test",
            BpmEvents = [new BpmEvent(0, bpm)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
        };
        project.Tabs.Add(DifficultyTab.CreateFor(template, "Normal"));
        return project;
    }

    public static EditorDocument NewDocument(double bpm = 120) => new(NewProject(bpm), Repository());
}
