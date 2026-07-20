using System.Text;
using DanoniEditor.Core.Import;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// DroppedFileClassifier(仕様書TBD#7、2026-07-20)のテスト。拡張子だけでは判別できない組み合わせ
/// (FUJI/SKB/dos.txtの.txt共有、自形式/SKBの.json共有)が正しく中身で判定されることを確認する。
/// </summary>
public class DroppedFileClassifierTests
{
    private static DroppedFileKind Classify(string fileName, string text) =>
        DroppedFileClassifier.Classify(fileName, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void OwnProjectJson_IsClassified()
    {
        var json = """{"schemaVersion":2,"project":{"projectName":"test"}}""";
        Assert.Equal(DroppedFileKind.OwnProject, Classify("chart.json", json));
    }

    [Fact]
    public void SkbJson_IsClassified_EvenWithTxtExtension()
    {
        var json = """{"keyKind":"7","scores":[],"blankFrame":0,"timings":[],"scoreNumber":1}""";
        Assert.Equal(DroppedFileKind.Skb, Classify("chart.json", json));
        Assert.Equal(DroppedFileKind.Skb, Classify("chart.txt", json)); // SKBは.txt保存の場合もある
    }

    [Fact]
    public void FujiText_IsClassified()
    {
        var text = "$version=3.050\n$frame=0/0/9600/1,10\n$score=\n0000:0040,\n";
        Assert.Equal(DroppedFileKind.Fuji, Classify("chart.txt", text));
    }

    [Fact]
    public void DosText_IsClassified()
    {
        var text = "|musicTitle=テスト曲|\n|difData=5,Normal,3.5|\n";
        Assert.Equal(DroppedFileKind.Dos, Classify("dos.txt", text));
    }

    [Fact]
    public void DosTextWrappedInJs_IsStillClassifiedAsDos()
    {
        // dos.txtはexternalDosInit()でJSラップされることがある(DosExporter参照)
        var text = "function externalDosInit() {\n  g_externalDos = `|musicTitle=テスト|`;\n}";
        Assert.Equal(DroppedFileKind.Dos, Classify("dos.js", text));
    }

    [Fact]
    public void Base64MusicJs_IsClassified_RegardlessOfExtension()
    {
        var text = "function musicInit(){g_musicdata='AAAA'}";
        Assert.Equal(DroppedFileKind.Base64Music, Classify("song.js", text));
        Assert.Equal(DroppedFileKind.Base64Music, Classify("song.txt", text));
    }

    [Theory]
    [InlineData("song.mp3")]
    [InlineData("song.wav")]
    [InlineData("song.wma")]
    [InlineData("song.ogg")]
    public void AudioExtension_IsClassifiedAsRawAudio_RegardlessOfContent(string fileName)
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 }; // 中身は無関係(拡張子で判定)
        Assert.Equal(DroppedFileKind.RawAudio, DroppedFileClassifier.Classify(fileName, bytes));
    }

    [Fact]
    public void UnrecognizedText_IsUnknown()
    {
        Assert.Equal(DroppedFileKind.Unknown, Classify("readme.txt", "これはただのメモです。"));
    }

    [Fact]
    public void UnrecognizedJson_IsUnknown()
    {
        var json = """{"foo":"bar"}""";
        Assert.Equal(DroppedFileKind.Unknown, Classify("data.json", json));
    }

    [Fact]
    public void BinaryGarbage_IsUnknown()
    {
        // 0xFFはUTF-8で単独使用が許されないバイトのため、確実にテキストデコードに失敗する
        var bytes = Enumerable.Repeat((byte)0xFF, 64).ToArray();
        Assert.Equal(DroppedFileKind.Unknown, DroppedFileClassifier.Classify("mystery.bin", bytes));
    }

    [Fact]
    public void EmptyFile_IsUnknown()
    {
        Assert.Equal(DroppedFileKind.Unknown, DroppedFileClassifier.Classify("empty.txt", []));
    }
}
