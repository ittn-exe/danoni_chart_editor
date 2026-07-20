using DanoniEditor.Core.Import;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// FujiImporter.ScanDifData(2026-07-20、D&D対応)のテスト。従来は「キー種を手入力→Import後に
/// 同キー種内の複数難易度だけ選択」だったが、difData行は自分のキー種を持つ自己完結データなので、
/// キー種を問わず全行を先読みできる。0/1/複数件それぞれの挙動と、$frame等が無くても
/// (Import()と違い)例外を投げないことを確認する。
/// </summary>
public class FujiImporterScanDifDataTests
{
    [Fact]
    public void NoDifData_ReturnsEmpty()
    {
        var text = """
        $frame=0/0/9600/1,10
        $score=
        $header=
        |musicUrl=nosound.mp3|
        """;
        Assert.Empty(FujiImporter.ScanDifData(text));
    }

    [Fact]
    public void SingleDifDataRow_ReturnsOneCandidate_WithKeyTypeAndName()
    {
        var text = """
        $frame=0/0/9600/1,10
        $score=
        $header=
        |difData=5,Normal,3.5|
        """;
        var result = FujiImporter.ScanDifData(text);
        var c = Assert.Single(result);
        Assert.Equal("5", c.KeyTypeId);
        Assert.Equal("Normal", c.DifficultyName);
        Assert.Equal(3.5, c.InitialSpeed);
    }

    [Fact]
    public void MultipleDifDataRows_MixedKeyTypes_ReturnsAllUnfiltered()
    {
        // difDataは$区切りで複数行(wiki準拠)。キー種混在でも絞り込まず全件返す(D&Dで
        // 「まず全キー種から選ばせる」フローの土台になるため)。
        var text = """
        $frame=0/0/9600/1,10
        $score=
        $header=
        |difData=5,Normal,3.5$7,Hard,4.0$11,Extreme,5.2|
        """;
        var result = FujiImporter.ScanDifData(text);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, c => c.KeyTypeId == "5" && c.DifficultyName == "Normal");
        Assert.Contains(result, c => c.KeyTypeId == "7" && c.DifficultyName == "Hard");
        Assert.Contains(result, c => c.KeyTypeId == "11" && c.DifficultyName == "Extreme");
    }

    [Fact]
    public void ScanDifData_DoesNotRequireFrameSection_UnlikeImport()
    {
        // Import()は$frame必須(無ければ例外)だが、ScanDifDataはdifDataだけを見るので不要
        var text = """
        $header=
        |difData=5,Normal,3.5|
        """;
        var result = FujiImporter.ScanDifData(text);
        Assert.Single(result);
    }

    [Fact]
    public void MissingInitialSpeed_ReturnsNullSpeed()
    {
        var text = """
        $header=
        |difData=5,Normal|
        """;
        var result = FujiImporter.ScanDifData(text);
        var c = Assert.Single(result);
        Assert.Null(c.InitialSpeed);
    }
}
