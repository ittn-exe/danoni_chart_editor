using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>2026-08-09不具合修正の回帰テスト: GaugeNameDefConverter(GaugeNames、gaugeXXX宣言の
/// シリアライズ/デシリアライズ)が、型自体に付与された[JsonConverter(typeof(GaugeNameDefConverter))]
/// 属性のせいで自分自身を無限に呼び出し、スタックオーバーフロー(try/catchで捕捉不可能な即死クラッシュ)
/// を起こしていた不具合。GaugeNamesを1件以上含むプロジェクトを保存しようとすると必ず発生し、
/// 曲名の言語や内容とは無関係だった(第三者報告は「曲名を日本語で入力後の上書き保存でクラッシュ」
/// だったが、実際にはGaugeNamesを含む任意のプロジェクトの保存で再現する不具合だった)。</summary>
public class GaugeNameDefSerializationTests
{
    private static ChartProject NewProjectWithGaugeNames(params GaugeNameDef[] gaugeNames)
    {
        var project = new ChartProject
        {
            ProjectName = "song",
            MusicTitle = "曲名",
            BpmEvents = [new BpmEvent(0, 150)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            StartNumber = 0,
            BlankFrame = 0,
            GaugeNames = [.. gaugeNames],
        };
        var tab = new DifficultyTab { DifficultyName = "Hard", KeyTypeId = "7" };
        tab.Lanes.Add(new LaneNotes());
        project.Tabs.Add(tab);
        return project;
    }

    [Fact]
    public void Serialize_ProjectWithGaugeNames_DoesNotStackOverflow()
    {
        // 修正前はここでStackOverflowException(捕捉不可能、プロセス強制終了)が発生していた。
        var project = NewProjectWithGaugeNames(new GaugeNameDef("gaugeORIGINAL"), new GaugeNameDef("gaugeHARD"));
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("gaugeORIGINAL", json);
        Assert.Contains("gaugeHARD", json);
    }

    [Fact]
    public void Serialize_GaugeNameWithDisplayName_WritesBothFields()
    {
        var project = NewProjectWithGaugeNames(new GaugeNameDef("gaugeCUSTOM", "カスタム表示名"));
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"name\": \"gaugeCUSTOM\"", json);
        Assert.Contains("\"displayName\": \"カスタム表示名\"", json);
    }

    [Fact]
    public void Serialize_GaugeNameWithoutDisplayName_OmitsDisplayNameField()
    {
        var project = NewProjectWithGaugeNames(new GaugeNameDef("gaugeORIGINAL"));
        var json = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("displayName", json);
    }

    [Fact]
    public void RoundTrip_ObjectFormGaugeNames_PreservesNameAndDisplayName()
    {
        var project = NewProjectWithGaugeNames(
            new GaugeNameDef("gaugeORIGINAL"),
            new GaugeNameDef("gaugeCUSTOM", "カスタム表示名"));
        var json = ProjectSerializer.Serialize(project);

        var reloaded = ProjectSerializer.Deserialize(json);

        Assert.Equal(2, reloaded.GaugeNames.Count);
        Assert.Equal("gaugeORIGINAL", reloaded.GaugeNames[0].Name);
        Assert.Null(reloaded.GaugeNames[0].DisplayName);
        Assert.Equal("gaugeCUSTOM", reloaded.GaugeNames[1].Name);
        Assert.Equal("カスタム表示名", reloaded.GaugeNames[1].DisplayName);
    }

    [Fact]
    public void RoundTrip_ThenReserialize_IsStableAndDoesNotStackOverflow()
    {
        // 保存→読込→再保存を2周させ、オブジェクト形式で書き出された後の再読込・再シリアライズでも
        // 無限再帰が起きないことを確認する(旧実装はWriteが常にオブジェクト形式を書くため、
        // 一度でも保存されたファイルを再度開いて保存し直した時点でRead側の無限再帰も誘発し得た)。
        var project = NewProjectWithGaugeNames(new GaugeNameDef("gaugeORIGINAL", "オリジナル"));
        var json1 = ProjectSerializer.Serialize(project);
        var reloaded1 = ProjectSerializer.Deserialize(json1);
        var json2 = ProjectSerializer.Serialize(reloaded1);
        var reloaded2 = ProjectSerializer.Deserialize(json2);

        Assert.Equal(json1, json2);
        Assert.Equal("gaugeORIGINAL", reloaded2.GaugeNames[0].Name);
        Assert.Equal("オリジナル", reloaded2.GaugeNames[0].DisplayName);
    }

    [Fact]
    public void Deserialize_LegacyStringFormGaugeNames_StillSupported()
    {
        // 旧形式(schemaVersion=3のままGaugeNamesが文字列配列だった時期)との後方互換確認。
        const string json = """
        {
          "schemaVersion": 3,
          "project": {
            "projectName": "song",
            "musicTitle": "曲名",
            "bpmEvents": [{ "tick": 0, "bpm": 150 }],
            "timeSignatures": [{ "measureIndex": 0, "numerator": 4, "denominator": 4 }],
            "gaugeNames": ["gaugeORIGINAL", "gaugeHARD"],
            "tabs": [{ "difficultyName": "Hard", "keyTypeId": "7", "lanes": [{}] }]
          }
        }
        """;
        var project = ProjectSerializer.Deserialize(json);
        Assert.Equal(["gaugeORIGINAL", "gaugeHARD"], project.GaugeNames.Select(g => g.Name));
    }
}
