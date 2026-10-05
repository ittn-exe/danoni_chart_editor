using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Tests.Editing;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Tests;

/// <summary>難易度別音源(ChartProject.AdditionalSongs/DifficultyTab.SongIndex、2026-09-29要望対応・
/// 簡素化版)のテスト。danoniplus本体のmusicTitle/musicUrl("$"区切りで複数曲)+musicNo(タブ順・"$"区切り
/// で曲番号指定、dos-h0011/h0012)への対応を検証する。</summary>
public class DosMusicPerTabTests
{
    private static ChartProject NewTwoTabProject()
    {
        var repo = TestFixtures.Repository();
        var template = repo.Get("5");
        var p = new ChartProject
        {
            ProjectName = "t",
            MusicTitle = "曲A",
            ArtistName = "アーティストA",
            ArtistUrl = "http://a.example/",
            MusicUrl = "a.mp3",
        };
        var normal = DifficultyTab.CreateFor(template, "Normal");
        normal.Lanes[0].Notes.Add(0);
        var hard = DifficultyTab.CreateFor(template, "Hard");
        hard.Lanes[0].Notes.Add(TimingEngine.TicksPerBeat);
        p.Tabs.AddRange([normal, hard]);
        return p;
    }

    [Fact]
    public void Export_NoAdditionalSongs_EmitsSingleLineMusicHeaders_NoMusicNo()
    {
        // 後方互換: AdditionalSongsが空のプロジェクトは、従来通り単一行のmusicTitle/musicUrlのみ
        // (musicNoは出力しない)。新規タブのSongIndex既定値0(=1曲目)により、通常は誰も触らなくても
        // この状態のまま。
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();

        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: false);

        Assert.Contains("|musicTitle=曲A,アーティストA,http://a.example/|", text);
        Assert.Contains("|musicUrl=a.mp3|", text);
        Assert.DoesNotContain("musicNo", text);
    }

    [Fact]
    public void Export_OneAdditionalSong_TabUsesIt_EmitsMultiSongMusicHeaders_AndMusicNo()
    {
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();
        p.AdditionalSongs.Add(new SongInfo
        {
            MusicTitle = "曲B",
            ArtistName = "アーティストB",
            ArtistUrl = "http://b.example/",
            MusicUrl = "b.mp3",
        });
        p.Tabs[1].SongIndex = 1;

        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: false);

        Assert.Contains("|musicTitle=曲A,アーティストA,http://a.example/$曲B,アーティストB,http://b.example/|", text);
        Assert.Contains("|musicUrl=a.mp3$b.mp3|", text);
        Assert.Contains("|musicNo=0$1|", text);
    }

    [Fact]
    public void Export_MultipleTabsUseSameAdditionalSong_MusicNoRepeatsSameIndex()
    {
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();
        var extra = DifficultyTab.CreateFor(repo.Get("5"), "Another");
        extra.Lanes[0].Notes.Add(TimingEngine.TicksPerBeat * 2);
        p.Tabs.Add(extra);

        p.AdditionalSongs.Add(new SongInfo { MusicTitle = "曲B", MusicUrl = "b.mp3" });
        p.Tabs[1].SongIndex = 1;
        p.Tabs[2].SongIndex = 1;

        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: false);

        Assert.Contains("|musicUrl=a.mp3$b.mp3|", text);
        Assert.Contains("|musicNo=0$1$1|", text);
    }

    [Fact]
    public void Export_SongIndexOutOfRange_ClampsToValidRange()
    {
        // 手動編集等でAdditionalSongsの範囲外を指すSongIndexになっていた場合の安全策。
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();
        p.AdditionalSongs.Add(new SongInfo { MusicTitle = "曲B", MusicUrl = "b.mp3" });
        p.Tabs[1].SongIndex = 99; // 範囲外

        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: false);

        Assert.Contains("|musicNo=0$1|", text); // 99は最大有効値(1)へクランプされる
    }

    [Fact]
    public void RoundTrip_ImportRestoresAdditionalSongsAndSongIndex()
    {
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();
        p.AdditionalSongs.Add(new SongInfo
        {
            MusicTitle = "曲B",
            ArtistName = "アーティストB",
            ArtistUrl = "http://b.example/",
            MusicUrl = "b.mp3",
        });
        p.Tabs[1].SongIndex = 1;
        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: true);

        var result = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        Assert.Equal("曲A", result.Project.MusicTitle);
        Assert.Equal("a.mp3", result.Project.MusicUrl);
        Assert.Equal(0, result.Project.Tabs[0].SongIndex);

        Assert.Single(result.Project.AdditionalSongs);
        var song = result.Project.AdditionalSongs[0];
        Assert.Equal("曲B", song.MusicTitle);
        Assert.Equal("アーティストB", song.ArtistName);
        Assert.Equal("http://b.example/", song.ArtistUrl);
        Assert.Equal("b.mp3", song.MusicUrl);
        Assert.Equal(1, result.Project.Tabs[1].SongIndex);
    }

    [Fact]
    public void RoundTrip_NoAdditionalSongsUsed_ImportLeavesSongIndexAtZero()
    {
        var repo = TestFixtures.Repository();
        var p = NewTwoTabProject();
        var text = new DosExporter(repo.Get).Export(p, includeEditorMetadata: true);

        var result = new DosImporter(repo.Get).Import(text, new DosImportOptions());

        Assert.Empty(result.Project.AdditionalSongs);
        Assert.All(result.Project.Tabs, t => Assert.Equal(0, t.SongIndex));
    }

    [Fact]
    public void Clone_CopiesSongIndex()
    {
        var repo = TestFixtures.Repository();
        var tab = DifficultyTab.CreateFor(repo.Get("5"), "Normal");
        tab.SongIndex = 2;

        var clone = tab.Clone();
        Assert.Equal(2, clone.SongIndex);
    }
}
