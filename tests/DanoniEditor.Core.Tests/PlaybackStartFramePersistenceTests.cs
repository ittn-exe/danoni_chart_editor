using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests;

/// <summary>再生開始フレーム(2026-07-17f)のプロジェクトファイル永続化テスト。
/// ChartProject.PlaybackStartFrameは2026-08-04不具合修正で「旧・互換用」フィールドとなったが、
/// 旧形式プロジェクトファイルの読み込みに使い続けるためシリアライズ形式自体は変更していない
/// (ロールトリップ自体は従来通り保証する)。新形式の本体であるDifficultyTab.PlaybackStartFrame
/// についても同様にラウンドトリップすることを確認する。</summary>
public class PlaybackStartFramePersistenceTests
{
    [Fact]
    public void PlaybackStartFrame_RoundTrips()
    {
        var project = new ChartProject { ProjectName = "t", PlaybackStartFrame = 123.5 };
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(123.5, back.PlaybackStartFrame);
    }

    [Fact]
    public void PlaybackStartFrame_Unset_RoundTripsAsNull()
    {
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new ChartProject()));
        Assert.Null(back.PlaybackStartFrame);
    }

    [Fact]
    public void DifficultyTabPlaybackStartFrame_RoundTrips()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject { ProjectName = "t" };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));
        project.Tabs[0].PlaybackStartFrame = 88.0;
        // タブ1は未設定のまま(タブごとに独立していることの確認)

        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.Equal(88.0, back.Tabs[0].PlaybackStartFrame);
        Assert.Null(back.Tabs[1].PlaybackStartFrame);
    }
}
