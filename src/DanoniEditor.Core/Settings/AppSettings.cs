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

    // =====================================================================
    // カーソルライン(マウスモード、2026-07-25): ホバー中の最寄りスナップ位置を示す横線と、
    // カーソルが乗っているレーンを強調する帯。それぞれ独立に太さ・色を変更できる。
    // =====================================================================

    /// <summary>カーソルライン(全レーン共通、細い横線)の太さ(px)。</summary>
    public double CursorLineWidth { get; set; } = 1.0;

    /// <summary>カーソルラインの色(6桁カラーコード)。描画時は薄く重なるよう固定の半透明度を
    /// 追加で適用する(他のライン設定同様、アルファ自体は設定項目に含めない)。</summary>
    public string CursorLineColorHex { get; set; } = "#FFFFFF";

    /// <summary>カーソルが乗っているレーンを強調する帯の太さ(px)。</summary>
    public double CursorHighlightWidth { get; set; } = 8.0;

    /// <summary>カーソル強調帯の色(6桁カラーコード)。</summary>
    public string CursorHighlightColorHex { get; set; } = "#00E5FF";

    /// <summary>プレイテスト: Reverse(スクロール反転)ON/OFF(2026-07-17g)。</summary>
    public bool PlaytestReverse { get; set; } = false;

    /// <summary>プレイテスト: ハイスピード倍率(px/frame換算、本家のx1=1px/frame相当)。</summary>
    public double PlaytestHiSpeed { get; set; } = 2.0;

    /// <summary>プレイテスト: タイミング調整オフセット(frame、正の値で譜面を後ろへずらす。仕様書12.2)。</summary>
    public double PlaytestOffsetFrames { get; set; } = 0.0;

    /// <summary>プレイテスト: ウィンドウサイズ倍率(x0.5〜3.0、2026-07-17h)。
    /// キー種別の基準サイズ(本家autoSpread準拠の横幅×高さ)に掛けて実ウィンドウサイズとする。</summary>
    public double PlaytestWindowScale { get; set; } = 1.0;

    /// <summary>プレイテスト: オートプレイON/OFF(2026-07-20)。ONの場合、全ノートを±0Fジャストで
    /// 自動的に拾う(手動キー入力は終了キー以外無効)。</summary>
    public bool PlaytestAutoPlay { get; set; } = false;

    /// <summary>プレイテスト: 中断キーとしてDeleteキーを使用するか(2026-07-20)。
    /// Delete/BackSpace/Escapeのうち、checkedのものだけが中断キーとして機能する。</summary>
    public bool PlaytestQuitKeyDelete { get; set; } = true;

    /// <summary>プレイテスト: 中断キーとしてBackSpaceキーを使用するか(2026-07-20)</summary>
    public bool PlaytestQuitKeyBackSpace { get; set; } = true;

    /// <summary>プレイテスト: 中断キーとしてEscapeキーを使用するか(2026-07-20)</summary>
    public bool PlaytestQuitKeyEscape { get; set; } = true;

    /// <summary>譜面ビュー(編集画面)のReverse表示(2026-07-22)。ONの場合、tick0を画面下端・末尾を
    /// 上端にして進行方向を逆にする(画像等は上下反転しない、座標変換のみを反転する仕様)。
    /// プレイテスト(PlaytestReverse)とは完全に独立した設定。環境設定からのみ切替可能
    /// (ボタン・チェックボックス・ショートカットキーは用意しない、2026-07-22ユーザー確定仕様)。</summary>
    public bool ChartViewReverse { get; set; } = false;

    /// <summary>再生速度(目視テスト・プレイテスト共通、2026-07-23)。0.1〜2.0、0.1刻み。
    /// MediaPlayer.SpeedRatioへそのまま渡す(ピッチ補正は行わない)。</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

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
    // SKB操作モード(キーボード操作、2026-07-21確定仕様)
    // =====================================================================

    /// <summary>同時押しとみなす時間閾値(ms)。この時間以内の連続キー入力は同一カーソル位置への
    /// 入力として扱われ、カーソルを進めない(SKBエディタの実装に準拠、既定30ms)。</summary>
    public double SimultaneousPressThresholdMs { get; set; } = 30;

    /// <summary>Ctrl+1〜9,0,-,^によるグリッド分解能切替のキー割り当てプリセット(2026-07-26)。
    /// "original"=分解能の単純な昇順(このエディタ独自)、"skbExtended"=SKBエディタのCtrl+1〜7割り当てを
    /// 踏襲した拡張セット(<see cref="GridShortcutPresets"/>参照)。エディタ本体にUIは設けず、
    /// 環境設定からのみ変更できる。</summary>
    public string GridShortcutPreset { get; set; } = GridShortcutPresets.Original;

    // --- Shift+Ctrl+A(全選択)の対象種別(2026-07-21、TBD項目「shift+ctrl+a targets selectable in preferences」) ---
    // Ctrl+A(修飾無し)は常にノート・フリーズのみを対象とする(仕様固定)。Shift+Ctrl+Aはここで
    // ON にした種別すべてを対象に全選択する。

    public bool SelectAllTargetNote { get; set; } = true;
    public bool SelectAllTargetFreeze { get; set; } = true;
    public bool SelectAllTargetSpeed { get; set; } = true;
    public bool SelectAllTargetBoost { get; set; } = true;
    public bool SelectAllTargetBpm { get; set; } = true;
    public bool SelectAllTargetTimeSignature { get; set; } = true;
    public bool SelectAllTargetMarker { get; set; } = true;

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

    public void Save(string path)
    {
        // 2026-07-20: settings.jsonが./settingsサブディレクトリへ移動したため、初回はフォルダが無い
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
