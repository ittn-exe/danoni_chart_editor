using System.Text.Json;
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

    /// <summary>2026-08-08要望対応(再設計版): tick&lt;0(frame&lt;0、チャートのtick0より手前)への
    /// オブジェクト配置を許可するかどうか。既定false。trueの間、譜面ビュー上のクリック配置・
    /// ペーストでtick&lt;0への配置が可能になる(SmartToolController.SnappedTickAt/Paste/
    /// PasteWithLaneMapping参照)。BPMイベントはTimingEngineの前提(tick0に先頭BPM必須)に
    /// 直結するため、この設定に関わらず常に対象外(tick&lt;0への配置は禁止のまま)。
    /// このフラグは「新規配置の可否」のみを制御し、既にtick&lt;0にあるオブジェクト(このフラグが
    /// falseの間に外部形式からインポートされた場合等)を削除・警告する機能ではない
    /// (TimingEngine.TickToFrameは元々tick&lt;0でも正しく変換できるため、フラグの値に関わらず
    /// エクスポート結果には影響しない)。dos.txtへは出力しないエディタ専用設定のため、
    /// SerializeTabExportの対象には含めない。</summary>
    public bool AllowNegativeFramePlacement { get; set; } = false;

    /// <summary>StartNumber: 小節0の頭が置かれる絶対フレーム(仕様書7.3。実運用ではblankFrameと同値)</summary>
    public double StartNumber { get; set; } = 0;

    /// <summary>BPM変化点(曲共通、仕様書7.3)。tick 0に初期BPM必須。</summary>
    public List<BpmEvent> BpmEvents { get; set; } = [new(0, 120)];

    /// <summary>拍子変化点(曲共通、仕様書7.5)。未配置区間は直前の値を継続、デフォルト4/4。</summary>
    public List<TimeSignatureEvent> TimeSignatures { get; set; } = [];

    /// <summary>マーカー(dos.txtには出力されないエディタ専用オブジェクト、仕様書7.4)</summary>
    public List<Marker> Markers { get; set; } = [];

    /// <summary>【旧・互換用】目視テスト/プレイテストの再生開始フレーム(2026-07-17f、未解決事項§2-2)。
    /// 2026-08-04不具合修正: 複数の難易度タブが同一プロジェクト内で本値を共有していたため、
    /// タブを切り替えても再生開始位置がリセットされず、意図しない位置に開始ラインが残る不具合が
    /// あった(第三者からの報告)。以後は<see cref="DifficultyTab.PlaybackStartFrame"/>がタブごとに
    /// 独立した値を持つ形へ移行し、本プロパティは旧形式プロジェクトファイルの読み込み専用の
    /// 互換フィールドとして残す。旧ファイルを開いた直後はこの値が入っており、EditorDocument側が
    /// 保存タイミングで各タブへ振り分けたうえで本プロパティをnullへクリアする(移行処理完了後は
    /// 常にnull)。新規プロジェクトでは一切使用しない。</summary>
    public double? PlaybackStartFrame { get; set; }

    /// <summary>譜面ビューの縦方向ズーム(ChartLayout.PxPerTick、Shift+ホイール)・横方向ズーム
    /// (ChartLayout.ZoomScale、Alt+ホイール)の保存値(2026-07-26要望対応)。dos.txtには出力されない
    /// エディタ専用の表示設定だが、プロジェクトファイルには永続化し次回オープン時に復元する。
    /// null=未設定(エディタ既定値を使用、旧プロジェクトファイルとの後方互換)。</summary>
    public double? EditorZoomPxPerTick { get; set; }
    public double? EditorZoomScale { get; set; }

    /// <summary>スナップ分解能(SnapService.Division、上部パネルのプルダウン)の保存値
    /// (2026-08-02要望対応)。dos.txtには出力されないエディタ専用の編集設定だが、作業中に
    /// 頻繁に切り替える値のため、プロジェクトごとに前回値を覚えておいてほしいという要望対応。
    /// null=未設定(エディタ既定値16分を使用、旧プロジェクトファイルとの後方互換)。</summary>
    public int? SnapDivision { get; set; }

    /// <summary>目視テスト・プレイテストの再生音量(0.0〜1.0)の保存値(2026-08-02要望対応)。
    /// 音源によって適正音量が異なるため、AppSettings.PlaybackVolume(エディタ全体の既定値・
    /// 新規プロジェクト作成時のフォールバック用)とは別に、プロジェクトごとに個別の値を
    /// 覚えておけるようにする。dos.txtには出力されないエディタ専用設定。
    /// null=未設定(AppSettings.PlaybackVolumeを使用、旧プロジェクトファイルとの後方互換)。</summary>
    public double? PlaybackVolume { get; set; }

    /// <summary>その他のヘッダーパラメータ(仕様書6.4.4)。「使用する」チェックONのもののみ格納。</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];

    /// <summary>プラグイン用の自由記述領域(2026-07-26、プラグイン対応の土台)。キーは
    /// "{プラグインID}.{任意のキー名}" の形で自動的に名前空間分けされ(PluginHostImpl参照)、
    /// 値は各プラグインが自由な形式(JSON文字列等)で読み書きする。dos.txtへは出力されない。
    /// エディタ本体はこの中身を一切解釈しない。</summary>
    public Dictionary<string, string> PluginData { get; set; } = [];

    /// <summary>難易度タブ(並び順=出力順=サフィックス採番順)</summary>
    public List<DifficultyTab> Tabs { get; set; } = [];

    /// <summary>customGauge/gaugeXXX機能で使うゲージ名の宣言(名前+既定表示名)の並び順
    /// (2026-07-26、GaugeEditorWindow。2026-07-30再設計でstringからGaugeNameDefへ変更、宣言時に
    /// 既定表示名を持てるようにした)。表の行順・出力順を保持するためだけのプロジェクト全体の情報で、
    /// 実際のパラメータ値(border/recovery/damage/initLife)はタブごとに
    /// <see cref="DifficultyTab.GaugeParams"/>が持つ(2026-07-24: 旧GaugeParamSet.PerTabCsvはタブ削除時に
    /// インデックス調整が漏れて値がズレる不具合があったため、他のタブ別設定(setColor/frzColor/
    /// customGauge)と同じ「タブ自身が値を持つ」方式へ統一した。タブを削除すればそのタブの値も
    /// 一緒に破棄されるだけで整合するようになる)。</summary>
    public List<GaugeNameDef> GaugeNames { get; set; } = [];

    /// <summary>「直接入力モード」(2026-07-26、ユーザー確定仕様)。空でなければ、ゲージ関連ヘッダー
    /// (customGauge系・gaugeXXX系)の出力はこのテキストの内容(dos.txtにそのまま書き込む前提の
    /// 生テキスト、複数行可)で完全に置き換えられ、GaugeParams/DifficultyTab.Gaugeによる
    /// UI構築ロジックは無視される(過去資産からのコピペ用途、プロジェクト全体で1つ)。</summary>
    public string? GaugeRawOverrideText { get; set; }

    /// <summary>「dos作成後に直接編集する」フラグ(2026-07-26、ユーザー確定仕様、プロジェクト全体で1つ)。
    /// trueの間、エクスポート時にゲージ関連の出力(difData内のborder/recovery/damage/initLife%、
    /// customGauge系・gaugeXXX系ヘッダー、GaugeRawOverrideTextによる直接入力を含む)を一切行わない。
    /// エディタでは編集せず、書き出し後のdos.txtへユーザー自身がテキストエディタ等で直接追記する
    /// 運用を想定した機能のため、GaugeEditorWindow側の①②③④は本フラグON中すべて無効化する。</summary>
    public bool GaugeManualEditAfterExport { get; set; } = false;

    public TimingEngine CreateTimingEngine() =>
        new(StartNumber, BpmEvents, TimeSignatures);
}

