using DanoniEditor.Core.Audio;

namespace DanoniEditor.Core.Tests;

public class Base64MusicFileNameTests
{
    private static byte[] Mp3Like(byte seed) => [0x49, 0x44, 0x33, 0x03, seed, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    [Fact]
    public void BuildContentFileName_SameBytes_GivesSameName()
    {
        Assert.Equal(Base64MusicDecoder.BuildContentFileName(Mp3Like(1)), Base64MusicDecoder.BuildContentFileName(Mp3Like(1)));
    }

    [Fact]
    public void BuildContentFileName_DifferentBytes_GivesDifferentName()
    {
        Assert.NotEqual(Base64MusicDecoder.BuildContentFileName(Mp3Like(1)), Base64MusicDecoder.BuildContentFileName(Mp3Like(2)));
    }

    [Fact]
    public void BuildContentFileName_HasPrefixAndExtension()
    {
        var name = Base64MusicDecoder.BuildContentFileName(Mp3Like(1));
        Assert.StartsWith(Base64MusicDecoder.DecodedFilePrefix, name);
        Assert.EndsWith(".mp3", name);
        Assert.True(Base64MusicDecoder.IsDecodedFileName(name));
    }

    [Theory]
    [InlineData("danoni_music_b1f1b155ca4b4204a36d9c2245074455.mp3", true)]
    [InlineData("DANONI_MUSIC_abc.wav", true)]
    [InlineData("song.mp3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDecodedFileName_RecognizesLegacyAndNewNames(string? name, bool expected)
    {
        Assert.Equal(expected, Base64MusicDecoder.IsDecodedFileName(name));
    }
}
