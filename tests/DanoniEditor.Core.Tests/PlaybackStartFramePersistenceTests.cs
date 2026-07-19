using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;

namespace DanoniEditor.Core.Tests;

/// <summary>再生開始フレーム(2026-07-17f)のプロジェクトファイル永続化テスト</summary>
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
}