/// <summary>難易度タブ(仕様書5章)。難易度ごとにキー種(テンプレート)が異なってよい。</summary>
public sealed class DifficultyTab
{
    /// <summary>タブの一意識別子(2026-07-26要望対応、タブリンク機能)。並び順や名前が変わっても
    /// リンク相手を一意に指し示すために使う(index参照だと並び替え・削除でズレるため)。
    /// プロジェクトファイルへ永続化するが、dos.txtには一切出力しない。</summary>
    public string TabId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>リンク中の相手タブのTabId(2026-07-26要望対応)。null=リンクなし。相互参照(双方が
    /// 互いのTabIdを持つ)。同じキー種のタブ同士でのみ結べる(App層が保証する、Core層では未検証)。
    /// リンク中はアクティブタブの背景に、非アクティブタブ(リンク相手)のノートを薄く表示する。</summary>
    public string? LinkedTabId { get; set; }

    public string DifficultyName { get; set; } = "";
    public string KeyTypeId { get; set; } = "5";
    public double InitialSpeed { get; set; } = 3.5;

    /// <summary>「dosロック」フラグ(2026-08-02要望対応)。trueの間、このタブはdosエクスポート時に
    /// 完全に除外される(difData一覧・ノート/速度/色等のデータブロックのどちらからも出力されない。
    /// DosExporter参照)。制作中で未公開にしたい譜面を、削除せずに一時的にエクスポート対象外へ
    /// できるようにするための機能。タブ複製時は引き継ぐ(2026-08-02ユーザー確定仕様、Clone()参照)。</summary>
    public bool ExcludeFromDosExport { get; set; } = false;

    /// <summary>難易度タブ見出し表示用の算出プロパティ(2026-07-21要望)。
    /// 「キー種k - 難易度名」形式(例: "5k - Normal"、"11Lk - Hard")。
    /// 難易度名が未設定(空白含む)の場合は"(newdiff)"を表示する。
    /// ExcludeFromDosExportがtrueの場合は先頭に"[×]"を付ける(2026-08-02要望対応、dosロック中の
    /// タブをタブ一覧上で一目で見分けられるようにするための表示)。
    /// プロジェクトファイルには永続化しない(タブ見出し表示専用の派生値のため[JsonIgnore])。</summary>
    [JsonIgnore]
    public string DisplayLabel =>
        $"{(ExcludeFromDosExport ? "[×] " : "")}{KeyTypeId}k - {(string.IsNullOrWhiteSpace(DifficultyName) ? "(newdiff)" : DifficultyName)}";

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

    /// <summary>customGauge{N}(仕様dos-h0053、2026-07-26)。null=このタブはゲージ名リストを
    /// 指定しない(customGauge{N}ヘッダー自体を出力しない=本体の既定ゲージが使われる)。</summary>
    public GaugeConfig? Gauge { get; set; }

    /// <summary>gauge{ゲージ名}{N}(仕様dos-h0022)のこのタブぶんの値(2026-07-24、旧
    /// ChartProject.GaugeParams/GaugeParamSet.PerTabCsvから移行)。キー=ゲージ名(ChartProject.GaugeNames
    /// に列挙されるもの)、値="ノルマ(またはx固定),回復,ダメージ,初期ライフ"のCSV文字列。
    /// キーが存在しない(未設定)場合は出力時に空文字列として扱う(本体側がgauges[j] || gauges[0]で
    /// 先頭タブへ自動フォールバックするため、空のままで問題ない)。null=このタブは1件も未設定。</summary>
    public Dictionary<string, string>? GaugeParams { get; set; }

    /// <summary>歌詞表示レーン(仕様dos-e0003-wordData、2026-07-23、TBD 4)。ユーザーが任意に追加/削除できる
    /// (既定0本=歌詞表示機能を使わないプロジェクトでは何も出力されない)。同一タブ内でIsReverseが同じ
    /// レーンが複数ある場合、出力時はフレーム順にマージして1つのword_data(またはwordRev_data)にまとめる
    /// (DosExporter参照)。</summary>
    public List<WordLane> WordLanes { get; set; } = [];

    /// <summary>時間情報レーンのドラッグによる「時間範囲選択」(tick単位、2026-07-27要望対応)。
    /// 元々はレーン入替マクロ専用の「選択範囲内のみ適用」機能だったが、時間情報レーンの汎用ドラッグ
    /// 操作へ置き換え、マクロの範囲スコープ指定・(将来の)ループ再生区間指定など複数機能で共有する
    /// 汎用の時間範囲選択として再定義した(旧名: MacroRangeStartTick)。
    /// 片方だけ設置された状態(null混在)もあり得る(その間はハイライト非表示、マクロの範囲適用も不可)。
    /// タブごとに独立して保持し、プロジェクトファイルへ永続化する。</summary>
    public long? TimeRangeSelectionStartTick { get; set; }

    /// <summary>TimeRangeSelectionStartTick参照。範囲の終点(tick単位、旧名: MacroRangeEndTick)。</summary>
    public long? TimeRangeSelectionEndTick { get; set; }

    /// <summary>目視テスト/プレイテストの再生開始フレーム(タブごとに独立、2026-08-04不具合修正)。
    /// null=未設定(曲頭から再生)。マーカー/時間情報レーンのダブルクリックで設定、BackSpaceキーで
    /// リセット。dos.txtには出力されないエディタ専用の再生設定だが、プロジェクトファイルには
    /// 永続化する。旧形式(<see cref="ChartProject.PlaybackStartFrame"/>がプロジェクト全体で1つだった
    /// 頃)のプロジェクトファイルを開いた場合、本値は保存時まで未設定のままで構わない
    /// (EditorDocumentが保存直前に旧値を振り分ける、詳細はChartProject.PlaybackStartFrameのコメント参照)。
    /// タブ複製時に引き継ぐかどうかはAppSettings.CarryOverPlaybackStartOnTabDuplicateによる
    /// (2026-08-04要望対応、既定は引き継がない)。</summary>
    public double? PlaybackStartFrame { get; set; }

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

    /// <summary>このタブの完全な複製を作る(2026-07-26、タブ複製機能)。ネストしたList/Dictionaryを
    /// 参照共有すると複製後どちらかを編集した際に相方も壊れるため、値の入れ物は全て新規に作り直す
    /// (中身のレコード型(ValueEvent/FreezeNote/NColorEntry等)自体はイミュータブルなため使い回してよい)。
    /// DifficultyNameは呼び出し側で設定する(複製直後は既定で変更するため、ここでは元の値のまま返す)。
    /// carryOverPlaybackStart(2026-08-04要望対応、AppSettings.CarryOverPlaybackStartOnTabDuplicateに連動):
    /// trueの場合のみPlaybackStartFrameを複製先へ引き継ぐ。既定false(複製先は未設定=曲頭から再生。
    /// 再生開始ラインをタブごとに独立させた趣旨に合わせ、既定では引き継がない)。</summary>
    public DifficultyTab Clone(bool carryOverPlaybackStart = false)
    {
        var clone = new DifficultyTab
        {
            // TabIdは複製先固有の新規値を採番する(元タブと同一視されないように)。
            // LinkedTabIdは意図的に複製しない(複製直後は誰ともリンクしていない状態にする、
            // 2026-07-26要望対応。片方だけコピーすると相互参照が崩れて事故のもとになるため)。
            DifficultyName = DifficultyName,
            KeyTypeId = KeyTypeId,
            InitialSpeed = InitialSpeed,
            ExcludeFromDosExport = ExcludeFromDosExport,
            DifDataExtra = DifDataExtra,
            SpeedEvents = new List<ValueEvent>(SpeedEvents),
            BoostEvents = new List<ValueEvent>(BoostEvents),
            SetColorOverride = SetColorOverride is null ? null : new List<string>(SetColorOverride),
            FrzColorOverride = FrzColorOverride is null ? null : new List<string>(FrzColorOverride),
            Gauge = Gauge is null ? null : new GaugeConfig
            {
                InheritKeyword = Gauge.InheritKeyword,
                Entries = new List<GaugeListEntry>(Gauge.Entries),
            },
            GaugeParams = GaugeParams is null ? null : new Dictionary<string, string>(GaugeParams),
            TimeRangeSelectionStartTick = TimeRangeSelectionStartTick,
            TimeRangeSelectionEndTick = TimeRangeSelectionEndTick,
            PlaybackStartFrame = carryOverPlaybackStart ? PlaybackStartFrame : null,
        };
        foreach (var lane in Lanes)
        {
            clone.Lanes.Add(new LaneNotes
            {
                Notes = new List<long>(lane.Notes),
                Freezes = new List<FreezeNote>(lane.Freezes),
                ColorOverrides = new List<NColorEntry>(lane.ColorOverrides),
                Annotations = new List<NoteAnnotation>(lane.Annotations),
            });
        }
        foreach (var wordLane in WordLanes)
        {
            clone.WordLanes.Add(new WordLane
            {
                Name = wordLane.Name,
                IsReverse = wordLane.IsReverse,
                Entries = new List<WordEntry>(wordLane.Entries),
            });
        }
        return clone;
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

/// <summary>ノート/フリーズ1件分のコメント・警告(2026-07-26)。Comment=""かつWarning=falseかつ
/// ShowIcon=falseのエントリはリストから削除してよい(空エントリを残さない規約)。
/// ShowIcon(2026-07-30要望対応、「コメント記載お知らせ用アイコン」): Warningとは独立したユーザー
/// 任意のON/OFF(プロパティパネルのチェックボックス)。trueの間、譜面ビューにコメント有りお知らせ
/// アイコン(SystemIcons.Application)を重ね描きする。Warning(SystemIcons.Warning、インポート時の
/// 自動フラグ)とは別系統で、両方同時にONにもできる。</summary>
public sealed record NoteAnnotation(long Tick, string Comment, bool Warning, bool ShowIcon = false);

public sealed record FreezeNote(long StartTick, long EndTick);

/// <summary>speed/boost等の値変化点(tick位置+値)。
/// LinkGridDivision(2026-07-30要望対応、「始点終点オートスムージング出力」): nullの場合は通常の
/// イベント(リンク無し)。非nullの場合、このイベントはtick順で直後(次)の同種イベント(speed同士/
/// boost同士)と自動的にリンクしており、両者の間を指定した設置間隔(4=4分/8=8分/16=16分/32=32分、
/// 既存ノートと同じ絶対グリッド基準)で区切った中間点を自動生成し、区間内を線形補間した値を
/// 割り当てる(ValueEventSmoothing.ExpandLinkedEvents参照)。生成される中間点はSpeedEvents/
/// BoostEvents自体には追加されず、dos.txt出力・プレイテスト・プレビューの速度/ブースト計算にのみ
/// 都度展開して用いる(選択・削除・ドラッグ移動の単純さを保つため)。
/// リンクは常に「tick順で早い方のイベントがLinkGridDivisionを持つ」形で表現するため、
/// 「1つ手前のイベントとリンクする」設定をしたい場合は、手前のイベント側のLinkGridDivisionを
/// 設定する(③プロパティパネルのUIが自動的にこの変換を行う)。</summary>
public sealed record ValueEvent(long Tick, double Value, int? LinkGridDivision = null);

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

/// <summary>難易度タブ1件分のcustomGauge{N}設定(2026-07-26、仕様dos-h0053)。
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

/// <summary>customGauge{N}の明示リスト1項目分(name::F|V(::displayName)?)。DisplayNameが未指定
/// (null/空)の場合、エクスポート時にChartProject.GaugeNamesで宣言された既定表示名があればそちらを
/// 使う(2026-07-30再設計、GaugeNameDef参照)。</summary>
public sealed record GaugeListEntry(string Name, bool IsVariable, string? DisplayName = null);

/// <summary>gaugeXXX(ChartProject.GaugeNames)の宣言1件分(2026-07-30再設計)。内部名(gauge{Name}
/// ヘッダーやcustomGauge{N}のリストが参照するキー)と、宣言時に設定できる既定表示名の組。
/// DisplayNameは各タブのGaugeListEntry.DisplayNameが空の場合のフォールバックとして使われるのみで、
/// タブ側で個別に上書きもできる。</summary>
[JsonConverter(typeof(GaugeNameDefConverter))]
public sealed record GaugeNameDef(string Name, string? DisplayName = null);

/// <summary>GaugeNameDefの「文字列 or オブジェクト」両対応コンバータ(2026-08-02不具合修正)。
/// 2026-07-30再設計でGaugeNamesがList&lt;string&gt;からList&lt;GaugeNameDef&gt;へ変更された際、
/// ProjectSerializerのSchemaVersionが据え置きのままだったため、その間(schemaVersion=3のまま
/// GaugeNamesがまだ文字列配列だった時期)に保存されたプロジェクトファイルが読み込めなくなっていた
/// (KeyAssignConverterと同じ「文字列1つなら簡易形」パターンを踏襲し、スキーマバージョンに関係なく
/// どちらの形式でも読めるようにする)。書き戻しは常に新形式(オブジェクト)で行う。</summary>
public sealed class GaugeNameDefConverter : JsonConverter<GaugeNameDef>
{
    /// <summary>2026-08-09不具合修正: 従来は「自分自身を除いたoptionsで再帰デシリアライズ/
    /// シリアライズする」方式(RawOptions)だったが、GaugeNameDefには
    /// [JsonConverter(typeof(GaugeNameDefConverter))]属性が型そのものに付与されているため、
    /// options.Convertersリストから自分を取り除いてもSystem.Text.Jsonの型解決は属性を見て
    /// 結局同じコンバータへ戻ってしまい、Read/Write双方が無限再帰(スタックオーバーフロー、
    /// try/catchで捕捉不可能な即死クラッシュ)に陥っていた(第三者報告: 曲名編集後の上書き保存で
    /// クラッシュ。実際はgaugeNamesを含む全プロジェクトの保存で発生する不具合で、曲名の内容とは
    /// 無関係だった)。JsonSerializerを一切経由せず、Utf8JsonReader/Utf8JsonWriterを直接操作して
    /// フィールドを手動で読み書きすることで、型解決による自己参照そのものを断つ。</summary>
    public override GaugeNameDef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new GaugeNameDef(reader.GetString()!);
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            string? displayName = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                string? propName = reader.GetString();
                reader.Read();
                if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
                    name = reader.GetString();
                else if (string.Equals(propName, "displayName", StringComparison.OrdinalIgnoreCase))
                    displayName = reader.GetString();
                else
                    reader.Skip();
            }
            return name is null
                ? throw new JsonException("gaugeNamesの要素にnameがありません")
                : new GaugeNameDef(name, displayName);
        }
        throw new JsonException("gaugeNamesの要素は文字列またはオブジェクトで指定してください");
    }

    public override void Write(Utf8JsonWriter writer, GaugeNameDef value, JsonSerializerOptions options)
    {
        string nameProp = options.PropertyNamingPolicy?.ConvertName(nameof(GaugeNameDef.Name)) ?? nameof(GaugeNameDef.Name);
        string displayNameProp = options.PropertyNamingPolicy?.ConvertName(nameof(GaugeNameDef.DisplayName)) ?? nameof(GaugeNameDef.DisplayName);

        writer.WriteStartObject();
        writer.WriteString(nameProp, value.Name);
        if (value.DisplayName is not null)
            writer.WriteString(displayNameProp, value.DisplayName);
        writer.WriteEndObject();
    }
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
