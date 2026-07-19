using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Models;

/// <summary>
/// プロジェクトファイル(仕様書3.1/5章)。1プロジェクト = 1曲。
/// 難易度はタブ(DifficultyTab)のリストとして保持し、並び順がdos.txt出力時のサフィックス採番順になる
/// (先頭タブ=無サフィックス、2番目以降=2,3...)。
/// </summary>
public sealed class ChartProject
{
    public string ProjectName { get; set; } = "untitled";

    // --- 曲全体(全難易度共通)の情報(仕様書5章) ---
    public string MusicTitle { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string ArtistUrl { get; set; } = "";
    public string MusicUrl { get; set; } = "noname";

    /// <summary>目テスト・プレイテスト時にエディタが再生するローカル音源の絶対パス(dos.txtには出力しない。
    /// MusicUrlはdos.txt上の相対パス文字列であり、これとは別物)</summary>
    public string AudioFilePath { get; set; } = "";
    public string Tuning { get; set; } = "name";
    public int StartFrame { get; set; } = 0;
    public int BlankFrame { get; set; } = 0;
    public int FrzAttempt { get; set; } = 5;

    /// <summary>StartNumber: 小節0の頭が置かれる絶対フレーム(仕様書7.3。実運用ではblankFrameと同値)</summary>
    public double StartNumber { get; set; } = 0;

    /// <summary>BPM変化点(曲共通、仕様書7.3)。tick 0に初期BPM必須。</summary>
    public List<BpmEvent> BpmEvents { get; set; } = [new(0, 120)];

    /// <summary>拍子変化点(曲共通、仕様書7.5)。未配置区間は直前の値を継続、デフォルト4/4。</summary>
    public List<TimeSignatureEvent> TimeSignatures { get; set; } = [];

    /// <summary>マーカー(dos.txtには出力されないエディタ専用オブジェクト、仕様書7.4)</summary>
    public List<Marker> Markers { get; set; } = [];

    /// <summary>目視テスト/プレイテストの再生開始フレーム(2026-07-17f、未解決事項§2-2)。
    /// null=未設定(曲頭から再生)。マーカーレーンのダブルクリックで設定、BackSpaceキーでリセット。
    /// dos.txtには出力されないエディタ専用の再生設定だが、プロジェクトファイルには永続化する。</summary>
    public double? PlaybackStartFrame { get; set; }

    /// <summary>その他のヘッダーパラメータ(仕様書6.4.4)。「使用する」チェックONのもののみ格納。</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];

    /// <summary>難易度タブ(並び順=出力順=サフィックス採番順)</summary>
    public List<DifficultyTab> Tabs { get; set; } = [];

    public TimingEngine CreateTimingEngine() =>
        new(StartNumber, BpmEvents, TimeSignatures);
}

/// <summary>難易度タブ(仕様書5章)。難易度ごとにキー種(テンプレート)が異なってよい。</summary>
public sealed class DifficultyTab
{
    public string DifficultyName { get; set; } = "";
    public string KeyTypeId { get; set; } = "5";
    public double InitialSpeed { get; set; } = 3.5;

    /// <summary>difDataの4フィールド目以降(ゲージ設定等)をそのまま保持(例: "0,2,25,50")</summary>
    public string? DifDataExtra { get; set; }

    /// <summary>レーンごとのノートデータ。インデックスはテンプレートのlanes[]と対応。</summary>
    public List<LaneNotes> Lanes { get; set; } = [];

    /// <summary>speed_data変化点(tick単位、仕様書9章)</summary>
    public List<ValueEvent> SpeedEvents { get; set; } = [];

    /// <summary>boost_data変化点(tick単位、仕様書9章)</summary>
    public List<ValueEvent> BoostEvents { get; set; } = [];

    /// <summary>難易度個別のsetColor上書き(null=曲共通を使用、仕様書6.4.2)</summary>
    public List<string>? SetColorOverride { get; set; }

    /// <summary>難易度個別のfrzColor上書き(null=曲共通を使用)</summary>
    public List<string>? FrzColorOverride { get; set; }

    /// <summary>テンプレートに合わせてレーン数を初期化する</summary>
    public static DifficultyTab CreateFor(KeyTemplate template, string name, double initialSpeed = 3.5)
    {
        var tab = new DifficultyTab
        {
            DifficultyName = name,
            KeyTypeId = template.KeyTypeId,
            InitialSpeed = initialSpeed,
        };
        for (int i = 0; i < template.KeyCount; i++) tab.Lanes.Add(new LaneNotes());
        return tab;
    }
}

/// <summary>1レーン分のノートデータ(位置はすべてtick単位=拍管理、仕様書7.2)</summary>
public sealed class LaneNotes
{
    /// <summary>通常ノートのtick位置(昇順を保証しない。出力時にソート)</summary>
    public List<long> Notes { get; set; } = [];

    /// <summary>フリーズアロー(始点tick, 終点tick)</summary>
    public List<FreezeNote> Freezes { get; set; } = [];
}

public sealed record FreezeNote(long StartTick, long EndTick);

/// <summary>speed/boost等の値変化点(tick位置+値)</summary>
public sealed record ValueEvent(long Tick, double Value);

/// <summary>エディタ専用マーカー(仕様書7.4)</summary>
public sealed record Marker(long Tick, string Comment);
