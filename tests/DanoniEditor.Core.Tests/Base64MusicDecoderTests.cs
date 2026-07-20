using DanoniEditor.Core.Audio;

namespace DanoniEditor.Core.Tests;

/// <summary>
/// Base64MusicDecoder(仕様書TBD#6、danoniplus wiki dos-h0011-musicUrl準拠、2026-07-20)のテスト。
/// フォーマットは実際のwikiページと変換ツール(suzme/danoni-base64)のソースで確認済み:
/// function musicInit(){g_musicdata='&lt;base64&gt;'}
/// </summary>
public class Base64MusicDecoderTests
{
    [Fact]
    public void TryExtractBase64_SingleQuoted_Extracts()
    {
        var content = "function musicInit(){g_musicdata='SGVsbG8='}";
        Assert.True(Base64MusicDecoder.TryExtractBase64(content, out var b64));
        Assert.Equal("SGVsbG8=", b64);
    }

    [Fact]
    public void TryExtractBase64_MultilineWithSemicolon_Extracts()
    {
        // wiki記載のもう一つの書式(複数行・セミコロン付き)にも対応する
        var content = "function musicInit(){\n  g_musicdata='SGVsbG8=';\n}";
        Assert.True(Base64MusicDecoder.TryExtractBase64(content, out var b64));
        Assert.Equal("SGVsbG8=", b64);
    }

    [Fact]
    public void TryExtractBase64_NoMatch_ReturnsFalse()
    {
        Assert.False(Base64MusicDecoder.TryExtractBase64("not a music file", out _));
    }

    [Fact]
    public void DecodeToBytes_ValidBase64_ReturnsOriginalBytes()
    {
        var original = "Hello, danoni!"u8.ToArray();
        var b64 = Convert.ToBase64String(original);
        var content = $"function musicInit(){{g_musicdata='{b64}'}}";

        var decoded = Base64MusicDecoder.DecodeToBytes(content);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeToBytes_InvalidBase64_ReturnsNull()
    {
        var content = "function musicInit(){g_musicdata='not-valid-base64!!!'}";
        Assert.Null(Base64MusicDecoder.DecodeToBytes(content));
    }

    [Fact]
    public void DecodeToBytes_NoMusicData_ReturnsNull()
    {
        Assert.Null(Base64MusicDecoder.DecodeToBytes("plain text file"));
    }

    [Fact]
    public void GuessExtension_Wav_DetectsRiffWave()
    {
        var bytes = new byte[16];
        "RIFF"u8.ToArray().CopyTo(bytes, 0);
        "WAVE"u8.ToArray().CopyTo(bytes, 8);
        Assert.Equal(".wav", Base64MusicDecoder.GuessExtension(bytes));
    }

    [Fact]
    public void GuessExtension_Ogg_DetectsOggS()
    {
        var bytes = "OggS____"u8.ToArray();
        Assert.Equal(".ogg", Base64MusicDecoder.GuessExtension(bytes));
    }

    [Fact]
    public void GuessExtension_Mp3WithId3_Detects()
    {
        var bytes = "ID3____"u8.ToArray();
        Assert.Equal(".mp3", Base64MusicDecoder.GuessExtension(bytes));
    }

    [Fact]
    public void GuessExtension_Mp3FrameSyncWithoutId3_Detects()
    {
        var bytes = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        Assert.Equal(".mp3", Base64MusicDecoder.GuessExtension(bytes));
    }

    [Fact]
    public void GuessExtension_Unrecognized_FallsBackToMp3()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        Assert.Equal(".mp3", Base64MusicDecoder.GuessExtension(bytes));
    }
}
