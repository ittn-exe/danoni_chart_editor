using System.Linq;
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

    /// <summary>強調グリッドの対象からフリーズアロー終点を除外するか(2026-07-26要望対応)。
    /// 既定OFF(従来通り始点・終点の両方に描画)。環境設定「表示」カテゴリからのみ変更可能。</summary>
    public bool ExcludeFreezeEndFromHighlight { get; set; } = false;

    /// <summary>強調グリッドの色をHighlightLineColorHexの固定色ではなく、そのノート自身の色
    /// (setColor/ncolor_data由来、レーンのブラシと同じ色)にするか(2026-08-02要望対応)。
    /// 既定OFF(従来通りHighlightLineColorHexの単色)。環境設定「表示」カテゴリからのみ変更可能。</summary>
    public bool UseNoteColorForHighlight { get; set; } = false;

    /// <summary>目視テスト中の再生位置ライン追従方式(2026-07-17f、未解決事項§2-1)。
    /// "page"=(A)ページ送り: ラインが画面外へ出た瞬間に次の1画面分へ切り替える。
    /// "smooth"=(B)スムーズスクロール: ラインを画面上の固定位置に据えて譜面側を流す。</summary>
    public string VisualTestFollowMode { get; set; } = "page";

    /// <summary>キーボードモード目視テスト中の「ノート配置受付」(2026-07-26要望対応)。既定OFF。
    /// ONの間は目視テスト中のノート入力キー配置をプレイテスト用(KeyAssign)に切り替え、キー押下時点の
    /// 再生位置(スナップ後)へノートをトグル配置する(通常の編集操作としてUndo履歴に積む)。</summary>
    public bool VisualTestAcceptNoteInput { get; set; } = false;

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

    /// <summary>レーン入替マクロ「選択範囲内のみ適用」の範囲マーカー・ハイライト帯の色
    /// (6桁カラーコード、2026-07-26要望対応)。始点/終点マーカーの線と、その間のハイライト帯を
    /// 同じ色で描画する(帯は半透明、線は不透明)。</summary>
    public string MacroRangeHighlightColorHex { get; set; } = "#FFA500";

    /// <summary>マクロ範囲マーカー(始点/終点)の線の太さ(px)。</summary>
    public double MacroRangeMarkerWidth { get; set; } = 2.0;

    // =====================================================================
    // タブリンク機能(2026-07-26要望対応): 同キー種タブ同士をリンクし、アクティブタブの背景に
    // 非アクティブタブ(リンク相手)のノートを薄く表示する(DAW的な参照表示、編集対象にはならない)。
    // =====================================================================

    /// <summary>背景ノートのサイズ比率(通常ノートサイズに対する倍率)。既定0.85(=-15%)。</summary>
    public double LinkedNoteSizeRatio { get; set; } = 0.85;

    /// <summary>背景ノートの色(6桁カラーコード)。非アクティブタブの実際の色設定は反映せず、この色で
    /// 統一して表示する(ユーザー確定仕様)。</summary>
    public string LinkedNoteColorHex { get; set; } = "#999999";

    /// <summary>背景ノートの強調表示(バー)の幅比率(レーン幅に対する倍率)。既定0.5(=50%)。</summary>
    public double LinkedHighlightWidthRatio { get; set; } = 0.5;

    /// <summary>背景ノートの強調表示(バー)の高さ(px)。既定2.0。</summary>
    public double LinkedHighlightHeight { get; set; } = 2.0;

    /// <summary>背景ノートの強調表示(バー)の色(6桁カラーコード)。</summary>
    public string LinkedHighlightColorHex { get; set; } = "#999999";

    /// <summary>譜面ビュー分割表示(2026-07-26要望対応、第三者提案)。ONの間、譜面ビューを左右に分割し、
    /// 同じ難易度タブを両方に表示する(独立スクロール、どちらでも編集可)。既定OFF、エディタ全体で
    /// 共通の設定(タブごとには持たない)。</summary>
    public bool SplitViewEnabled { get; set; } = false;

    /// <summary>プレイテスト: Reverse(スクロール反転)ON/OFF(2026-07-17g)。
    /// 難易度タブ切替時にPlaytestReverseByKeyTypeの値で自動上書きされる(2026-07-26)。</summary>
    public bool PlaytestReverse { get; set; } = false;

    /// <summary>プレイテスト: キー種ごとのReverse既定値(2026-07-26要望対応)。
    /// キー=keyTypeId、値=そのキー種で難易度タブを開いた際に自動適用するReverse初期値。
    /// エディタUIには編集欄を設けず、環境設定「テンプレート」カテゴリでのみ変更する
    /// (テンプレートフォルダの全キー種を一覧表示してチェックボックスで設定)。
    /// キーが存在しないキー種はfalse(通常)扱い。</summary>
    public Dictionary<string, bool> PlaytestReverseByKeyType { get; set; } = [];

    /// <summary>プレイテスト: キー種ごとの採用キーパターン番号(2026-07-26e要望対応)。
    /// キー=keyTypeId、値=そのキー種のプレイテストで使うパターン番号(0=既定パターン)。
    /// キーが存在しない、またはテンプレート側の追加パターン数を超える値の場合は0(既定)扱い。
    /// パターンが1つしかない(追加パターンが無い)キー種は環境設定に選択欄を出さない。</summary>
    public Dictionary<string, int> PlaytestPatternByKeyType { get; set; } = [];

    /// <summary>プレイテスト: ハイスピード倍率(px/frame換算、本家のx1=1px/frame相当)。</summary>
    public double PlaytestHiSpeed { get; set; } = 2.0;

    /// <summary>プレイテスト: タイミング調整オフセット(frame、正の値で譜面を後ろへずらす。仕様書12.2)。</summary>
    public double PlaytestOffsetFrames { get; set; } = 0.0;

    /// <summary>プレイテスト: ウィンドウサイズ倍率(x0.5〜3.0、2026-07-17h)。
    /// キー種別の基準サイズ(本家autoSpread準拠の横幅×高さ)に掛けて実ウィンドウサイズとする。</summary>
    public double PlaytestWindowScale { get; set; } = 1.0;

    /// <summary>プレイテスト: ウィンドウ幅の指定方式(2026-07-26要望対応、2026-07-26 "auto"追加)。dos.txtの
    /// playingWidthヘッダーが明示されている場合は常にそちらが最優先(仕様書12.2)。ヘッダー未指定時の
    /// フォールバック値をどう決めるかがこの設定で、"px"=PlaytestWindowWidthPxを直接使う、
    /// "keyType"=PlaytestWindowWidthKeyTypeで指定したキー種の幅(本家autoSpread準拠)を常に使う
    /// (実際に開いている難易度タブのキー種に関わらず一定にしたい場合用)。環境設定「プレイテスト」
    /// カテゴリでのみ変更可能。</summary>
    public string PlaytestWindowWidthMode { get; set; } = "keyType";

    /// <summary>プレイテスト: ウィンドウ幅を直接指定する場合のpx値
    /// (PlaytestWindowWidthMode="px"時のみ使用)。</summary>
    public double PlaytestWindowWidthPx { get; set; } = 600;

    /// <summary>プレイテスト: 「常にこのキー種の幅を使う」場合の基準キー種ID
    /// (PlaytestWindowWidthMode="keyType"時のみ使用)。既定は5keyの幅(=600px、AutoSpreadWidth準拠)で
    /// 従来の既定挙動と同じ結果になる。</summary>
    public string PlaytestWindowWidthKeyType { get; set; } = "5";

    /// <summary>プレイテスト: オートプレイON/OFF(2026-07-20)。ONの場合、全ノートを±0Fジャストで
    /// 自動的に拾う(手動キー入力は終了キー以外無効)。</summary>
    public bool PlaytestAutoPlay { get; set; } = false;

    /// <summary>ノート音ON/OFF(既定OFF)。ONの場合、目視テスト・プレイテストの両方で、
    /// ノート(通常+フリーズ始点)が存在するframeを通過するたびに、環境設定「テスト再生 > 全般」で
    /// 選択した./sounds内の音声ファイルを鳴らす。上部パネルのチェックボックスで切り替える。</summary>
    public bool HandClapEnabled { get; set; } = false;

    /// <summary>ノート音の再生音量(0.0〜1.0、既定1.0)。上部パネルの音量欄(%)で調整する。</summary>
    public double HandClapVolume { get; set; } = 1.0;

    /// <summary>ノート音として使用する音声ファイル名(./sounds内、拡張子含む)。環境設定
    /// 「テスト再生 > 全般」の一覧から選択する。既定は同梱のclap.wav。</summary>
    public string NoteSoundFileName { get; set; } = "clap.wav";

    /// <summary>プレイテスト: 中断キーとしてDeleteキーを使用するか(2026-07-20)。
    /// Delete/Escapeのうち、checkedのものだけが中断キーとして機能する。
    /// (2026-07-26d: BackSpaceは「再生開始フレームからやり直し」専用キーへ変更したため、
    /// 中断キーの選択肢からは除外した)</summary>
    public bool PlaytestQuitKeyDelete { get; set; } = true;

    /// <summary>プレイテスト: 中断キーとしてEscapeキーを使用するか(2026-07-20)</summary>
    public bool PlaytestQuitKeyEscape { get; set; } = true;

    /// <summary>プレイテスト起動時のウェイト(ms、2026-07-26d要望対応、既定0)。
    /// プレイテストウィンドウ表示後、この時間だけ待ってから音楽再生・判定を開始する。</summary>
    public int PlaytestStartupWaitMs { get; set; } = 0;

    /// <summary>プレイテスト中に小節線・小節番号を表示するか(2026-07-27要望対応、既定OFF)。
    /// 譜面ビューの小節線描画とは独立した設定。</summary>
    public bool PlaytestShowMeasureLines { get; set; } = false;

    /// <summary>プレイ画面プレビュー(右パネル「プレビュー」タブ)の「ノートの表示期限」設定
    /// (2026-07-29要望対応)。"passThrough"(既定)=ステップゾーンを通過するまで(frame &lt; 再生開始ライン
    /// で非表示)、"overlap"=ステップゾーンに重なるまで(frame &lt;= 再生開始ラインで非表示、
    /// frame=再生開始ラインでも消す)。</summary>
    public string PreviewNoteExpiryMode { get; set; } = "passThrough";

    /// <summary>プレイ画面プレビュー(右パネル「プレビュー」タブ)の表示サイズ倍率(2026-07-29要望対応)。
    /// 0.25〜2.0(25%〜200%)。論理座標(ノート配置等)には影響せず、表示のみ拡縮する
    /// (PlaytestWindowのウィンドウサイズ倍率と同じ考え方)。</summary>
    public double PreviewDisplayScale { get; set; } = 1.0;

    /// <summary>譜面ビュー(編集画面)のReverse表示(2026-07-22)。ONの場合、tick0を画面下端・末尾を
    /// 上端にして進行方向を逆にする(画像等は上下反転しない、座標変換のみを反転する仕様)。
    /// プレイテスト(PlaytestReverse)とは完全に独立した設定。環境設定からのみ切替可能
    /// (ボタン・チェックボックス・ショートカットキーは用意しない、2026-07-22ユーザー確定仕様)。</summary>
    public bool ChartViewReverse { get; set; } = false;

    // =====================================================================
    // 目視テスト: 自動でスタート位置(再生開始ライン)へ戻る機能(2026-07-29要望対応、既定OFF)。
    // 再生開始ラインから指定した小節数/秒数が経過すると、自動的に再生開始ラインの位置へ戻る
    // (DAWループ再生(時間情報レーンの範囲選択)とは別の、より単純な「練習用ループ」機能)。
    // =====================================================================

    /// <summary>自動でスタート位置へ戻る機能を有効にするか(既定OFF)。</summary>
    public bool VisualTestAutoReturnEnabled { get; set; } = false;

    /// <summary>経過判定の単位。"measures"(既定)=小節数、"seconds"=秒数。</summary>
    public string VisualTestAutoReturnUnit { get; set; } = "measures";

    /// <summary>自動で戻るまでの小節数(VisualTestAutoReturnUnit="measures"時のみ使用、既定4)。</summary>
    public int VisualTestAutoReturnMeasures { get; set; } = 4;

    /// <summary>自動で戻るまでの秒数(VisualTestAutoReturnUnit="seconds"時のみ使用、既定10.0)。</summary>
    public double VisualTestAutoReturnSeconds { get; set; } = 10.0;

    /// <summary>戻った後、再生を継続するか(true=既定、そのまま先頭からループ再生を続ける)、
    /// それとも目視テストを終了するか(false)。</summary>
    public bool VisualTestAutoReturnContinuePlayback { get; set; } = true;

    /// <summary>キーボードモード中のSpace/Bキーの移動方向の解釈方式(2026-07-26要望対応)。
    /// - "visual"(既定、現在の実装通り): 画面上の見た目方向に固定(Space=常に画面下へ、B=常に画面上へ。
    ///   ChartViewReverse中でもこの見た目基準は変わらない)。
    /// - "time": 時間(tick)方向に固定(Space=常に前進、B=常に後退)。通常表示では"visual"と同じ結果になるが、
    ///   ChartViewReverse中は画面上の方向が逆転する(前進が画面上方向になる)。
    /// ↑/↓キーの解釈(見た目方向固定)には影響しない。</summary>
    public string KeyboardModeSpaceBMode { get; set; } = "visual";

    /// <summary>キーボードモード中の←/→キー(および対応するCtrl+←/→の2小節移動、Shift+Ctrl+←/→の
    /// 4小節移動)の移動方向の解釈方式(2026-07-26要望対応、KeyboardModeSpaceBModeと同じ考え方)。
    /// - "visual"(既定、現在の実装通り): 画面上の見た目方向に固定(←=常に画面上へ、→=常に画面下へ。
    ///   ChartViewReverse中でもこの見た目基準は変わらない)。
    /// - "time": 時間(tick)方向に固定(←=常に後退、→=常に前進)。通常表示では"visual"と同じ結果になるが、
    ///   ChartViewReverse中は画面上の方向が逆転する。</summary>
    public string KeyboardModeLeftRightMode { get; set; } = "visual";

    /// <summary>レーンラベル欄へのノート数リアルタイム表示(2026-07-26、要望対応)。既定OFF。
    /// マウスモード中はラベルの次の行に表示し、キーボードモード中(既に2行使用中)は
    /// 1行目(実キー表示)をノート数表示に置き換える(ChartCanvas.DrawLaneLabels参照)。</summary>
    public bool ShowLaneNoteCount { get; set; } = false;

    /// <summary>レーンラベル欄の1行目表示切替(2026-08-02要望対応)。既定false=キー表示
    /// (KeyAssignLabel、例"S"、"E/R")。true=レーン名表示(LaneDef.LaneId、例"left"、"sleft")。
    /// 同じレーンに複数キーをアサインした際、キー表示だと文字が長くなり隣接レーンと重なって
    /// 読みづらいとの要望対応(ChartCanvas.DrawLaneLabels参照)。</summary>
    public bool ShowLaneNameLabel { get; set; } = false;

    /// <summary>再生速度(目視テスト・プレイテスト共通、2026-07-23)。0.1〜2.0、0.1刻み。
    /// MediaPlayer.SpeedRatioへそのまま渡す(ピッチ補正は行わない)。</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>音楽再生時の音量(2026-07-26)。0.0〜1.0(MediaPlayer.Volumeへそのまま渡す)。
    /// 上部パネルのスライダー+数値入力欄(0〜100%表示)で変更する。</summary>
    public double PlaybackVolume { get; set; } = 1.0;

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
    // 自動保存・クラッシュ復旧(2026-07-25、TBD)。B案(復旧用スロット、通常保存とは別領域)。
    // 既定OFF。ONの間、変更のあるプロジェクトタブだけを一定間隔で./autosave配下へ書き込む。
    // 次回起動時にクラッシュを検知した場合のみ復旧ダイアログを出す(AutoSaveManager参照)。
    // =====================================================================

    /// <summary>自動保存を有効にするか(既定OFF)。</summary>
    public bool AutoSaveEnabled { get; set; } = false;

    /// <summary>自動保存の間隔(分)。AutoSaveEnabled=true時のみ使用(既定5分)。</summary>
    public double AutoSaveIntervalMinutes { get; set; } = 5.0;

    // =====================================================================
    // 最近開いたファイル(2026-07-26、ファイル>最近開いたファイル)
    // =====================================================================

    /// <summary>最近開いた(自形式)プロジェクトファイルの絶対パス一覧(新しい順)。
    /// RecentFilesLimit件を超える分はAddRecentFile側で切り詰める。</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>「最近開いたファイル」の保持件数上限(既定10件、環境設定で変更可)。</summary>
    public int RecentFilesLimit { get; set; } = 10;

    /// <summary>最近開いたファイル一覧の先頭へpathを追加する(既存の同一パスは重複排除して先頭へ移動、
    /// 大文字小文字を区別しないパス比較。フルパス化してから比較・格納する)。RecentFilesLimitを
    /// 超えた分は切り詰める。呼び出し元でSave()すること。</summary>
    public void AddRecentFile(string path)
    {
        var full = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, full);
        int limit = Math.Max(1, RecentFilesLimit);
        if (RecentFiles.Count > limit) RecentFiles.RemoveRange(limit, RecentFiles.Count - limit);
    }

    /// <summary>存在しなくなったパスを一覧から取り除く(メニュー表示直前の整理用)。変化があればtrue。</summary>
    public bool PruneMissingRecentFiles()
    {
        int before = RecentFiles.Count;
        RecentFiles.RemoveAll(p => !File.Exists(p));
        return RecentFiles.Count != before;
    }

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
    // 譜面ビューのレーン文字サイズ(2026-07-26要望対応)。時間情報レーン(小節番号/frame/time)と
    // マーカーレーンのタグ文字それぞれの基準フォントサイズ(pt、ZoomScale=1.0時)。
    // 実描画時はChartCanvas側でZoomScaleを掛けて最終サイズを求める(既定値は変更前の固定値8/9を踏襲)。
    // =====================================================================

    /// <summary>時間情報レーン(小節番号/frame/time)の基準フォントサイズ</summary>
    public double TimeInfoFontSize { get; set; } = 8.0;

    /// <summary>マーカーレーンのタグ文字の基準フォントサイズ</summary>
    public double MarkerFontSize { get; set; } = 9.0;

    // =====================================================================
    // musicURLからの楽曲取得(2026-07-26確定仕様)。指定フォルダをカレントディレクトリとして扱い、
    // その中からProject.MusicUrlで指定されたファイル名の楽曲を読み込めるようにする機能。
    // 既定OFF(意図しない自動読込・意図しないフォルダ露出を避けるため)。
    // =====================================================================

    /// <summary>musicURLからの楽曲自動取得機能をONにするか(既定false)</summary>
    public bool MusicUrlAutoLoadEnabled { get; set; } = false;

    /// <summary>musicURL取得のカレントディレクトリとして扱うフォルダ(絶対パス)。
    /// MusicUrlAutoLoadEnabled=trueの間のみ使用する。</summary>
    public string MusicUrlBaseFolder { get; set; } = "";

    // =====================================================================
    // コピーマネージャー(異なるキー種間のコピー&ペースト、2026-07-31)。
    // コピー元タブとペースト先タブのキー種が異なる場合、CopyManagerWindowでレーン対応を指定して
    // 貼り付ける。その際に発生しうる6種類のオブジェクト衝突の解決方法をここで保持する
    // (CopyManagerWindow内の折り畳み「コピー・ペースト設定」から変更可能、値は即時保存)。
    // 値はいずれもラジオボタンの選択肢を表す文字列定数(enumではなく既存の他設定と同じ流儀)。
    // =====================================================================

    /// <summary>通常ノートとフリーズ先頭が同一tickで衝突した場合の優先。
    /// "note"=通常ノートを優先(フリーズ側を破棄)、"freeze"=フリーズを優先(通常ノート側を破棄)。</summary>
    public string CopyManagerNoteVsFreezeHeadMode { get; set; } = "note";

    /// <summary>通常ノートがフリーズの帯(始点・終点を除く区間内部)に衝突した場合の解決方法。
    /// "trim"=フリーズをそのノートの16分手前で切る、"ignoreNote"=そのノート側を無視する。</summary>
    public string CopyManagerNoteVsFreezeBodyMode { get; set; } = "trim";

    /// <summary>通常ノートがフリーズ終端と同一tickで衝突した場合の解決方法。
    /// "trim"=フリーズをそのノートの16分手前で切る、"ignoreNote"=そのノート側を無視する。</summary>
    public string CopyManagerNoteVsFreezeTailMode { get; set; } = "trim";

    /// <summary>2つのフリーズの先頭同士が同一tickで衝突した場合、どちらを残すか。
    /// "long"=長い方を残す、"short"=短い方を残す。</summary>
    public string CopyManagerFreezeHeadVsHeadMode { get; set; } = "long";

    /// <summary>フリーズの先頭が、別のフリーズの帯(始点・終点を除く区間内部)に衝突した場合の解決方法。
    /// "trim"=時間的に後ろのフリーズの16分手前で前側のフリーズを切る、"ignoreFreeze"=後ろのフリーズ側を無視する。</summary>
    public string CopyManagerFreezeHeadVsBodyMode { get; set; } = "trim";

    /// <summary>フリーズの先頭が、別のフリーズ終端と同一tickで衝突した場合の解決方法。
    /// "trim"=時間的に後ろのフリーズの16分手前で前側のフリーズを切る、"ignoreFreeze"=後ろのフリーズ側を無視する。</summary>
    public string CopyManagerFreezeHeadVsTailMode { get; set; } = "trim";

    /// <summary>異なるキー種間の貼り付け時、色編集モードの色情報・コメント/警告注釈といった付随プロパティを
    /// 新しいレーンへ引き継ぐか。"carryOver"=引き継ぐ、"discard"=引き継がない。</summary>
    public string CopyManagerPreservePropertiesMode { get; set; } = "carryOver";

    // =====================================================================
    // 色履歴(仕様書6.4.2/14章 colorHistory、2026-07-19b)
    // ※履歴の記録・呼び出しUI(カラーピッカー連携)は今後の実装。上限と保存領域を先に用意する。
    // =====================================================================

    /// <summary>色コード使用履歴の上限件数(仕様書14章でデフォルト24件と確定)</summary>
    public int ColorHistoryLimit { get; set; } = 24;

    /// <summary>色コード使用履歴(新しい順)</summary>
    public List<string> ColorHistory { get; set; } = [];

    // 2026-07-26: レーン入替マクロ(仕様書11章)は settings.json ではなく独立した、
    // キー種ごとの s-macro_キー種.json(同じ./settingsフォルダ内)で管理する(2026-07-26g)。
    // LaneSwapMacroFile.LoadAll/SaveAll参照。

    // =====================================================================
    // 統計情報(2026-07-26、環境設定 > 統計情報で閲覧のみ可能)。
    // ITTNアナライザー/おにスターの隠し機能解禁条件(docs/progress_and_tbd_2026-07-25.md §2-2)にも
    // これらのうちStatObjectsPlaced/StatOniStarRecalcPressesを流用する。
    // プロジェクトを跨いだアプリ全体の累計のため、プロジェクトファイルではなくAppSettings側に持つ。
    // =====================================================================

    /// <summary>新規配置したオブジェクトの累計数(コピー/貼り付けによる複製含む)。</summary>
    public int StatObjectsPlaced { get; set; } = 0;

    /// <summary>削除したオブジェクトの累計数(通常削除・ドラッグ削除・切り取りに伴う削除を含む)。</summary>
    public int StatObjectsDeleted { get; set; } = 0;

    /// <summary>Ctrl+C(コピー)操作の累計回数。切り取り(Cut)はこちらには含めず別カウントする。</summary>
    public int StatObjectsCopied { get; set; } = 0;

    /// <summary>Ctrl+X(切り取り)操作の累計回数。</summary>
    public int StatObjectsCut { get; set; } = 0;

    /// <summary>Ctrl+V(貼り付け)操作の累計回数(貼り付けた個々のオブジェクト数はStatObjectsPlacedで別途カウント)。</summary>
    public int StatObjectsPasted { get; set; } = 0;

    /// <summary>「おにスター(推定star値)」の算出・再算出ボタンの累計押下回数。10回到達で
    /// 実際の推定値が表示されるようになる(それまではプレースホルダ表示)。</summary>
    public int StatOniStarRecalcPresses { get; set; } = 0;

    /// <summary>新規プロジェクト作成の累計回数。</summary>
    public int StatNewProjectCount { get; set; } = 0;

    /// <summary>プロジェクト保存(手動保存)の累計回数。</summary>
    public int StatProjectSaveCount { get; set; } = 0;

    /// <summary>dos.txtエクスポートの累計回数。</summary>
    public int StatDosExportCount { get; set; } = 0;

    /// <summary>Undo/Redo実行の累計回数(両方合計、2026-07-26追加)。</summary>
    public int StatUndoRedoCount { get; set; } = 0;

    /// <summary>プレイテスト起動の累計回数。</summary>
    public int StatPlaytestLaunchCount { get; set; } = 0;

    /// <summary>プレイテスト中、オートプレイではない手動プレイ中に打鍵によって消えた
    /// (判定されて画面から消えた)ノートの累計数。Uwan/Iknai(判定枠内で押されずタイムアウトした
    /// もの)は「打鍵による」ではないため含めない。</summary>
    public int StatPlaytestNotesCleared { get; set; } = 0;

    /// <summary>レーン入替マクロの実行(右パネル「実行」ボタン)の累計回数。</summary>
    public int StatMacroRunCount { get; set; } = 0;

    /// <summary>アプリ起動の累計回数。</summary>
    public int StatAppLaunchCount { get; set; } = 0;

    /// <summary>クラッシュ検出(前回起動時に正常終了フラグが消えていなかった)の累計回数。</summary>
    public int StatCrashCount { get; set; } = 0;

    // =====================================================================
    // 終了時のウィンドウ状態(2026-07-26要望対応)。「どの画面のどの位置に」「最大化かどうか」を保存し、
    // 次回起動時に復元する。null/未設定=従来通りOS既定の位置。位置(WindowLeft/Top)は仮想スクリーン
    // 座標(マルチモニタ環境ではモニタをまたいだ通し座標)のため、これ自体が「何番の画面か」の情報を
    // 兼ねる(モニタ構成が変わって画面外になった場合はMainWindow側で既定位置へフォールバックする)。
    // =====================================================================

    /// <summary>終了時に最大化状態だったか。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>終了時の非最大化状態でのウィンドウ位置・サイズ(仮想スクリーン座標、px)。
    /// 最大化解除時に復元する基準としても使う(WPFのRestoreBounds相当)。</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>右パネル(譜面ビューとの境界のGridSplitterでドラッグ調整する側)の幅(px、2026-07-29要望対応)。
    /// null=未保存(初回起動等)、XAML既定値(280px)のまま。</summary>
    public double? RightPanelWidth { get; set; }

    /// <summary>環境設定ウィンドウのサイズ(px、2026-07-29要望対応)。OK/キャンセルどちらで閉じても
    /// 保存する(MainWindow本体の位置保存と同じ考え方)。null=未保存(初回起動等)、既定値(560x470)のまま。</summary>
    public double? PreferencesWindowWidth { get; set; }
    public double? PreferencesWindowHeight { get; set; }

    /// <summary>プレイテストウィンドウの表示位置(2026-07-26要望対応、第三者提案)。閉じるボタン・
    /// 中断キーでの終了時の位置を保存し、次回プレイテスト表示時に引き継ぐ。SizeToContentのため
    /// 幅・高さは保存しない(内容によって毎回変わるため)。null=未設定(既定通り親ウィンドウ中央に表示)。</summary>
    public double? PlaytestWindowLeft { get; set; }
    public double? PlaytestWindowTop { get; set; }

    // =====================================================================
    // キーマクロ(2026-07-26要望対応、第三者要望): Ctrl+Shift+1〜9に割り当てる、複数の機能を
    // 順番に実行するマクロ。既存の「レーン入替マクロ」(仕様書11章)とは別機能(KeyMacro.cs参照)。
    // =====================================================================

    /// <summary>設定済みのキーマクロ一覧(スロット1〜9、未設定のスロットはリストに存在しない)。</summary>
    public List<KeyMacroDefinition> KeyMacros { get; set; } = [];

    // =====================================================================
    // ショートカットキーカスタマイズ(2026-07-27要望対応)。マウスモード側のグローバルショートカット
    // (ShortcutSettings.ShortcutId参照)のユーザー定義割り当て。キーはShortcutIdのenum名文字列。
    // 未設定のShortcutIdはShortcutDefaults.Allの既定値へフォールバックする(GetShortcut参照)。
    // =====================================================================

    /// <summary>ショートカットキーのユーザー定義割り当て。</summary>
    public Dictionary<string, ShortcutBinding> Shortcuts { get; set; } = [];

    /// <summary>指定ShortcutIdの現在の割り当てを取得する(ユーザー定義優先、無ければ既定値)。</summary>
    public ShortcutBinding GetShortcut(ShortcutId id) =>
        Shortcuts.TryGetValue(id.ToString(), out var b) ? b : ShortcutDefaults.All[id].Default;

    /// <summary>指定ShortcutIdへユーザー定義の割り当てを設定する。</summary>
    public void SetShortcut(ShortcutId id, ShortcutBinding binding) => Shortcuts[id.ToString()] = binding;

    /// <summary>指定ShortcutIdのユーザー定義割り当てを取り除き、既定値へ戻す。</summary>
    public void ResetShortcutToDefault(ShortcutId id) => Shortcuts.Remove(id.ToString());

    /// <summary>全ShortcutIdのユーザー定義割り当てを取り除き、既定値へ戻す(環境設定「デフォルト値へのリセット」ボタン用)。</summary>
    public void ResetAllShortcutsToDefault() => Shortcuts.Clear();

    // =====================================================================
    // キーボードモード専用ショートカットキーカスタマイズ(2026-07-29要望対応)。マウスモードのShortcuts
    // とは完全に別の辞書で管理し、同じ物理キーが重複して割り当てられることを許容する(KeyboardModeShortcutId
    // 参照)。値はKey名文字列のみ(修飾キーは扱わない)。
    // =====================================================================

    /// <summary>キーボードモード専用ショートカットキーのユーザー定義割り当て(キー=KeyboardModeShortcutIdの
    /// enum名文字列、値=System.Windows.Input.KeyのKey名文字列)。</summary>
    public Dictionary<string, string> KeyboardModeShortcuts { get; set; } = [];

    /// <summary>指定KeyboardModeShortcutIdの現在の割り当て(Key名文字列)を取得する
    /// (ユーザー定義優先、無ければ既定値)。</summary>
    public string GetKeyboardModeShortcutKey(KeyboardModeShortcutId id) =>
        KeyboardModeShortcuts.TryGetValue(id.ToString(), out var k) ? k : KeyboardModeShortcutDefaults.All[id].DefaultKey;

    public void SetKeyboardModeShortcutKey(KeyboardModeShortcutId id, string key) => KeyboardModeShortcuts[id.ToString()] = key;

    public void ResetKeyboardModeShortcutToDefault(KeyboardModeShortcutId id) => KeyboardModeShortcuts.Remove(id.ToString());

    public void ResetAllKeyboardModeShortcutsToDefault() => KeyboardModeShortcuts.Clear();

    /// <summary>環境設定ウィンドウの作業コピー用(2026-07-19)。ColorHistory/RecentFilesは参照型のため個別に複製する</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.ColorHistory = [.. ColorHistory];
        c.RecentFiles = [.. RecentFiles];
        c.KeyMacros = [.. KeyMacros.Select(m => new KeyMacroDefinition
        {
            Slot = m.Slot,
            Steps = [.. m.Steps.Select(s => new KeyMacroStep { Kind = s.Kind, Value = s.Value })],
        })];
        c.Shortcuts = Shortcuts.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        c.KeyboardModeShortcuts = new Dictionary<string, string>(KeyboardModeShortcuts);
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
