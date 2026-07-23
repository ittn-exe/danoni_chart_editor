using System.Text.Json.Serialization;
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

    /// <summary>customGauge/gaugeXXX機能(2026-08-01、GaugeEditorWindow)。
    /// ゲージ名別パラメータ(border/recovery/damage/initLife)はプロジェクト全体で共有し、
    /// キー=ゲージ名(例: "Original", "Heavy", ユーザー定義名等)。
    /// 個々のGaugeParamSet.PerTabCsvは「難易度タブ数」分の要素を持ち、各要素は
    /// "ノルマ(またはx固定),回復,ダメージ,初期ライフ" のCSV文字列(未設定タブは空文字列とし、
    /// 出力時にgaugeXXXの$結合へそのまま渡す。本体側がgauges[j] || gauges[0]で
    /// 先頭タブへ自動フォールバックするため、空要素のままで問題ない)。</summary>
    public Dictionary<string, GaugeParamSet> GaugeParams { get; set; } = [];

    /// <summary>「直接入力モード」(2026-08-01、ユーザー確定仕様)。空でなければ、ゲージ関連ヘッダー
    /// (customGauge系・gaugeXXX系)の出力はこのテキストの内容(dos.txtにそのまま書き込む前提の
    /// 生テキスト、複数行可)で完全に置き換えられ、GaugeParams/DifficultyTab.Gaugeによる
    /// UI構築ロジックは無視される(過去資産からのコピペ用途、プロジェクト全体で1つ)。</summary>
    public string? GaugeRawOverrideText { get; set; }

    public TimingEngine CreateTimingEngine() =>
        new(StartNumber, BpmEvents, TimeSignatures);
}

/// <summary>難易度タブ(仕様書5章)。難易度ごとにキー種(テンプレート)が異なってよい。</summary>
public sealed class DifficultyTab
{
    public string DifficultyName { get; set; } = "";
    public string KeyTypeId { get; set; } = "5";
    public double InitialSpeed { get; set; } = 3.5;

    /// <summary>難易度タブ見出し表示用の算出プロパティ(2026-07-21要望)。
    /// 「キー種k - 難易度名」形式(例: "5k - Normal"、"11Lk - Hard")。
    /// 難易度名が未設定(空白含む)の場合は"(newdiff)"を表示する。
    /// プロジェクトファイルには永続化しない(タブ見出し表示専用の派生値のため[JsonIgnore])。</summary>
    [JsonIgnore]
    public string DisplayLabel =>
        $"{KeyTypeId}k - {(string.IsNullOrWhiteSpace(DifficultyName) ? "(newdiff)" : DifficultyName)}";

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

    /// <summary>customGauge{N}(仕様dos-h0053、2026-08-01)。null=このタブはゲージ名リストを
    /// 指定しない(customGauge{N}ヘッダー自体を出力しない=本体の既定ゲージが使われる)。</summary>
    public GaugeConfig? Gauge { get; set; }

    /// <summary>歌詞表示レーン(仕様dos-e0003-wordData、2026-07-23、TBD 4)。ユーザーが任意に追加/削除できる
    /// (既定0本=歌詞表示機能を使わないプロジェクトでは何も出力されない)。同一タブ内でIsReverseが同じ
    /// レーンが複数ある場合、出力時はフレーム順にマージして1つのword_data(またはwordRev_data)にまとめる
    /// (DosExporter参照)。</summary>
    public List<WordLane> WordLanes { get; set; } = [];

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

    /// <summary>ncolor_data個別色指定(色編集モード、2026-07-23)。通常ノートはNotes中のtickで、
    /// フリーズはFreezes中のStartTickで同定する(1レーン内でtickが重複することは無い前提)。
    /// ノート移動・削除の際はこのリストのエントリも追随させる必要がある(EditActions.cs参照)。</summary>
    public List<NColorEntry> ColorOverrides { get; set; } = [];

    /// <summary>ノート/フリーズのコメント・警告フラグ(2026-07-26、ユーザー確定仕様)。同定方法は
    /// ColorOverridesと同じ(通常ノート=tick、フリーズ=StartTick)。インポート時に丸め処理等を行った
    /// オブジェクトへ、エラーダイアログと同じ文言のコメント+警告ON(Warning=true)を記録する。
    /// 警告は編集操作では自動クリアせず、ユーザーがプロパティパネルで手動OFFするまで残す
    /// (保存・再読込でも維持=シリアライズ対象)。移動・削除の際はColorOverrides同様に追随させる
    /// (EditActions.cs参照)。譜面ビューではWarning=trueのオブジェクトに警告アイコンを重ね描きする。</summary>
    public List<NoteAnnotation> Annotations { get; set; } = [];
}

/// <summary>ノート/フリーズ1件分のコメント・警告(2026-07-26)。Comment=""かつWarning=falseの
/// エントリはリストから削除してよい(空エントリを残さない規約)。</summary>
public sealed record NoteAnnotation(long Tick, string Comment, bool Warning);

public sealed record FreezeNote(long StartTick, long EndTick);

/// <summary>speed/boost等の値変化点(tick位置+値)</summary>
public sealed record ValueEvent(long Tick, double Value);

/// <summary>エディタ専用マーカー(仕様書7.4)</summary>
public sealed record Marker(long Tick, string Comment);

