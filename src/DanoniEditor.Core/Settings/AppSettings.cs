using System.Text.Json;

namespace DanoniEditor.Core.Settings;

/// <summary>
/// アプリケーション本体の環境設定(仕様書14章)。プロジェクト(ChartProject)とは別に、
/// exeと同じフォルダの settings.json に永続化する(2026-07-16b: 実装着手時点で必要になった
/// 項目のみ収録。14章の headerDefaults/colorHistory/macros/undoHistorySize は
/// 「TBD: 今後追加される項目」のまま未実装 — 追加する際もこのクラスに項目を足していく想定)。
/// </summary>
public sealed class AppSettings
{
    /// <summary>ノートレーンにノート画像を表示するか。OFFの場合は強調グリッドのみで表示する
    /// (2026-07-16b: 「作業エリア全体のズーム」導入後、画像だけだとレーン上の位置がわかりにくい
    /// という指摘から追加)。</summary>
    public bool ShowNoteImages { get; set; } = true;

    /// <summary>ノートレーンに強調グリッド(グリッド位置を示す横棒)を表示するか。
    /// ShowNoteImagesとは独立にON/OFFできる(2026-07-16j: 「ノート画像ON時にも強調表示を併用したい」
    /// という要望により、旧来の「ShowNoteImages=falseの時だけ強調表示」という排他仕様から変更。
    /// ただしShowNoteImagesとShowHighlightGridの両方が同時にOFFにならないよう、UI側
    /// (MainWindow)で常にどちらか一方はONを維持する制約を課している)。</summary>
    public bool ShowHighlightGrid { get; set; } = false;

    /// <summary>ノート強調グリッドの太さ(px)。ノート/フリーズ端点のグリッド位置に描く横棒の高さ。</summary>
    public double HighlightLineWidth { get; set; } = 3.0;

    /// <summary>ノート強調グリッドの色(6桁カラーコード、仕様書6.4.2のカラーコード入力方式に準拠)。</summary>
    public string HighlightLineColorHex { get; set; } = "#FFD400";

    /// <summary>目視テスト中の再生位置ライン追従方式(2026-07-17f、未解決事項§2-1)。
    /// "page"=(A)ページ送り: ラインが画面外へ出た瞬間に次の1画面分へ切り替える。
    /// "smooth"=(B)スムーズスクロール: ラインを画面上の固定位置に据えて譜面側を流す。</summary>
    public string VisualTestFollowMode { get; set; } = "page";

    /// <summary>再生開始フレーム可視化ラインの太さ(px)(2026-07-17f、未解決事項§2-2)。</summary>
    public double PlaybackStartLineWidth { get; set; } = 2.0;

    /// <summary>再生開始フレーム可視化ラインの色(6桁カラーコード)。</summary>
    public string PlaybackStartLineColorHex { get; set; } = "#4FC3F7";

    /// <summary>プレイテスト: Reverse(スクロール反転)ON/OFF(2026-07-17g)。</summary>
    public bool PlaytestReverse { get; set; } = false;

    /// <summary>プレイテスト: ハイスピード倍率(px/frame換算、本家のx1=1px/frame相当)。</summary>
    public double PlaytestHiSpeed { get; set; } = 2.0;

    /// <summary>プレイテスト: タイミング調整オフセット(frame、正の値で譜面を後ろへずらす。仕様書12.2)。</summary>
    public double PlaytestOffsetFrames { get; set; } = 0.0;

    /// <summary>プレイテスト: ウィンドウサイズ倍率(x0.5〜3.0、2026-07-17h)。
    /// キー種別の基準サイズ(本家autoSpread準拠の横幅×高さ)に掛けて実ウィンドウサイズとする。</summary>
    public double PlaytestWindowScale { get; set; } = 1.0;

    // =====================================================================
    // 新規プロジェクトのheaderデフォルト(仕様書6.4.1/14章 headerDefaults、2026-07-19b)。
    // 「個人の制作スタイルで固定したい」項目のデフォルト上書き。musicURLは対象外(常に都度入力)。
    // =====================================================================

    /// <summary>新規プロジェクトのstartFrame初期値</summary>
    public int DefaultStartFrame { get; set; } = 0;

    /// <summary>新規プロジェクトのblankFrame初期値(個人運用では200等を想定、仕様書6.4.1)</summary>
    public int DefaultBlankFrame { get; set; } = 0;

    /// <summary>新規プロジェクトのtuning初期値</summary>
    public string DefaultTuning { get; set; } = "name";

    /// <summary>新規プロジェクトのfrzAttempt初期値</summary>
    public int DefaultFrzAttempt { get; set; } = 5;

    /// <summary>新規プロジェクト作成ダイアログのBPM初期値。dos.txt単体インポートで
    /// タイミング復元不能時の仮定BPMにも使う(仕様書15.2)</summary>
    public double DefaultBpm { get; set; } = 120;

    // =====================================================================
    // 編集・保存(仕様書13章/14章、未解決事項§2-6、2026-07-19b)
    // =====================================================================

    /// <summary>Undo履歴の保持件数(仕様書14章 undoHistorySize、デフォルト30)</summary>
    public int UndoHistorySize { get; set; } = 30;

    /// <summary>未保存の変更があるままエディタを閉じる時に確認ダイアログを出すか(未解決事項§2-6)</summary>
    public bool ConfirmUnsavedOnClose { get; set; } = true;

    // =====================================================================
    // マーカー表示(仕様書7.4: コメントの全文表示/先頭数文字のみ表示、2026-07-19b)
    // =====================================================================

    /// <summary>マーカーコメントを全文表示するか(false=先頭数文字のみ)</summary>
    public bool MarkerCommentFull { get; set; } = true;

    /// <summary>先頭数文字表示の際の文字数</summary>
    public int MarkerCommentHeadChars { get; set; } = 4;

    // =====================================================================
    // 色履歴(仕様書6.4.2/14章 colorHistory、2026-07-19b)
    // ※履歴の記録・呼び出しUI(カラーピッカー連携)は今後の実装。上限と保存領域を先に用意する。
    // =====================================================================

    /// <summary>色コード使用履歴の上限件数(仕様書14章でデフォルト24件と確定)</summary>
    public int ColorHistoryLimit { get; set; } = 24;

    /// <summary>色コード使用履歴(新しい順)</summary>
    public List<string> ColorHistory { get; set; } = [];

    /// <summary>環境設定ウィンドウの作業コピー用(2026-07-19)。ColorHistoryのみ参照型のため個別に複製する</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.ColorHistory = [.. ColorHistory];
        return c;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(text, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // 壊れた/旧形式のsettings.jsonは既定値にフォールバック(アプリ起動を止めない)
            return new AppSettings();
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
}
