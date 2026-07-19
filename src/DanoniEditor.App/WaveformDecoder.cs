using System.IO;
using System.Runtime.InteropServices;
using DanoniEditor.Core.Audio;
using NAudio.Wave;

namespace DanoniEditor.App;

/// <summary>
/// 音声ファイルをデコードして波形ピークキャッシュを作る(2026-07-18)。
/// NAudioのAudioFileReader(mp3/wav/wma等、Windows Media Foundation依存)を使用。
/// oggは未対応(読み込み失敗時は呼び出し側がメッセージを出して波形OFFに戻す)。
/// 重い処理のためTask.Runで呼ぶこと。
/// </summary>
internal static class WaveformDecoder
{
    public static WaveformPeaks Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        using var reader = new AudioFileReader(path); // float PCM、任意チャンネル
        int channels = reader.WaveFormat.Channels;
        int rate = reader.WaveFormat.SampleRate;

        var mono = new List<float>(1 << 20);
        var buf = new float[rate * channels]; // 約1秒ぶんずつ読む
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += buf[i + c];
                mono.Add(sum / channels);
            }
        }
        return WaveformPeaks.FromSamples(CollectionsMarshal.AsSpan(mono), rate);
    }
}