/// <summary>ncolor_dataの個別色指定1件(2026-07-23、仕様書TBD「色編集モード」)。
/// 通常ノートはColorのみを使う(BandColorは常にnull)。フリーズは始点・終点(端点、本家のNormal相当)と
/// 帯(本家のNormalBar相当)を別々に持てる。ColorCodeの書式はdos.txt側のncolor_dataのColorCode欄
/// (#RRGGBB、色名、コロン区切りグラデーション記法)をそのまま格納する。
/// AllFlag(2026-07-24)はncolor_dataの4番目のフィールド(即時適用フラグ)に対応する。trueの場合、
/// 本家仕様上は「指定フレームの時点で既に出現済みの矢印/フリーズも含めて即座に塗り替える」
/// (全体色変化)。falseの場合は「以後新規出現するものだけに適用」(個別色変化、従来の既定動作)。
/// エントリ全体で1つのフラグを共有する(ColorとBandColorを別々には持たない)。
/// ShadowColor/HitColor/HitBarColor/HitShadowColor(2026-07-24、frzHitColor/ShadowColor編集モード用):
/// ShadowColorは通常ノートのArrowShadow、フリーズのNormalShadowを兼ねる(tickが属する実体の種別で
/// 判別する)。HitColor/HitBarColor/HitShadowColorはフリーズのヒット時(判定中)専用で、
/// 通常ノートには存在しない。</summary>
public sealed record NColorEntry(long Tick, string? Color, string? BandColor, bool AllFlag = false,
    string? ShadowColor = null, string? HitColor = null, string? HitBarColor = null, string? HitShadowColor = null);

/// <summary>難易度タブ1件分のcustomGauge{N}設定(2026-08-01、仕様dos-h0053)。
/// InheritKeywordが設定されていれば継承キーワード(survival/border/customDefault)そのものを
/// customGauge{N}の値として出力し、Entriesは無視する。InheritKeywordがnullの場合のみ
/// Entriesの明示リストを出力する(両方null/空の場合はこのタブのcustomGauge{N}自体を出力しない)。</summary>
public sealed class GaugeConfig
{
    /// <summary>"survival" / "border" / "customDefault" のいずれか、または未使用ならnull</summary>
    public string? InheritKeyword { get; set; }

    /// <summary>明示的なゲージ名リスト(InheritKeywordがnullの場合のみ使用)</summary>
    public List<GaugeListEntry> Entries { get; set; } = [];
}

/// <summary>customGauge{N}の明示リスト1項目分(name::F|V(::displayName)?)。</summary>
public sealed record GaugeListEntry(string Name, bool IsVariable, string? DisplayName = null);

/// <summary>gauge{ゲージ名}{N}(仕様dos-h0022)。プロジェクト全体でゲージ名ごとに1つ持つ。
/// PerTabCsvの要素数は難易度タブ数と一致させる想定(GaugeEditorWindow側で維持管理)。</summary>
public sealed class GaugeParamSet
{
    /// <summary>タブごとの"ノルマ(またはx),回復,ダメージ,初期ライフ"のCSV文字列。
    /// 空文字列のタブは出力時そのまま空セグメントとして$結合し、本体側のgauges[j] || gauges[0]
    /// フォールバックに委ねる(先頭タブと同じ値が使われる)。</summary>
    public List<string> PerTabCsv { get; set; } = [];
}

// =====================================================================
// 歌詞表示(word_data、仕様dos-e0003-wordData、2026-07-23、TBD 4)
// =====================================================================

/// <summary>歌詞レーン1本(ユーザーが任意に追加できる、DifficultyTab.WordLanes参照)。
/// IsReverseが出力先データ名(word_data系 or wordRev_data系)を決める唯一のプロパティ。
/// 将来、多言語(Ja/En)やスクロール種別(Cross/Split/Flat)対応が必要になった場合も、
/// このクラスへプロパティを追加する形で拡張していく想定(現時点では非対応、TBDとして別途記録)。</summary>
public sealed class WordLane
{
    /// <summary>UI表示用のレーン名(ユーザー編集可、出力には影響しない)</summary>
    public string Name { get; set; } = "歌詞";

    /// <summary>true=wordRev_data系(Reverse専用表示)として出力、false=word_data系(通常表示)</summary>
    public bool IsReverse { get; set; }

    public List<WordEntry> Entries { get; set; } = [];
}

/// <summary>歌詞表示1行分の種別(dos-e0003-wordData「使い方」節の3パターンに対応)。</summary>
public enum WordEntryKind
{
    /// <summary>通常表記(Frame,Position,Lyrics)</summary>
    Lyrics,
    /// <summary>歌詞変化(Frame,Position,[fadein]等のキーワード,FadeFrame(fadein/fadeout時のみ))</summary>
    Control,
    /// <summary>コメント行(Frame,-,Comment)。Positionは出力上"-"固定でありEntry.Positionの値は無視される。</summary>
    Comment,
}

/// <summary>歌詞表示1件分。KindがControlの場合、Textは"[fadein]"等のキーワード文字列そのものを保持する。</summary>
public sealed record WordEntry(long Tick, int Position, WordEntryKind Kind, string Text, int? FadeFrame = null);
