using System.IO;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;
using NAudio.Wave;

namespace DanoniEditor.App;

/// <summary>
/// ハンドクラップ効果音のデコード済みPCMデータ保持クラス(2026-07-26要望対応、2026-07-26f全面改修)。
///
/// 実装の変遷(発音の遅延=レイテンシ対策):
/// - 当初System.Media.SoundPlayerで実装(再生の都度ストリームを読み直すため毎回ロード待ちが発生)。
/// - 次にWPFのMediaPlayerを複数インスタンスであらかじめOpen()しておくプール方式に変更したが、
///   MediaPlayer(Media Foundationパイプライン)はPlay()の都度セッション開始のオーバーヘッドが
///   避けられず、体感できる遅延が残った。
/// - 2026-07-26c: NAudioのWasapiOut+MixingSampleProviderによる低レイテンシ再生方式(CachedSound
///   パターン)に置き換えたが、これは「クラップだけ」を専用の別デバイス出力に乗せる方式であり、
///   BGM側(WPFのMediaPlayer)の再生位置をUIスレッドのタイマーでポーリングして「通過したら鳴らす」
///   という判定をしていたため、①MediaPlayer.Positionの粗さ・遅延、②DispatcherTimerの16ms粒度、
///   ③クラップ専用WasapiOutの別デバイスバッファ、の3つが積み重なり、依然としてラグが残っていた。
/// - 2026-07-26f: BGM再生自体をNAudioBgmPlayer(同ディレクトリ)へ全面移行し、クラップの発音判定・
///   PCM重ね合わせをBGMのレンダースレッド内で直接行う設計に変更した(NAudioBgmPlayer.SetClapSchedule
///   参照)。BGMと同じ音声クロック上で発音位置を決めるため、UIスレッドのポーリングも別デバイス
///   バッファも介さない(BMS等の外部キー音再生方式に近い構造)。これによりこのクラス自身はもう
///   出力デバイスを持たず、「効果音をデコードしてメモリに持つだけ」の役割に単純化した。
///
/// 音源ファイルは元々./settings/clap.wav固定だったが、後にクラップ以外の音も選べる「ノート音」
/// 機能へ拡張し、参照先を./sounds(環境設定「テスト再生 > 全般」で選択したファイル)へ変更した。
/// クラス名・コメントは既存実装踏襲のため「クラップ」表記のまま残しているが、実際に鳴らす音は
/// 任意の音声ファイルになり得る。
/// </summary>
internal sealed class HandClapPlayer
{
    /// <summary>デコード済みの効果音本体(メモリ上のfloat PCM、1回だけ読み込む)。</summary>
    public float[] AudioData { get; }
    public WaveFormat WaveFormat { get; }
    public bool Available { get; }

    public HandClapPlayer(string wavPath)
    {
        if (!File.Exists(wavPath))
        {
            AudioData = [];
            WaveFormat = new WaveFormat();
            Available = false;
            return;
        }
        try
        {
            using var reader = new AudioFileReader(wavPath);
            WaveFormat = reader.WaveFormat;
            var wholeFile = new List<float>((int)(reader.Length / 4 + 1));
            var readBuffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int samplesRead;
            while ((samplesRead = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
                wholeFile.AddRange(new ArraySegment<float>(readBuffer, 0, samplesRead));
            AudioData = wholeFile.ToArray();
            Available = true;
        }
        catch
        {
            AudioData = [];
            WaveFormat = new WaveFormat();
            Available = false;
        }
    }

    /// <summary>指定タブの全レーンから、ノート(通常+フリーズ始点)が存在するtickをframeへ変換し、
    /// 昇順・重複除去したリストを返す(クラップを鳴らすタイミングの一覧)。
    /// offsetFramesはプレイテストの調整オフセットなど、呼び出し元の基準に合わせて加算する。</summary>
    public static List<double> ComputeNoteFrames(DifficultyTab tab, TimingEngine engine, double offsetFrames = 0)
    {
        var ticks = new HashSet<long>();
        foreach (var lane in tab.Lanes)
        {
            foreach (var t in lane.Notes) ticks.Add(t);
            foreach (var f in lane.Freezes) ticks.Add(f.StartTick);
        }
        return ticks.Select(t => engine.TickToFrame(t) + offsetFrames).OrderBy(f => f).ToList();
    }
}
