using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using DanoniEditor.Core.Analysis;
using DanoniEditor.Core.Export;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Settings;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;
using Microsoft.Win32;

namespace DanoniEditor.App;

public partial class MainWindow : Window
{
    private readonly TemplateRepository _templates;
    /// <summary>プラグイン対応の土台(2026-07-26)。読み込み・初期化・アクティブなドキュメントへの
    /// 追従をまとめて担う。コンストラクタで一度だけ生成し、_document切替のたびにNotifyDocumentChangedを
    /// 呼んで最新状態を追従させる。</summary>
    private readonly Plugins.PluginManager _pluginManager;
    private readonly List<LayoutAnchorable> _pluginPanelAnchorables = new(); // 2026-09-21: 表示メニューの並び順固定用(AvalonDock内部走査順に依存しないための独自追跡)
    private EditorDocument? _document;
    private SmartToolController? _controller;
    /// <summary>SKB操作モード(キーボード操作、2026-07-21)のコントローラ。ドキュメント切替のたびに
    /// OpenDocumentで作り直す(内部の同時押し判定/未完了フリーズ状態は一時的なものでよいため)。</summary>
    private KeyboardModeController? _keyboardMode;
    /// <summary>キーボードモードON/OFF(Ctrl+,または左パネルのトグルボタン、セッションを跨いで保持)</summary>
    private bool _keyboardModeActive;
    /// <summary>キーボードモード中、Shift+前進後退で範囲選択している間のアンカー位置(2026-07-26)。
    /// 選択中でなければnull。Shiftを離して移動すると選択がクリアされ、これもnullに戻る。</summary>
    private long? _keyboardSelectionAnchorTick;

    // =====================================================================
    // 譜面ビュー分割表示(2026-07-26要望対応、第三者提案)
    // =====================================================================

    /// <summary>分割表示ON/OFF(上パネルのトグルボタン、AppSettingsで永続化、エディタ全体で共通)</summary>
    private bool _splitViewEnabled;

    /// <summary>キーボードモード中のアクティブペイン(false=左/Canvas、true=右/Canvas2)。
    /// 分割OFF中・マウスモード中は参照されない(常に左=Canvasのみが対象)。Tabキーで切り替える。</summary>
    private bool _activePaneIsSecondary;

    /// <summary>アクティブペインの目印(枠線)色(固定色、2026-07-26要望対応。設定項目化はせず簡易な目印とする)。</summary>
    private static readonly Brush ActivePaneIndicatorBrush = Brushes.DeepSkyBlue;
    /// <summary>色編集モードON/OFF(左パネルのトグルボタン、セッションを跨いで保持、2026-07-23)</summary>
    private bool _colorEditModeActive;
    /// <summary>色編集モードの「即時適用(全体色変化)にする」チェック状態(2026-07-24)。
    /// ColorEditModeEnabled同様、セッションを跨いで保持しアクティブなコントローラへ都度反映する。</summary>
    private bool _paintAllFlag;
    /// <summary>色編集モードで塗る色のリスト(右パネル「色編集」タブ、常に1件以上)</summary>
    private readonly List<string> _nColorColors = ["#ffffff"];
    /// <summary>色編集タブの各色欄が「透明度を使用する」(色名+透明度指定)モードかどうか
    /// (2026-07-24)。_nColorColorsと常に同じ長さを保つ。</summary>
    private readonly List<bool> _nColorUseOpacity = [false];
    /// <summary>色編集タブの各色欄の透明度値(0-255の文字列、空文字は未指定)。
    /// _nColorColorsと常に同じ長さを保つ。透明度未使用の欄では無視される。</summary>
    private readonly List<string> _nColorOpacity = [""];
    private bool _suppressSelectionEvent;
    private bool _suppressPropertyPanelEvents;
    private bool _suppressObjectPanelEvents;
    private ObjectRef? _currentPropertyObject;
    private EditorDocument? _selectionSubscribedDoc;
    private string? _currentFilePath;

    /// <summary>共同編集セッション(2026-09-20、共同編集 設計メモ参照)。null=未接続。
    /// ホスト/ゲストいずれか一方のみ同時に持てる(CollabSessionController側で保証)。
    /// 現状はアクティブなセッション(_document)固定で開始し、セッション中のプロジェクトタブ切替は
    /// 未対応(フェーズ1 MVPの範囲外)。</summary>
    private Collab.CollabSessionController? _collab;

    /// <summary>仲介ヘルパー(2026-09-20、設計メモ4.2節、CGNAT対応)。null=待機していない。
    /// 共同編集セッション本体(_collab)とは独立したライフサイクルを持つ
    /// (自分がホスト/ゲストのいずれであっても、あるいはどちらでもなくても待機できる)。</summary>
    private DanoniEditor.Collab.Rendezvous.RendezvousHelperServer? _rendezvousHelper;

    /// <summary>レーン入替マクロ一覧(仕様書11章、2026-07-26)。settings.jsonとは独立した、
    /// キー種ごとの"s-macro_キー種.json"(AppPaths.SettingsDir内)で管理する(2026-07-26g)。</summary>
    private readonly List<LaneSwapMacro> _macros;

    // --- マルチプロジェクトタブ(2026-07-20、TBD#10) ---
    /// <summary>1プロジェクトタブ分の実行時状態。切替時に_document/_controller/_currentFilePathへ
    /// 読み書きする(既存コードの大半が_document等のフィールドを直接参照しているため、それらを
    /// 「アクティブなセッションの写し」として扱う設計。音楽/波形状態はOpenDocument内の
    /// ResetAudioForDocumentがdoc.Project.AudioFilePathから毎回再構築するため、ここでは保持しない)。</summary>
    private sealed class ProjectSession
    {
        public required EditorDocument Document { get; init; }
        public required SmartToolController Controller { get; set; }
        public string? FilePath { get; set; }

        /// <summary>自動保存スロットの一意なID(2026-07-25)。セッション生成時に1回だけ発行し、
        /// アプリの実行中は変わらない(復旧時に開いたセッションも新規に発行し直す)。</summary>
        public string SlotId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>プロジェクトタブの表示ラベル(プロジェクト名、未設定なら"Untitled" + 半角スペース + 未保存マーカー"*")</summary>
        public string TabLabel
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(Document.Project.ProjectName) ? "Untitled" : Document.Project.ProjectName;
                return Document.IsModified ? $"{name} *" : name;
            }
        }
    }

    private readonly List<ProjectSession> _sessions = [];
    private int _activeSessionIndex = -1;
    private bool _suppressProjectTabSelectionEvent;

    // --- 音楽ファイル再生(目テスト・プレイテスト用) ---
    // 2026-07-26f: WPF MediaPlayerから自前のNAudioBgmPlayerへ移行(ハンドクラップのサンプル精度
    // スケジューリング対応、詳細はNAudioBgmPlayer.cs参照)。
    private readonly NAudioBgmPlayer _audioPlayer = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) }; // ≒30fps同期

    /// <summary>自動保存(クラッシュ復旧用、2026-07-25)。間隔・ON/OFFはApplyAutoSaveTimerSettingsで反映。</summary>
    private readonly DispatcherTimer _autoSaveTimer = new();

    /// <summary>Spaceキーで開始する目視テスト中か(2026-07-17f)。目視テスト中のみ
    /// 追従スクロールと終了時のスクロール復帰が働く。</summary>
    private bool _visualTestActive;

    /// <summary>直近1回分の目視テスト開始試行の診断情報(2026-09-07要望対応、「Spaceで開始しても
    /// 無音・再生位置ラインが動かない」不具合の切り分け用)。StartVisualTest()のたびに上書きされる。
    /// 「設定 > 環境報告作成」に含めることで、コード修正無しに次回発生時の内部状態を確認できる。</summary>
    private VisualTestDiagnostics? _lastVisualTestDiag;

    // --- WASAPI出力の自動復旧(2026-09-13要望対応、PlaybackTimer_Tick参照) ---
    // 2026-09-13: 当初800msにしていたが、判定を「完全一致」(下記PlaybackTimer_Tick参照)に変更した
    // ことで誤検知の懸念(極端なスロー再生時に僅かな進みを誤差として無視してしまう問題)が無くなった
    // ため、DispatcherTimerの間隔(33ms)の1回分のブレで誤発動しない程度の余裕を見つつ250msへ短縮した。
    private static readonly TimeSpan StallRecoveryThreshold = TimeSpan.FromMilliseconds(250);
    private double _lastTickPositionSeconds = -1;
    private DateTime _lastTickPositionChangedUtc = DateTime.MinValue;

    // --- DAWループ再生(2026-07-29要望対応、既定OFF)。時間情報レーンの範囲選択(TimeRangeSelection
    // StartTick/EndTick)をそのままループ区間として使う。目視テスト専用(要望原文通り、プレイテストは対象外)。 ---
    private bool _loopPlaybackEnabled;

    // --- プレイ画面プレビュー(2026-07-29要望対応、右パネル「プレビュー」タブ)。 ---
    private readonly PlayPreviewSurface _previewSurface = new();

    /// <summary>音楽ファイル読込済みか(2026-07-17g: 再生ボタン撤去に伴いIsEnabledの代わりに保持)</summary>
    private bool _audioLoaded;

    /// <summary>musicURL欄がユーザーにより編集されたか(2026-07-26)。「読込」ボタンの活性化条件の1つ。
    /// ドキュメント読込・生成のたびにfalseへリセットする(RefreshProjectPropertiesPanel参照)。</summary>
    private bool _musicUrlDirty;

    /// <summary>音量スライダー/数値入力欄の相互同期中に再帰更新を防ぐガード(2026-07-26)。</summary>
    private bool _suppressVolumeEvents;

    // --- 波形表示(2026-07-18) ---
    private Core.Audio.WaveformPeaks? _waveformPeaks;
    private string? _waveformPath;
    private bool _waveformDecoding;

    // --- アプリ環境設定(仕様書14章、2026-07-16b: ノート強調グリッドの太さ・色から実装開始) ---
    private AppSettings _appSettings = new();

    // --- ショートカットキーカスタマイズ(2026-07-27要望対応)。chord(キー+修飾キー)→ShortcutIdの
    // 解決テーブル。_appSettings.Shortcutsが変わるたび(構築時・環境設定確定時)にRebuildShortcutChordMapで再構築する。 ---
    private readonly Dictionary<(Key Key, bool Ctrl, bool Shift, bool Alt), ShortcutId> _shortcutChordMap = [];

    // --- キーボードモード専用ショートカットキーカスタマイズ(2026-07-29要望対応)。マウスモードの
    // _shortcutChordMapとは別テーブル(Keyのみ、修飾キーは扱わない)で、同じ物理キーが重複して
    // 割り当てられることを許容する。_appSettings.KeyboardModeShortcutsが変わるたびに再構築する。 ---
    private readonly Dictionary<Key, KeyboardModeShortcutId> _keyboardShortcutChordMap = [];

    /// <summary>
    /// コンストラクタ完了フラグ。ShowNoteImagesToggleのIsChecked="True"(XAML)は
    /// InitializeComponent()実行中、ツリーがまだ構築し切っていない段階(Canvasはこのトグルより
    /// 後方の要素なのでまだ未生成)でChecked イベントを同期的に発火させてしまい、
    /// ApplyDisplaySettingsToCanvas()内でCanvasがnullのままNullReferenceExceptionが飛ぶ
    /// (=ウィンドウが一度も表示されずに落ちる。2026-07-16c: 「ビルドは通るが起動しない」の原因)。
    /// このフラグでInitializeComponent中に暴発したイベントを無視する。
    /// </summary>
    private bool _initialized;

    /// <summary>このプロセス(ウィンドウ)自身を識別するID(2026-07-26、複数ウィンドウ対応でクラッシュ
    /// フラグ・自動保存manifestをインスタンス単位に分離するために追加)。App.xaml.csで生成された
    /// ものをそのまま受け取り、自動保存の書き込み(WriteSlot)時に持ち主として渡す。</summary>
    private readonly string _instanceId;

    /// <summary>既定コンストラクタ(設定・テンプレートは自前で読み込む)。App.xaml.cs以外から
    /// 直接生成する場合はこちらを使う。</summary>
    public MainWindow() : this(null, null, Guid.NewGuid().ToString("N")) { }

    /// <summary>2026-07-26: スプラッシュウィンドウからの起動用。設定・テンプレートを事前に読み込んで
    /// 渡せるようにし、App.OnStartup側の進捗表示と実際の読込処理を1:1にする
    /// (省略時は従来通りここで読み込む)。2026-07-26: instanceIdはApp.xaml.csが発行した
    /// クラッシュフラグ・自動保存manifestの持ち主IDをそのまま受け取る。</summary>
    public MainWindow(AppSettings? preloadedSettings, TemplateRepository? preloadedTemplates, string instanceId)
    {
        _instanceId = instanceId;
        InitializeComponent();
        // 2026-09-21: 5ステップ計画Step4(AvalonDock導入)。LayoutAnchorableはFrameworkElementでは
        // ないためXAML上でVisibility属性を宣言できない。従来のTabItem Visibility="Collapsed"
        // (未解禁時は分析タブ自体を隠す、2026-07-26要望対応)と同じ既定状態をここで明示する。
        AnalysisTabItem.Hide();
        // 2026-08-08: レーンラベルヘッダー(LaneHeaderBar、ScrollViewer外の専用領域)と対になる
        // ChartCanvasを相互に結び付ける(ChartCanvas.HeaderBarはズーム変更時の再計測通知用、
        // LaneHeaderBar.TargetCanvasは描画内容の参照元)。
        LaneHeaderBar1.TargetCanvas = Canvas;
        Canvas.HeaderBar = LaneHeaderBar1;
        LaneHeaderBar2.TargetCanvas = Canvas2;
        Canvas2.HeaderBar = LaneHeaderBar2;
        // 2026-08-09要望対応: 難易度タブ/プロジェクトタブが増えた際、標準TabPanelの折り返しで
        // 2行目以降が高さ固定のDockPanel(Height=28)内に隠れてしまう不具合の対策。折り返しを許さず、
        // タブ数に応じて各タブの幅を動的に縮小し常に1行へ収める(AdjustTabHeaderWidths参照)。
        // タブ数変化(ItemContainerGenerator.StatusChanged)・幅変化(SizeChanged)の両方で再計算する。
        ProjectTabControl.SizeChanged += (_, _) => AdjustTabHeaderWidths(ProjectTabControl);
        ProjectTabControl.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ProjectTabControl.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                AdjustTabHeaderWidths(ProjectTabControl);
        };
        DifficultyTabControl.SizeChanged += (_, _) => AdjustTabHeaderWidths(DifficultyTabControl);
        DifficultyTabControl.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (DifficultyTabControl.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                AdjustTabHeaderWidths(DifficultyTabControl);
        };
        _templates = preloadedTemplates ?? new TemplateRepository(FindTemplateDir());
        _macros = LaneSwapMacroFile.LoadAll(AppPaths.SettingsDir); // 2026-07-26g: キー種ごとのs-macro_*.jsonへ分割(旧swap_macro.jsonは自動移行)
        _pluginManager = new Plugins.PluginManager(() => _document); // 2026-07-26: プラグイン対応の土台
        SnapDivisionCombo.ItemsSource = SnapService.Divisions;
        SnapDivisionCombo.SelectedItem = 16;

        // 2026-07-26: 音楽読込完了(非同期)のたびに全体長(フレーム)をChartCanvas/ChartMinimapへ反映。
        // ノートを置いていなくても曲の長さぶんスクロールできるようにするための値(要望対応)。
        _audioPlayer.MediaOpened += AudioPlayer_MediaOpened;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        // 2026-07-26: Alt+ホイール(譜面ビューの横ズーム)でAltキーを離した際、Windows/WPF標準の
        // 「単独Alt押下→メニューへのアクセスキーフォーカス」機能が働き、メニュー(ファイル(F)等)へ
        // フォーカスが奪われてしまう不具合への対処。単独AltのKeyDown/KeyUpをここで握りつぶし、
        // メニューのアクセスキー処理へ渡らないようにする(Ctrl+Alt等の組み合わせは通常通り通す)。
        PreviewKeyDown += MainWindow_SuppressLoneAltMenuFocus;
        PreviewKeyUp += MainWindow_SuppressLoneAltMenuFocus;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick; // 2026-07-25

        // 2026-07-20: D&Dによるファイル読み込み(仕様書TBD#7)。ウィンドウ全体を対象にする。
        AllowDrop = true;
        DragEnter += Window_DragEnter;
        Drop += Window_Drop;

        // 2026-07-17f: 上部パネルのカーソル位置表示(tick/frame/秒)。ChartCanvasのOnMouseMoveは
        // キャプチャ中しかControllerへ流さないが、添付ハンドラは常時発火するのでここで拾う。
        Canvas.MouseMove += (_, me) =>
        {
            if (_document is null) return;
            double t = Math.Max(0, _document.CurrentLayout.YToTick(me.GetPosition(Canvas).Y));
            CursorPosText.Text = FormatTickPos(t);
        };

        // StartNumberドラッグ確定時に右パネルの数値表示を同期する(2026-07-18)
        Canvas.StartNumberChangedByDrag += RefreshProjectPropertiesPanel;

        // 2026-07-29要望対応: プレイ画面プレビュー(仮想スナップショット)を右パネルへ設置
        PreviewHostBorder.Child = _previewSurface;

        // 時間範囲選択の設置/移動確定時に右パネルの範囲表示を同期する(2026-07-27)
        Canvas.TimeRangeSelectionChanged += RefreshTimeRangeStatus;

        _appSettings = preloadedSettings ?? AppSettings.Load(AppPaths.SettingsFilePath);
        RebuildShortcutChordMap(); // 2026-07-27要望対応: ショートカットキーカスタマイズ
        RebuildKeyboardShortcutChordMap(); // 2026-07-29要望対応: キーボードモード専用ショートカットキーカスタマイズ
        // 2026-07-29要望対応: プレイ画面プレビューの「ノートの表示期限」設定を復元
        PreviewExpiryOverlapRadio.IsChecked = _appSettings.PreviewNoteExpiryMode == "overlap";
        PreviewExpiryPassThroughRadio.IsChecked = _appSettings.PreviewNoteExpiryMode != "overlap";
        // 2026-07-29要望対応: プレビューの表示サイズ倍率(25%〜200%)
        PreviewScaleCombo.ItemsSource = PreviewScaleValues;
        PreviewScaleCombo.SelectedItem = PreviewScaleValues.OrderBy(v => Math.Abs(v - _appSettings.PreviewDisplayScale)).First();
        ApplyPreviewDisplayScale();
        ApplyAutoSaveTimerSettings(); // 2026-07-25
        ShowNoteImagesToggle.IsChecked = _appSettings.ShowNoteImages;
        ShowHighlightGridToggle.IsChecked = _appSettings.ShowHighlightGrid;
        NoteCountToggle.IsChecked = _appSettings.ShowLaneNoteCount; // 2026-07-26
        LaneNameLabelToggle.IsChecked = _appSettings.ShowLaneNameLabel; // 2026-08-02
        ApplyDisplaySettingsToCanvas();
        // 2026-07-26要望対応: 譜面ビュー分割表示(既定OFF、エディタ全体で共通の設定)
        SplitViewToggle.IsChecked = _appSettings.SplitViewEnabled;
        _splitViewEnabled = _appSettings.SplitViewEnabled;
        ApplySplitViewLayout();

        // 2026-07-17g: プレイテスト設定(Reverse/ハイスピ/調整オフセット)の初期化
        PlaytestHiSpeedCombo.ItemsSource = Enumerable.Range(1, 40).Select(i => i * 0.25).ToList(); // x0.25〜x10(2026-07-19: 0.25刻み化、TBD§1-4の一部)
        PlaytestHiSpeedCombo.SelectedItem = PlaytestHiSpeedValues_Nearest(_appSettings.PlaytestHiSpeed);
        PlaytestReverseCheck.IsChecked = _appSettings.PlaytestReverse;
        PlaytestAutoPlayCheck.IsChecked = _appSettings.PlaytestAutoPlay;
        HandClapCheck.IsChecked = _appSettings.HandClapEnabled; // 2026-07-26
        HandClapVolumeBox.Text = Math.Round(_appSettings.HandClapVolume * 100).ToString(System.Globalization.CultureInfo.InvariantCulture); // 2026-07-26b
        PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PlaytestScaleCombo.ItemsSource = PlaytestScaleValues; // ウィンドウサイズ倍率 x0.5〜3(2026-07-17h)
        PlaytestScaleCombo.SelectedItem = PlaytestScaleValues.OrderBy(v => Math.Abs(v - _appSettings.PlaytestWindowScale)).First();

        // 2026-07-23: 再生速度(目視テスト・プレイテスト共通、0.1〜2.0・0.1刻み)
        PlaybackSpeedCombo.ItemsSource = Enumerable.Range(1, 20).Select(i => Math.Round(i * 0.1, 1)).ToList();
        PlaybackSpeedCombo.SelectedItem = PlaybackSpeedValues_Nearest(_appSettings.PlaybackSpeed);
        _audioPlayer.SpeedRatio = _appSettings.PlaybackSpeed;
        // 2026-08-08要望対応: 再生速度をプレイテストへ反映するかどうか(既定OFF)
        ReflectPlaybackSpeedInPlaytestToggle.IsChecked = _appSettings.ReflectPlaybackSpeedInPlaytest;
        // 2026-08-08c要望対応: プレイテストのスクロール速度を再生速度に関わらず一定に保つかどうか(既定OFF)
        KeepScrollSpeedInPlaytestToggle.IsChecked = _appSettings.KeepScrollSpeedInPlaytest;
        UpdateKeepScrollSpeedToggleEnabled(); // 2026-08-08d: 「プレイテストへ反映」OFFの間はグレーアウト

        // 2026-07-26: 音量(0〜100%、スライダー+数値入力欄を相互同期)
        _suppressVolumeEvents = true;
        VolumeSlider.Value = Math.Clamp(_appSettings.PlaybackVolume, 0.0, 1.0) * 100;
        VolumeBox.Text = Math.Round(VolumeSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _suppressVolumeEvents = false;
        _audioPlayer.Volume = _appSettings.PlaybackVolume;

        // 2026-07-23: 色編集モード(ncolor_data)の右パネル初期化
        NColorGradientTypeCombo.ItemsSource = new[] { "単色", "linear-gradient", "radial-gradient", "conic-gradient" };
        NColorGradientTypeCombo.SelectedIndex = 0;
        RebuildNColorListPanel();
        UpdateNColorPreview();

        // 2026-07-24: frzHitColor/ShadowColorサブモードの色欄初期値(既定色は本体の既定塗りつぶし色
        // #000000に合わせる。Hit/HitBarはNormal/NormalBarと同じ#ffffffを仮の初期値としている)。
        NColorHitColorBox.Text = "#ffffff";
        NColorHitBarColorBox.Text = "#ffffff";
        NColorHitShadowColorBox.Text = "#000000";
        NColorArrowShadowColorBox.Text = "#000000";
        NColorNormalShadowColorBox.Text = "#000000";

        RefreshMacroList(); // 2026-07-26: レーン入替マクロ一覧(プロジェクト未オープンでも表示できる)
        RefreshLinkPanel(); // 2026-07-26: タブリンクパネルも同様に初期化する
        RefreshParticipantsPanel(); // 2026-09-21: 参加者一覧パネル(5ステップ計画Step5)も同様に初期化する

        // 2026-07-26: プラグイン対応の土台。./pluginsフォルダを読み込み、パネル系プラグインは
        // 右パネルへタブとして追加、オーバーレイ系プラグインは譜面ビューへ登録する。
        // 読み込み時に問題があった場合は起動を止めず、まとめて一度だけ通知する。
        foreach (var panelPlugin in _pluginManager.PanelPlugins)
        {
            try
            {
                var pluginAnchorable = new LayoutAnchorable { Title = panelPlugin.PanelTitle, Content = panelPlugin.CreatePanel(), CanClose = false };
                PropertyAnchorablePane.Children.Add(pluginAnchorable);
                _pluginPanelAnchorables.Add(pluginAnchorable); // 2026-09-21: 表示メニューの並び順固定用
            }
            catch (Exception ex)
            {
                Plugins.PluginLog.Write($"{panelPlugin.Id}: CreatePanelで例外が発生しました({ex.Message})");
            }
        }
        Canvas.OverlayPlugins = _pluginManager.OverlayPlugins;
        if (_pluginManager.LoadErrors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", _pluginManager.LoadErrors), "プラグインの読み込みで問題がありました",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 2026-07-26: 終了時のウィンドウ状態(モニタ/最大化/位置サイズ)を復元し、終了時に保存する。
        RestoreWindowPlacement();
        RestoreRightPanelWidth(); // 2026-07-29要望対応
        Closed += (_, _) => { SaveWindowPlacement(); SaveRightPanelWidth(); _audioPlayer.Dispose(); }; // 2026-07-26f/2026-07-29

        // 2026-08-08要望対応: 上パネルのボタン/トグルボタン/チェックボックス/コンボボックスをマウス
        // クリックで操作した後、キーボードショートカット(スナップ切替・Space・Ctrl+P等)がすぐ使える
        // よう、必ず譜面ビューへフォーカスを戻す。個々のイベントハンドラへ都度Focus呼び出しを
        // 追加するのではなく、上パネルのBorder(TopPanelBorder)へButtonBase.Click/
        // Selector.SelectionChangedのバブリングハンドラを1つずつ追加するだけで、現在・将来の
        // 全ての該当コントロールをまとめてカバーする(CheckBox/ToggleButtonはButtonBaseのClickも
        // 併せて発火するため、Checked/Unchecked個別のフックは不要)。
        // なお、初期化時・環境設定ウィンドウ復帰時のコード側でのIsChecked/SelectedItem代入でも
        // Checked/Unchecked・SelectionChangedは発火するが、Clickはユーザーの物理クリック時にしか
        // 発火しないため、ここではAddHandlerを初期化完了(_initialized = true)の直前に置くことで、
        // 起動シーケンス中のコード側同期による意図しないフォーカス移動を避けている
        // (SelectionChangedはコード代入でも発火するため完全には避けられないが、その時点では
        // ウィンドウがまだ表示されておらずFocus呼び出しは実害が無い)。
        TopPanelBorder.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => FocusChartView()));
        TopPanelBorder.AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler((_, _) => FocusChartView()));

        _initialized = true;
    }

    /// <summary>2026-08-08要望対応: 上パネル操作後に譜面ビューへフォーカスを戻す。分割ビュー中は
    /// アクティブペイン側(_activePaneIsSecondary)のCanvasへフォーカスする(ActiveChartScrollViewer
    /// と同じ判定基準)。</summary>
    private void FocusChartView()
    {
        var canvas = _splitViewEnabled && _activePaneIsSecondary ? Canvas2 : Canvas;
        canvas.Focus();
    }

    // =====================================================================
    // 終了時のウィンドウ状態の保存/復元(2026-07-26要望対応)
    // =====================================================================

    /// <summary>起動時、前回終了時の位置・サイズ・最大化状態を復元する。WindowLeft/Topは仮想スクリーン
    /// 座標(マルチモニタをまたいだ通し座標)のため、これ自体が「どの画面にあったか」を表す。
    /// モニタ構成が変わって画面外(現在の仮想スクリーン範囲外)になっている場合は、位置指定を諦めて
    /// OS既定の位置へフォールバックする(画面外に表示されて操作不能になる事故を避けるため)。</summary>
    private void RestoreWindowPlacement()
    {
        if (_appSettings.WindowLeft is not { } left || _appSettings.WindowTop is not { } top ||
            _appSettings.WindowWidth is not { } width || _appSettings.WindowHeight is not { } height)
            return; // 未保存(初回起動等)はOS既定の位置のまま

        double vLeft = SystemParameters.VirtualScreenLeft, vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth, vBottom = vTop + SystemParameters.VirtualScreenHeight;
        // ウィンドウの少なくとも一部(タイトルバー付近)が現在の仮想スクリーン範囲内に収まっているかで判定
        bool onScreen = left + width > vLeft && left < vRight && top + 40 > vTop && top < vBottom;
        if (!onScreen) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (_appSettings.WindowMaximized)
        {
            // 2026-07-26: WindowState=Maximizedを起動直後に直接指定すると、Left/Topで指定した
            // モニタではなくプライマリ画面側で最大化されることがあるため、Loadedまで遅延させる。
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    /// <summary>終了時、最大化状態・非最大化時の位置サイズ(RestoreBounds)を保存する。</summary>
    private void SaveWindowPlacement()
    {
        _appSettings.WindowMaximized = WindowState == WindowState.Maximized;

        // 最大化中はRestoreBoundsが「解除した時に戻る非最大化時の位置サイズ」を保持しているが、
        // ウィンドウが一度もNormal状態を経ずに最大化された場合(起動直後にRestoreWindowPlacementの
        // Loadedハンドラで直接最大化した場合等)、RestoreBoundsがRect.Empty(Left/Top=+∞、
        // Width/Height=-∞)を返すことがある(2026-07-26実データで確認)。この無限大値をそのまま
        // AppSettingsへ書き込むと、保存時にSystem.Text.Jsonが「positive and negative infinity
        // cannot be written as valid JSON」で例外を投げ、正常保存・正常終了したはずのセッションが
        // 予期しないエラーダイアログ経由でクラッシュ扱いされてしまう不具合の原因になっていた。
        // 有限値でない場合は、現在のウィンドウの実際のLeft/Top/Width/Height(最大化時は画面いっぱいの
        // 値になる)へフォールバックする。
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (!IsFiniteRect(bounds)) bounds = new Rect(Left, Top, Width, Height);

        if (IsFiniteRect(bounds))
        {
            _appSettings.WindowLeft = bounds.Left;
            _appSettings.WindowTop = bounds.Top;
            _appSettings.WindowWidth = bounds.Width;
            _appSettings.WindowHeight = bounds.Height;
        }
        // どちらも有限でなければ(通常は起こらない想定)前回保存済みの値をそのまま残し、位置情報の
        // 更新のみスキップする(最大化状態のフラグ自体は上で更新済み)。

        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>Rectの4成分が全て有限値(NaN・±Infinityでない)かどうか(2026-07-26、
    /// Window.RestoreBoundsがRect.Emptyを返すケースの検出用)。</summary>
    private static bool IsFiniteRect(Rect r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Width) && double.IsFinite(r.Height);

    /// <summary>起動時、前回終了時の右パネル幅(譜面ビューとの境界のGridSplitterでドラッグ調整した幅)を
    /// 復元する(2026-07-29要望対応)。未保存(初回起動等)の場合はXAML既定値(280px)のまま。</summary>
    private void RestoreRightPanelWidth()
    {
        if (_appSettings.RightPanelWidth is not { } width || !double.IsFinite(width) || width <= 0) return;
        RightPanelColumn.Width = new GridLength(width);
    }

    /// <summary>終了時、右パネルの現在の幅を保存する(2026-07-29要望対応)。</summary>
    private void SaveRightPanelWidth()
    {
        double width = RightPanelColumn.ActualWidth;
        if (double.IsFinite(width) && width > 0) _appSettings.RightPanelWidth = width;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    private void ApplyDisplaySettingsToCanvas()
    {
        var color = (Color)ColorConverter.ConvertFromString(_appSettings.HighlightLineColorHex)!;
        Canvas.ApplyDisplaySettings(_appSettings.ShowNoteImages, _appSettings.ShowHighlightGrid, _appSettings.HighlightLineWidth, color,
            _appSettings.ExcludeFreezeEndFromHighlight, _appSettings.UseNoteColorForHighlight);
        var startColor = (Color)ColorConverter.ConvertFromString(_appSettings.PlaybackStartLineColorHex)!;
        Canvas.ApplyPlaybackStartLineSettings(_appSettings.PlaybackStartLineWidth, startColor);
        // 2026-07-25b: カーソルライン(マウスホバー中の最寄りスナップ位置)の太さ・色
        var cursorLineColor = (Color)ColorConverter.ConvertFromString(_appSettings.CursorLineColorHex)!;
        var cursorHighlightColor = (Color)ColorConverter.ConvertFromString(_appSettings.CursorHighlightColorHex)!;
        Canvas.ApplyCursorLineSettings(_appSettings.CursorLineWidth, cursorLineColor, _appSettings.CursorHighlightWidth, cursorHighlightColor);
        // 2026-07-26: レーン入替マクロ「選択範囲内のみ適用」の範囲マーカー・ハイライト帯
        var macroRangeColor = (Color)ColorConverter.ConvertFromString(_appSettings.MacroRangeHighlightColorHex)!;
        Canvas.ApplyMacroRangeHighlightSettings(_appSettings.MacroRangeMarkerWidth, macroRangeColor);
        // 2026-07-26: タブリンク機能の背景ノート表示設定
        var linkedNoteColor = (Color)ColorConverter.ConvertFromString(_appSettings.LinkedNoteColorHex)!;
        var linkedHighlightColor = (Color)ColorConverter.ConvertFromString(_appSettings.LinkedHighlightColorHex)!;
        Canvas.ApplyLinkedBackgroundSettings(_appSettings.LinkedNoteSizeRatio, linkedNoteColor,
            _appSettings.LinkedHighlightWidthRatio, _appSettings.LinkedHighlightHeight, linkedHighlightColor);
        Canvas.MarkerCommentFull = _appSettings.MarkerCommentFull;   // 2026-07-19b
        Canvas.MarkerCommentHeadChars = Math.Max(1, _appSettings.MarkerCommentHeadChars);
        Canvas.TimeInfoFontSize = _appSettings.TimeInfoFontSize;     // 2026-07-26
        Canvas.MarkerFontSize = _appSettings.MarkerFontSize;         // 2026-07-26
        Canvas.Reverse = _appSettings.ChartViewReverse; // 2026-07-22: 譜面ビューReverse(環境設定のみで切替)
        Canvas.ShowLaneNoteCount = _appSettings.ShowLaneNoteCount; // 2026-07-26
        Canvas.ShowLaneNameLabel = _appSettings.ShowLaneNameLabel; // 2026-08-02
        Canvas.ShowFrameWithBlankFrame = _appSettings.ShowFrameWithBlankFrame; // 2026-08-08
        ApplyLaneLabelHeaderPosition(); // 2026-08-08: レーンラベルヘッダーの表示位置(上部/下部/非表示)
        InvalidateChartViews();
    }

    private void ShowNoteImagesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // XAML初期値設定によるInitializeComponent中の発火を無視(上記コメント参照)
        EnforceAndApplyDisplayToggles();
    }

    /// <summary>「ノート数表示」トグル(2026-07-26)。他のトグルとの排他制約は無いため単純に反映するのみ。</summary>
    private void NoteCountToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.ShowLaneNoteCount = NoteCountToggle.IsChecked == true;
        ApplyDisplaySettingsToCanvas();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>「レーン名表示」トグル(2026-08-02要望対応)。レーンラベル欄の1行目表示を
    /// キー割当(既定)⇔レーン名(LaneId)で切り替える。他のトグルとの排他制約は無い。</summary>
    private void LaneNameLabelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.ShowLaneNameLabel = LaneNameLabelToggle.IsChecked == true;
        ApplyDisplaySettingsToCanvas();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    private void ShowHighlightGridToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        EnforceAndApplyDisplayToggles();
    }

    /// <summary>
    /// 「ノート画像 表示」と「強調表示」の両方が同時にOFFにならないようにする(2026-07-16j)。
    /// 2つのトグルの現在状態をUIから読み直し、両方OFFならノート画像側を強制的にONへ戻す
    /// (このIsChecked代入で本メソッドが再度呼ばれ、その時点で両方OFFではなくなっているので
    /// 下のif には入らず最終的な状態がまとめて_appSettingsへ確定・保存される)。
    /// </summary>
    private void EnforceAndApplyDisplayToggles()
    {
        bool showImages = ShowNoteImagesToggle.IsChecked == true;
        bool showGrid = ShowHighlightGridToggle.IsChecked == true;
        if (!showImages && !showGrid)
        {
            ShowNoteImagesToggle.IsChecked = true;
            return;
        }
        _appSettings.ShowNoteImages = showImages;
        _appSettings.ShowHighlightGrid = showGrid;
        ApplyDisplaySettingsToCanvas();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>設定 > 環境設定(2026-07-19)。旧DisplaySettingsDialogを置き換えるカテゴリ式ウィンドウ</summary>
    private void OpenPreferences_Click(object sender, RoutedEventArgs e) => OpenPreferences(0);

    /// <summary>上部パネルの「表示設定...」ボタン(従来動作互換: 表示カテゴリを開く)</summary>
    private void DisplaySettings_Click(object sender, RoutedEventArgs e) => OpenPreferences(0);

    /// <summary>右パネル「プロジェクト」タブ・「オブジェクト」タブ双方の「ゲージ設定...」ボタン共通
    /// (customGauge/gaugeXXX/difData専用ウィンドウ、2026-07-30よりオブジェクトタブにも導線追加)。
    /// 2026-08-02要望対応: モーダル(ShowDialog)だと、そこから開くゲージ計算機の結果を見ながら
    /// 譜面ビュー等の他ウィンドウを参照できず不便なため、WordLaneManagerWindowと同じパターンで
    /// モードレス化した(フィールドで保持しActivate()により多重起動を防止、Closedで保存有無を反映)。</summary>
    private GaugeEditorWindow? _gaugeEditorWindow;
    private void OpenGaugeEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "編集できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_gaugeEditorWindow is { IsLoaded: true })
        {
            _gaugeEditorWindow.Activate();
            return;
        }
        var win = new GaugeEditorWindow(_document.Project) { Owner = this };
        win.Closed += (_, _) =>
        {
            if (win.Saved) _document.NotifyChanged();
            _gaugeEditorWindow = null;
        };
        _gaugeEditorWindow = win;
        win.Show();
    }

    /// <summary>歌詞レーンの管理ウィンドウを開く(2026-07-23、TBD 4)。モードレスなので開いたまま
    /// 譜面ビューでの歌詞エントリ配置・編集ができる。レーン追加/削除のたびに譜面ビューを再描画する。</summary>
    private WordLaneManagerWindow? _wordLaneManagerWindow;
    private void OpenWordLaneManager_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "編集できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_wordLaneManagerWindow is { IsLoaded: true })
        {
            _wordLaneManagerWindow.Activate();
            return;
        }
        _wordLaneManagerWindow = new WordLaneManagerWindow(_document, () => InvalidateChartViews()) { Owner = this };
        _wordLaneManagerWindow.Show();
    }

    private void OpenPreferences(int category)
    {
        var win = new PreferencesWindow(_appSettings, category, _templates) { Owner = this };
        if (win.ShowDialog() != true || win.Result is null) return;
        _appSettings = win.Result;
        RebuildShortcutChordMap(); // 2026-07-27要望対応: ショートカットキーカスタマイズ変更を反映
        RebuildKeyboardShortcutChordMap(); // 2026-07-29要望対応: キーボードモード専用ショートカットキーカスタマイズ変更を反映
        RefreshPreviewPanel(); // 2026-07-29要望対応: Reverse/HiSpeed/調整オフセット等の変更をプレビューへ反映
        _appSettings.Save(AppPaths.SettingsFilePath);
        _handClapPlayer = null; // ノート音の選択ファイルが変わった可能性があるため再読込させる
        ApplyAutoSaveTimerSettings(); // 2026-07-25
        ApplyDisplaySettingsToCanvas();
        if (_document is not null) _document.UndoCapacity = Math.Max(1, _appSettings.UndoHistorySize); // 2026-07-19b(2026-08-06: 全タブの履歴へ適用)
        if (_keyboardMode is not null) _keyboardMode.ThresholdMs = _appSettings.SimultaneousPressThresholdMs; // 2026-07-21

        // 上部パネルの同項目コントロールへ反映(各Changedハンドラが再保存するが実害なし)
        ShowNoteImagesToggle.IsChecked = _appSettings.ShowNoteImages;
        ShowHighlightGridToggle.IsChecked = _appSettings.ShowHighlightGrid;
        PlaytestReverseCheck.IsChecked = _appSettings.PlaytestReverse;
        PlaytestAutoPlayCheck.IsChecked = _appSettings.PlaytestAutoPlay;
        PlaytestHiSpeedCombo.SelectedItem = PlaytestHiSpeedValues_Nearest(_appSettings.PlaytestHiSpeed);
        PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(CultureInfo.InvariantCulture);
        PlaytestScaleCombo.SelectedItem = PlaytestScaleValues.OrderBy(v => Math.Abs(v - _appSettings.PlaytestWindowScale)).First();
        ReflectPlaybackSpeedInPlaytestToggle.IsChecked = _appSettings.ReflectPlaybackSpeedInPlaytest; // 2026-08-08
        KeepScrollSpeedInPlaytestToggle.IsChecked = _appSettings.KeepScrollSpeedInPlaytest; // 2026-08-08c
        UpdateKeepScrollSpeedToggleEnabled(); // 2026-08-08d: 「プレイテストへ反映」OFFの間はグレーアウト
        UpdateMusicUrlLoadButtonState(); // 2026-07-26: 機能ON/OFF切替を「読込」ボタンの活性状態へ即反映
        InvalidateChartViews();
    }

    /// <summary>
    /// ./templateフォルダを探す。まずexeと同じフォルダ(publish単独exe配布時、csprojのContent項目でコピーされる場所)を見て、
    /// 無ければAppContext.BaseDirectoryから上へ辿る(開発中のDebug実行、tests側と同じ探索方式)。
    /// </summary>
    private static string FindTemplateDir() => AppPaths.FindAssetDir("template")
        ?? throw new DirectoryNotFoundException("./template フォルダが見つかりません(実行ファイルと同じ場所、および上位ディレクトリを探索しました)");

    /// <summary>1ウィンドウ1曲構成のため、複数曲を並行編集したい場合は新しいプロセスとして
    /// もう1つエディタを起動する(2026-07-17)。現在編集中のプロジェクトとは完全に独立する。</summary>
    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show(this, "実行ファイルのパスを取得できませんでした。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            System.Diagnostics.Process.Start(exePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"新しいウィンドウの起動に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // プロジェクト操作: 新規/開く/保存
    // =====================================================================

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var choice = NewProjectDialog.Ask(this, _templates, _appSettings.DefaultBpm);
        if (choice is not { } c) return;

        var template = _templates.Get(c.KeyTypeId);
        var project = new ChartProject
        {
            ProjectName = "untitled",
            MusicTitle = "無題の楽曲",
            BpmEvents = [new BpmEvent(0, c.Bpm)],
            TimeSignatures = [new TimeSignatureEvent(0, 4, 4)],
            // 環境設定のheaderDefaults(仕様書6.4.1/14章、2026-07-19b)。musicURLは対象外(都度入力)
            StartFrame = _appSettings.DefaultStartFrame,
            BlankFrame = _appSettings.DefaultBlankFrame,
            Tuning = _appSettings.DefaultTuning,
            FrzAttempt = _appSettings.DefaultFrzAttempt,
        };
        var firstTab = DifficultyTab.CreateFor(template, c.DifficultyName);
        // 2026-08-06要望対応: 色の初期値自動補完は「新規プロジェクト作成時」のみに限定する
        // (以前は②タブの表示更新のたびに書き込んでいたため、プロジェクトを開いた時やタブを追加した際にも
        // 勝手に既定色が入ってしまう不具合があった。新規作成の入口はここ一箇所のみなので、ここでだけ
        // 明示的に既定色をセットする)。
        int groupCount = template.Lanes.Select(l => l.ColorGroup).DefaultIfEmpty(0).Max() + 1;
        firstTab.SetColorOverride = DefaultSetColors(groupCount);
        firstTab.FrzColorOverride = DefaultFrzColors();
        project.Tabs.Add(firstTab);
        AddSession(new EditorDocument(project, _templates), null); // 2026-07-20: 新規プロジェクトタブとして追加
        _appSettings.StatNewProjectCount++;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>プロジェクトファイルの既定保存先(仕様書3.1確定: ./projects)を、無ければ作成して返す</summary>
    private static string EnsureProjectsDir()
    {
        Directory.CreateDirectory(AppPaths.ProjectsDir);
        return AppPaths.ProjectsDir;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "プロジェクトファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            InitialDirectory = EnsureProjectsDir(),
        };
        if (dlg.ShowDialog(this) != true) return;
        OpenProjectFile(dlg.FileName);
    }

    /// <summary>自形式プロジェクト(.json)を開く。OpenProject_ClickとD&D(2026-07-20)の共通処理。
    /// 丸ごと置き換え("開く"相当、既存プロジェクトへの合成はしない)。</summary>
    private void OpenProjectFile(string path)
    {
        try
        {
            var project = ProjectSerializer.Load(path);
            // 2026-07-26: タイトルバー・プロジェクトタブは「プロジェクトファイル名(拡張子除く)」を
            // 表示する仕様のため、開いた時点の実際のファイル名で同期する(ファイルがリネームされていた
            // 場合や、保存時ProjectName同期が無かった旧バージョンで保存されたファイルにも対応)。
            project.ProjectName = Path.GetFileNameWithoutExtension(path);

            // 2026-08-08新設(第三者報告のノート重複不具合対応): 気付かないまま重複ノートを含んだ
            // 状態で保存されていた過去のプロジェクトを開いた際、ユーザーに解決方法を選ばせる。
            // EditorDocument化(Undo履歴の起点)より前に素のChartProjectへ直接適用することで、
            // この修正自体はUndo対象にしない(以後の編集操作と区別する)。
            var duplicates = DuplicateNoteChecker.FindDuplicates(project);
            if (duplicates.Count > 0)
            {
                // FindDuplicatesは箇所(タブ×レーン×tick)単位で1件ずつ返すため、件数=箇所数
                var resolution = DuplicateNoteDialog.Ask(this, duplicates.Count);
                if (resolution == DuplicateNoteResolution.Resolve)
                    DuplicateNoteChecker.ResolveByRemoving(project, duplicates);
                else if (resolution == DuplicateNoteResolution.Keep)
                    DuplicateNoteChecker.ResolveByMarking(project, duplicates);
                // null(キャンセル)の場合は何もせず、重複を含んだままのprojectをそのまま開く
            }

            AddSession(new EditorDocument(project, _templates), path); // 2026-07-20: 新規プロジェクトタブとして追加
            // 2026-07-26: musicURL設定済みのITTNエディタ形式プロジェクトを開いた際、機能ONなら自動読込を試みる
            // (ローカルAudioFilePathからの復元(ResetAudioForDocument、AddSession内で実行済み)が
            // 既に成功している場合はTryLoadMusicFromUrl内の_audioLoadedガードで何もしない)。
            TryLoadMusicFromUrl(autoTriggered: true);
            // 2026-07-26: 開いたファイルを「最近開いたファイル」の先頭へ記録する
            _appSettings.AddRecentFile(path);
            _appSettings.Save(AppPaths.SettingsFilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // 最近開いたファイル(2026-07-26、ファイル > 最近開いたファイル)
    // =====================================================================

    /// <summary>サブメニューを開くたびに項目を動的再構築する。存在しなくなったファイルは
    /// 一覧から取り除いてから表示する(PruneMissingRecentFiles)。空なら「(履歴なし)」のみ表示。</summary>
    private void RecentFilesMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (_appSettings.PruneMissingRecentFiles()) _appSettings.Save(AppPaths.SettingsFilePath);

        RecentFilesMenu.Items.Clear();
        if (_appSettings.RecentFiles.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "(履歴なし)", IsEnabled = false });
            return;
        }

        int idx = 1;
        foreach (var path in _appSettings.RecentFiles)
        {
            // 表示は「番号 ファイル名」(フルパスはToolTipで確認)。番号は覚えやすさ・Alt+数字選択の慣習に合わせる。
            var item = new MenuItem { Header = $"_{idx} {Path.GetFileName(path)}", ToolTip = path };
            item.Click += (_, _) => OpenRecentFile(path);
            RecentFilesMenu.Items.Add(item);
            idx++;
        }

        RecentFilesMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "履歴をクリア" };
        clear.Click += (_, _) =>
        {
            _appSettings.RecentFiles.Clear();
            _appSettings.Save(AppPaths.SettingsFilePath);
        };
        RecentFilesMenu.Items.Add(clear);
    }

    /// <summary>「最近開いたファイル」の項目クリック。ファイルが既に無い場合は知らせて一覧から除去する。</summary>
    private void OpenRecentFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"ファイルが見つかりませんでした:\n{path}", "最近開いたファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
            _appSettings.RecentFiles.Remove(path);
            _appSettings.Save(AppPaths.SettingsFilePath);
            return;
        }
        OpenProjectFile(path);
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProjectInternal(forcePrompt: false);

    /// <summary>「ファイル > 名前を付けて保存」(2026-07-26要望対応)。上書き保存と処理は同じで、
    /// 既存の保存先パスの有無に関わらず必ず保存先ダイアログを出す点だけが異なる。</summary>
    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectInternal(forcePrompt: true);

    private void SaveProjectInternal(bool forcePrompt)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから保存する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "保存できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = forcePrompt ? null : _currentFilePath;
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "プロジェクトファイル (*.json)|*.json",
                FileName = _document.Project.ProjectName + ".json",
                InitialDirectory = EnsureProjectsDir(),
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }

        try
        {
            // 2026-07-26: タイトルバー・プロジェクトタブは「プロジェクトファイル名(拡張子除く)」を
            // 表示する仕様のため、保存確定時にProject.ProjectNameを実際の保存先ファイル名へ同期する
            // (従来はNewProject_Click等で設定した"untitled"のまま更新されず、保存後も表示が
            // 変わらない不具合になっていた)。
            _document.Project.ProjectName = Path.GetFileNameWithoutExtension(path);
            // 2026-08-04不具合修正: 旧形式プロジェクト(再生開始フレームをタブ横断で共有していた形式)から
            // 移行する場合、保存直前に各タブへ値を確定させる(EditorDocument.PrepareForSave参照)。
            _document.PrepareForSave();
            ProjectSerializer.Save(_document.Project, path);
            _currentFilePath = path;
            _document.MarkSaved(); // 未保存フラグ解除→タイトルバーの'*'も消える(2026-07-19b)
            // 2026-07-25: 手動保存が完了した時点で、このセッションの自動保存スロットは
            // 役目を終えるので消去する(古い控えが手動保存より後まで残らないようにする)。
            var savedSession = _sessions.FirstOrDefault(x => x.Document == _document);
            if (savedSession is not null) AutoSaveManager.ClearSlot(AppPaths.AutoSaveDir, savedSession.SlotId);
            UpdateWindowTitle();
            RefreshProjectTabBarLabelOnly(); // プロジェクトタブの表示名も同期
            ProjectTitleText.Text = $"{_document.Project.ProjectName} ({_document.Project.MusicTitle})";
            StatusText.Text = $"保存しました: {Path.GetFileName(path)}";
            // 2026-07-26: 保存先も「最近開いたファイル」の先頭へ記録する(初回保存のパス確定時も含む)
            _appSettings.AddRecentFile(path);
            _appSettings.StatProjectSaveCount++; // 2026-07-26: 統計情報(手動保存回数)
            _appSettings.Save(AppPaths.SettingsFilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // dos.txtエクスポート
    // =====================================================================

    /// <summary>「設定 > 環境報告作成...」(2026-07-26要望対応)。バグ報告時のデバッグ情報源として、
    /// OS・.NET・エディタの設定値・プラグイン読み込み状況をまとめたテキストファイルを書き出す。</summary>
    private void CreateDiagnosticsReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt",
            FileName = $"danoni_editor_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var text = DiagnosticsReport.Build(_appSettings, _pluginManager, _lastVisualTestDiag);
            File.WriteAllText(dlg.FileName, text);
            StatusText.Text = $"環境報告を書き出しました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"環境報告の書き出しに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>設定メニュー「バージョン情報」(2026-07-31)</summary>
    private void OpenAbout_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // =====================================================================
    // 共同編集(2026-09-20、共同編集 設計メモ参照。フェーズ1 MVP・簡易版:双方向スナップショット送信)
    // =====================================================================

    private void CollabStartHost_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        if (_collab is not null)
        {
            MessageBox.Show(this, "既に共同編集セッションが開始されています。先に切断してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Collab.CollabHostStartDialog(this, Environment.UserName);
        if (dlg.ShowDialog() != true) return;

        var collab = new Collab.CollabSessionController(Dispatcher);
        AttachCollabUiHandlers(collab);
        try
        {
            collab.StartHost(_document, dlg.Port, dlg.DisplayName);
            _collab = collab;
            SetCollabMenuState(active: true);
            RefreshParticipantsPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ホストの開始に失敗しました: {ex.Message}", "共同編集", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CollabJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        if (_collab is not null)
        {
            MessageBox.Show(this, "既に共同編集セッションが開始されています。先に切断してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Collab.CollabJoinDialog(this, Environment.UserName);
        if (dlg.ShowDialog() != true) return;

        var collab = new Collab.CollabSessionController(Dispatcher);
        AttachCollabUiHandlers(collab);
        try
        {
            await collab.JoinAsync(_document, dlg.HostAddress, dlg.Port, dlg.DisplayName);
            _collab = collab;
            SetCollabMenuState(active: true);
            RefreshParticipantsPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"接続に失敗しました: {ex.Message}", "共同編集", MessageBoxButton.OK, MessageBoxImage.Error);
            await collab.DisposeAsync();
        }
    }

    private async void CollabDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_collab is null) return;
        await _collab.DisconnectAsync();
    }

    private void AttachCollabUiHandlers(Collab.CollabSessionController collab)
    {
        // 2026-09-20: ノート所有者アイコン(設計メモ6.4節)用に、ChartCanvas側へセッション自体への
        // 参照を渡しておく(Document/Controllerと同じ受け渡し方。Canvas2(右ペイン)は分割ビューON時のみ
        // SyncCanvas2FromCanvasで追従するため、開始直後の1回はここで明示的にコピーしておく)。
        Canvas.CollabSession = collab;
        if (_splitViewEnabled) Canvas2.CollabSession = collab;

        collab.StatusChanged += text => CollabStatusText.Text = text;
        // 2026-09-21: 参加者一覧パネル(5ステップ計画Step5)。ロスターが変化するたびに再描画する。
        collab.RosterChanged += _ => RefreshParticipantsPanel();
        collab.RemoteEditApplied += () =>
        {
            InvalidateChartViews();
            Minimap.InvalidateVisual();
            if (_splitViewEnabled) Minimap2.InvalidateVisual();
        };
        collab.Disconnected += () =>
        {
            _collab = null;
            Canvas.CollabSession = null;
            Canvas2.CollabSession = null;
            SetCollabMenuState(active: false);
            CollabStatusText.Text = "共同編集: 未接続";
            InvalidateChartViews(); // 切断直後、表示済みの所有者アイコンを消すために再描画する
            RefreshParticipantsPanel(); // 2026-09-21: 参加者一覧パネルも「未接続」表示へ戻す
        };
    }

    /// <summary>参加者一覧パネルの表示用ラッパー(2026-09-21、5ステップ計画Step5)。「表示名」+
    /// 「識別色のブラシ」をまとめる(色はCollabHost.ResolveColorが割り当てるParticipantInfo.Colorを
    /// そのまま使う。ノート所有者アイコンと同じ色になる)。</summary>
    private sealed record ParticipantRow(string Label, Brush ColorBrush);

    /// <summary>右パネル「参加者一覧」タブの中身を、現在のCollabSessionController.Self/Rosterから
    /// 作り直す(2026-09-21、5ステップ計画Step5)。共同編集セッション未接続時は案内文のみ表示する。
    /// 自分自身はRoster(自分以外の一覧)には含まれないため、Selfを別途先頭に足す。</summary>
    private void RefreshParticipantsPanel()
    {
        ParticipantsListBox.Items.Clear();

        if (_collab is null)
        {
            ParticipantsNotConnectedText.Visibility = Visibility.Visible;
            ParticipantsListBox.Visibility = Visibility.Collapsed;
            return;
        }

        ParticipantsNotConnectedText.Visibility = Visibility.Collapsed;
        ParticipantsListBox.Visibility = Visibility.Visible;

        if (_collab.Self is { } self)
            ParticipantsListBox.Items.Add(new ParticipantRow($"{self.DisplayName} (自分)", SafeColorBrush(self.Color)));
        foreach (var p in _collab.Roster)
            ParticipantsListBox.Items.Add(new ParticipantRow(p.DisplayName, SafeColorBrush(p.Color)));
    }

    private void SetCollabMenuState(bool active)
    {
        CollabStartHostMenuItem.IsEnabled = !active;
        CollabJoinMenuItem.IsEnabled = !active;
        CollabDisconnectMenuItem.IsEnabled = active;
        RendezvousJoinMenuItem.IsEnabled = !active;
        RendezvousAcceptMenuItem.IsEnabled = active && _collab is not null && _collab.IsHosting;
    }

    // --- 仲介ヘルパー機能(2026-09-20、設計メモ4.2節、CGNAT対応) ---

    private void RendezvousHelperStart_Click(object sender, RoutedEventArgs e)
    {
        if (_rendezvousHelper is not null)
        {
            MessageBox.Show(this, "既に仲介ヘルパーとして待機中です。先に停止してください。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Collab.RendezvousHelperStartDialog(this);
        if (dlg.ShowDialog() != true) return;

        try
        {
            var helper = new DanoniEditor.Collab.Rendezvous.RendezvousHelperServer(dlg.Port);
            helper.Start();
            _rendezvousHelper = helper;
            RendezvousHelperStartMenuItem.IsEnabled = false;
            RendezvousHelperStopMenuItem.IsEnabled = true;
            CollabStatusText.Text = $"仲介ヘルパー: 待機中(ポート{helper.Port})";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"仲介ヘルパーの開始に失敗しました: {ex.Message}", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RendezvousHelperStop_Click(object sender, RoutedEventArgs e)
    {
        if (_rendezvousHelper is null) return;
        await _rendezvousHelper.DisposeAsync();
        _rendezvousHelper = null;
        RendezvousHelperStartMenuItem.IsEnabled = true;
        RendezvousHelperStopMenuItem.IsEnabled = false;
        CollabStatusText.Text = _collab is null ? "共同編集: 未接続" : CollabStatusText.Text;
    }

    private async void RendezvousJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        if (_collab is not null)
        {
            MessageBox.Show(this, "既に共同編集セッションが開始されています。先に切断してください。", "共同編集", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Collab.RendezvousJoinDialog(this, Environment.UserName);
        if (dlg.ShowDialog() != true) return;

        var collab = new Collab.CollabSessionController(Dispatcher);
        AttachCollabUiHandlers(collab);
        try
        {
            await collab.JoinViaRendezvousAsync(_document, dlg.HelperAddress, dlg.HelperPort, dlg.SessionCode, dlg.DisplayName);
            _collab = collab;
            SetCollabMenuState(active: true);
            RefreshParticipantsPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"仲介ヘルパー経由の接続に失敗しました: {ex.Message}", "共同編集", MessageBoxButton.OK, MessageBoxImage.Error);
            await collab.DisposeAsync();
        }
    }

    private async void RendezvousAccept_Click(object sender, RoutedEventArgs e)
    {
        if (_collab is null || !_collab.IsHosting)
        {
            MessageBox.Show(this, "ホストとして開始していない状態では使用できません。", "仲介ヘルパー", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Collab.RendezvousAcceptDialog(this);
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _collab.AcceptViaRendezvousAsync(dlg.HelperAddress, dlg.HelperPort, dlg.SessionCode);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"仲介ヘルパー経由の参加受け入れに失敗しました: {ex.Message}", "共同編集", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>キーマクロ(Ctrl+Shift+1〜9、2026-07-26要望対応)の実行。指定スロットに登録された
    /// 手順を先頭から順に実行する。未登録スロットは何もしない。</summary>
    private void RunKeyMacro(int slot)
    {
        var def = _appSettings.KeyMacros.FirstOrDefault(m => m.Slot == slot);
        if (def is null || def.Steps.Count == 0) return;

        foreach (var step in def.Steps)
        {
            switch (step.Kind)
            {
                case KeyMacroStepKind.SetPlaybackSpeed:
                    _appSettings.PlaybackSpeed = step.Value;
                    _appSettings.Save(AppPaths.SettingsFilePath);
                    _audioPlayer.SpeedRatio = step.Value;
                    _suppressPlaybackSpeedComboEvent = true;
                    PlaybackSpeedCombo.SelectedItem = PlaybackSpeedValues_Nearest(step.Value);
                    _suppressPlaybackSpeedComboEvent = false;
                    break;
                case KeyMacroStepKind.SetPlaybackStartSeconds:
                    if (_document is not null)
                    {
                        _document.CurrentTab.PlaybackStartFrame = step.Value * 60.0;
                        _document.NotifyChanged();
                        InvalidateChartViews();
                    }
                    break;
                case KeyMacroStepKind.StartVisualTest:
                    if (_document is not null && !_visualTestActive) StartVisualTest();
                    break;
                case KeyMacroStepKind.StartPlaytest:
                    StartPlaytest();
                    break;
            }
        }
    }

    /// <summary>dos.txtエクスポート(2026-08-08要望対応: 保存ダイアログのファイル種類欄で
    /// UTF-8/Shift-JISを選べるようにする)。FilterIndexは1始まりで、Filter文字列の並び順と対応する
    /// (1番目=UTF-8を既定にし、従来の挙動を変えない)。
    /// Shift-JISへ変換できない文字(絵文字等)が含まれる場合は事前に確認ダイアログを出し、
    /// OKなら「?」へ置換して保存する(DosTextEncoding参照、ユーザー確認済みの方針)。</summary>
    private void ExportDos_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから出力する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "dos.txt - UTF-8 (*.txt)|*.txt|dos.txt - Shift-JIS (*.txt)|*.txt",
            FilterIndex = 1, // 既定はUTF-8(従来通り)
            FileName = "dos.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var exporter = new DosExporter(_templates.Get);
            var text = exporter.Export(_document.Project, includeEditorMetadata: true);
            bool useShiftJis = dlg.FilterIndex == 2;

            if (useShiftJis)
            {
                var unmappable = DosTextEncoding.FindUnmappableChars(text);
                if (unmappable.Count > 0)
                {
                    string sample = string.Join(" ", unmappable.Take(20));
                    string more = unmappable.Count > 20 ? " …" : "";
                    var confirm = MessageBox.Show(this,
                        $"Shift-JISへ変換できない文字が{unmappable.Count}種類見つかりました: {sample}{more}\n" +
                        "該当箇所は「?」に置き換えて保存しますが、よろしいですか？",
                        "文字コード変換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.OK) return;
                }
                File.WriteAllBytes(dlg.FileName, DosTextEncoding.EncodeShiftJisWithReplacement(text));
            }
            else
            {
                File.WriteAllText(dlg.FileName, text); // 従来通りUTF-8(BOM無し)
            }

            StatusText.Text = $"エクスポートしました: {Path.GetFileName(dlg.FileName)}";
            _appSettings.StatDosExportCount++; // 2026-07-26: 統計情報(dosエクスポート回数)
            _appSettings.Save(AppPaths.SettingsFilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>合作用途(2026-07-23、TBD 5): カレント難易度タブ1つだけをITTNエディタ形式のタブファイルとして
    /// 書き出す。合作相手は「ITTNエディタのタブファイルをインポート」/D&Dで自分のプロジェクトへタブ追加できる。</summary>
    private void ExportCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから出力する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tab = _document.CurrentTab;
        var dlg = new SaveFileDialog
        {
            Filter = "ITTNエディタ タブファイル (*.json)|*.json",
            FileName = $"{_document.Project.ProjectName}_{tab.DifficultyName}.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            ProjectSerializer.SaveTabExport(_document.Project, tab, dlg.FileName);
            StatusText.Text = $"エクスポートしました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>合作用途(2026-08-22要望対応): カレント難易度タブ1つだけを対象に、dos.txtのデータ行
    /// (note/freeze/ncolor/speed/boost/word_data)のみをテキストとして書き出す。既存dos.txtへの
    /// 手動貼り付け用スニペットのため、プロジェクト共通ヘッダーやJSラッパーは含まない。
    /// サフィックス番号(dataName{N}_data等の{N}部分)はDosSuffixExportDialogで都度選ばせる
    /// (単体タブ出力のためプロジェクト内の並び順から自動採番できないことによる)。</summary>
    private void ExportCurrentTabDos_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから出力する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var choice = DosSuffixExportDialog.Ask(this);
        if (choice is null) return; // キャンセル

        var tab = _document.CurrentTab;
        string text;
        try
        {
            var exporter = new DosExporter(_templates.Get);
            text = exporter.ExportSingleTab(_document.Project, tab, choice.Value.SuffixNumber);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string suffixLabel = choice.Value.SuffixNumber?.ToString() ?? "";
        var dlg = new SaveFileDialog
        {
            Filter = "dos.txt - UTF-8 (*.txt)|*.txt|dos.txt - Shift-JIS (*.txt)|*.txt",
            FilterIndex = 1, // 既定はUTF-8(従来通り)
            FileName = $"{_document.Project.ProjectName}_{tab.DifficultyName}_dos{suffixLabel}.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            bool useShiftJis = dlg.FilterIndex == 2;
            if (useShiftJis)
            {
                var unmappable = DosTextEncoding.FindUnmappableChars(text);
                if (unmappable.Count > 0)
                {
                    string sample = string.Join(" ", unmappable.Take(20));
                    string more = unmappable.Count > 20 ? " …" : "";
                    var confirm = MessageBox.Show(this,
                        $"Shift-JISへ変換できない文字が{unmappable.Count}種類見つかりました: {sample}{more}\n" +
                        "該当箇所は「?」に置き換えて保存しますが、よろしいですか？",
                        "文字コード変換の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.OK) return;
                }
                File.WriteAllBytes(dlg.FileName, DosTextEncoding.EncodeShiftJisWithReplacement(text));
            }
            else
            {
                File.WriteAllText(dlg.FileName, text); // 従来通りUTF-8(BOM無し)
            }

            StatusText.Text = $"エクスポートしました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>現在の難易度タブをSKBエディタ保存ファイル(JSON)形式へエクスポートする
    /// (2026-08-03要望対応)。SkbExporter参照。</summary>
    private void ExportSkb_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから出力する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2026-09-07要望対応: 「グリッドに乗らないオブジェクトの扱い」ダイアログは、実際に丸め・削除の
        // 対象が存在する場合のみ表示する。まず既定(丸める)でエクスポートを試み、警告(=丸め・削除が
        // 発生した対象)が1件も無ければそのままダイアログを出さずに使う。1件でもあれば従来通り
        // ダイアログで選ばせ、「削除する」が選ばれた場合のみその設定で再実行する
        // (「丸める」ならこの時点の結果をそのまま使い回せるため再実行は不要)。
        SkbExportResult result;
        try
        {
            result = SkbExporter.Export(_document.Project, _document.CurrentTab, _document.CurrentTemplate,
                new SkbExportOptions { RoundMisalignedBpmEvents = true, RoundMisalignedPositions = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var round = GridMismatchPolicyDialog.Ask(this, "SKBエディタへエクスポート");
            if (round is null) return; // キャンセル

            if (round == false)
            {
                try
                {
                    result = SkbExporter.Export(_document.Project, _document.CurrentTab, _document.CurrentTemplate,
                        new SkbExportOptions { RoundMisalignedBpmEvents = false, RoundMisalignedPositions = false });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        var dlg = new SaveFileDialog
        {
            Filter = "SKBエディタ保存ファイル (*.json)|*.json",
            FileName = $"{_document.Project.ProjectName}_{_document.CurrentTab.DifficultyName}_skb.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, result.Json);
            StatusText.Text = $"SKBエディタ形式でエクスポートしました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.Warnings.Count > 0)
            MessageBox.Show(this,
                "エクスポートは完了いたしましたが、以下の点をご確認ください。\n\n" +
                string.Join("\n\n", result.Warnings.Select(w => "・" + w)),
                "エクスポート完了(要確認)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>現在の難易度タブをFUJIエディタ形式(テキスト)へエクスポートする
    /// (2026-08-03要望対応)。FujiExporter参照。</summary>
    private void ExportFuji_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから出力する(CommitPendingEdits参照)
        if (_document is null)
        {
            MessageBox.Show(this, "プロジェクトが開かれていません。", "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2026-09-07要望対応: SKB側と同じく、丸め・削除の対象が実際に存在する場合のみダイアログを出す。
        FujiExportResult result;
        try
        {
            result = FujiExporter.Export(_document.Project, _document.CurrentTab, _document.CurrentTemplate,
                new FujiExportOptions { RoundMisalignedPositions = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.Warnings.Count > 0)
        {
            var round = GridMismatchPolicyDialog.Ask(this, "FUJIエディタへエクスポート");
            if (round is null) return; // キャンセル

            if (round == false)
            {
                try
                {
                    result = FujiExporter.Export(_document.Project, _document.CurrentTab, _document.CurrentTemplate,
                        new FujiExportOptions { RoundMisalignedPositions = false });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        var dlg = new SaveFileDialog
        {
            Filter = "FUJIエディタファイル (*.txt)|*.txt",
            FileName = $"{_document.Project.ProjectName}_{_document.CurrentTab.DifficultyName}_fuji.txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, result.Text);
            StatusText.Text = $"FUJIエディタ形式でエクスポートしました: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.Warnings.Count > 0)
            MessageBox.Show(this,
                "エクスポートは完了いたしましたが、以下の点をご確認ください。\n\n" +
                string.Join("\n\n", result.Warnings.Select(w => "・" + w)),
                "エクスポート完了(要確認)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =====================================================================
    // FUJI / SKB インポート
    // =====================================================================

    /// <summary>インポートウィンドウを開く(2026-07-30要望対応)。モードレスなので開いたまま
    /// 何度でも続けてFUJI D&D・SKB貼り付けインポートができる。</summary>
    private ImportHubWindow? _importHubWindow;
    private void OpenImportHubWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_importHubWindow is { IsLoaded: true })
        {
            _importHubWindow.Activate();
            return;
        }
        _importHubWindow = new ImportHubWindow(this);
        _importHubWindow.Show();
    }

    private void ImportFuji_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "FUJIエディタファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImportFujiFile(dlg.FileName);
    }

    /// <summary>FUJIエディタファイルをインポートする。ImportFuji_ClickとD&D(2026-07-20)の共通処理。
    /// 2026-07-26再設計(ユーザー確定仕様、difDataへの参照範囲縮小+「1回で済ませたい」): キー種
    /// (difDataからは自動検出せずテンプレートフォルダの一覧から選択)と難易度名(選んだキー種に一致する
    /// difData候補+「後で設定する」「今設定する」)を、1つのウィンドウ(FujiImportSetupDialog)でまとめて
    /// 選ばせる。「後で設定する」を選んだ場合も、それ自体は正常な選択のため確認ダイアログは出さない。</summary>
    internal void ImportFujiFile(string path)
    {
        var fileName = Path.GetFileName(path);
        string text;
        bool wasShiftJis;
        try { (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(File.ReadAllBytes(path)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var allCandidates = FujiImporter.ScanDifData(text);
        var setup = FujiImportSetupDialog.Ask(this, _templates, allCandidates, fileName);
        if (setup is null) return; // キャンセル
        var (keyTypeId, nameChoice) = setup.Value;

        try
        {
            var importer = new FujiImporter(_templates.Get);
            var result = importer.Import(text, keyTypeId);

            // FujiImporter.Import内部の暫定的なdifData自動反映(difDataに1件だけ一致した場合等)は
            // ここで上のダイアログで選んだ内容によって常に上書きする(SetLaterなら空のまま)。
            result.Tab.DifficultyName = nameChoice.Name;
            if (nameChoice.InitialSpeed is { } sp) result.Tab.InitialSpeed = sp;
            // Import内部生成分の「難易度名を特定できません」警告はダイアログで解決済みのため取り除く
            // (「後で設定する」を選んだ場合も、それは意図した選択であって警告すべき問題ではない)。
            result.Warnings.RemoveAll(w => w.Contains("難易度名を特定できません"));

            var project = ChooseImportTargetProject(fileName);
            if (project is null) return; // インポート先の選択をキャンセル
            var warnings = ProjectOperations.ApplyImport(project, result);
            // 2026-08-09要望対応: 文字コード自動判定でShift-JISと判定された場合のみ、その旨を伝える
            // (UTF-8として読めた場合は従来通り無言、判定が働いた場合だけ知らせる方針)。
            if (wasShiftJis)
                warnings.Insert(0, "文字コードをUTF-8として読み込めなかったため、Shift-JISとして自動判定して読み込みました。");
            FinishTabImport(project, warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"FUJIインポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSkb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SKBエディタファイル (*.txt;*.json)|*.txt;*.json|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImportSkbFile(dlg.FileName);
    }

    /// <summary>SKBエディタファイルをインポートする。ImportSkb_ClickとD&D(2026-07-20)の共通処理。
    /// ファイルを読み込んでからImportSkbText(2026-07-30、インポートウィンドウ新設に伴い抽出)へ委譲する。</summary>
    private void ImportSkbFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ImportSkbText(text, Path.GetFileName(path));
    }

    /// <summary>SKBエディタ形式のテキストをインポートする(2026-07-30、インポートウィンドウの
    /// コピペ欄用に新設)。ImportSkbFileと共通のコア処理で、ファイルパスの有無だけが異なる。
    /// displayNameはインポート先選択・難易度名入力ダイアログの案内文にのみ使う表示用の名前
    /// (ファイルの場合はファイル名、貼り付けの場合は「クリップボードからの貼り付け」等)。</summary>
    internal void ImportSkbText(string text, string displayName)
    {
        try
        {
            var importer = new SkbImporter(_templates.Get);
            var result = importer.Import(text);

            var name = SimplePrompt.Ask(this, "難易度名の指定",
                $"インポート中のデータ: {displayName}\n\nSKB形式には難易度名が保存されていないため、手動で入力してください。", "Normal");
            if (!string.IsNullOrWhiteSpace(name)) result.Tab.DifficultyName = name;

            var project = ChooseImportTargetProject(displayName);
            if (project is null) return; // インポート先の選択をキャンセル
            var warnings = ProjectOperations.ApplyImport(project, result);
            FinishTabImport(project, warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"SKBインポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportDos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "dos.txt (*.txt)|*.txt|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImportDosFile(dlg.FileName);
    }

    /// <summary>dos.txtをインポートする。ImportDos_ClickとD&Dの共通処理。
    /// 2026-07-25: FUJI/SKB/タブファイルインポートとインポート先の選択フローを統一(ユーザー要望
    /// 「インポートのフローを共通にしてほしい」)。以前(2026-07-20)は「dos.txtインポートは単体で
    /// プロジェクト全体(タブ複数を含む)を作るため」既存プロジェクトへの追加を考慮せず常に新規
    /// プロジェクトタブとしていたが、他形式と同様プロジェクトが開いていれば追加/新規を選ばせるべき
    /// という指摘のため、ChooseImportTargetProject/ApplyImport(DosImportResult)/FinishTabImportの
    /// 共通トリオへ揃えた(dos.txt1件で複数タブを含み得る点はApplyImport側で全タブ追加として吸収)。
    /// 2026-07-30: インポート先の選択(新規/既存プロジェクトへタブ追加)を先に行うよう順序変更。
    /// 既存プロジェクトへタブとして取り込む場合、BPMは(「新規譜面を追加」と同様)プロジェクト
    /// 全体で共通の値を使うべきであり、ダイアログでの自動推定確認自体が不要(推定を行うと
    /// プロジェクト共通のBPMと食い違う値になり得るため)。そのためタブ追加時はBPM自動推定の
    /// 確認ダイアログを出さず、タイミング情報が無い場合のフォールバック値としてプロジェクトの
    /// 先頭BPMイベント値をそのまま使う(新規プロジェクトの場合は従来通り、確認ダイアログの上で
    /// 環境設定の既定BPMをフォールバックに使う)。</summary>
    private void ImportDosFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var project = ChooseImportTargetProject(fileName);
        if (project is null) return; // インポート先の選択をキャンセル
        bool addingToExistingTab = _document is not null && ReferenceEquals(project, _document.Project);

        bool autoEstimate;
        double defaultBpm;
        (double StartNumber, IReadOnlyList<BpmEvent> BpmEvents)? timingOverride = null;
        if (addingToExistingTab)
        {
            autoEstimate = false;
            defaultBpm = project.BpmEvents.Count > 0 ? project.BpmEvents[0].Bpm : _appSettings.DefaultBpm;
        }
        else
        {
            // 2026-08-02要望対応: 新規プロジェクト作成時のみ、BPMを自動検出/手動入力のどちらにするか
            // 冒頭で選ばせる。手動入力ならDosImportOptions.TimingOverrideへ渡し最優先で採用させる。
            var choice = DosBpmSetupDialog.Ask(this, _appSettings.DefaultBpm);
            if (choice is null) return; // キャンセル
            autoEstimate = choice.Value.AutoEstimate;
            defaultBpm = _appSettings.DefaultBpm; // 環境設定(仕様書15.2、2026-07-19b)
            if (choice.Value.ManualBpm is { } manualBpm)
                timingOverride = (StartNumber: 0, BpmEvents: (IReadOnlyList<BpmEvent>)[new BpmEvent(0, manualBpm)]);
        }

        try
        {
            var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(File.ReadAllBytes(path));
            var importer = new DosImporter(_templates.Get);
            var options = new DosImportOptions { AutoEstimateTiming = autoEstimate, DefaultBpm = defaultBpm, TimingOverride = timingOverride };
            var result = importer.Import(text, options);

            int addedTabCount = result.Project.Tabs.Count;
            var warnings = ProjectOperations.ApplyImport(project, result);
            warnings.Add($"タイミング情報の出所: {result.TimingSource} / 最大スナップ誤差: {result.MaxSnapErrorFrames:F2}フレーム");
            // 2026-08-09要望対応: 文字コード自動判定でShift-JISと判定された場合のみその旨を伝える。
            if (wasShiftJis)
                warnings.Insert(0, "文字コードをUTF-8として読み込めなかったため、Shift-JISとして自動判定して読み込みました。");
            FinishTabImport(project, warnings, addedTabCount);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"dos.txtインポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportTabExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ITTNエディタ タブファイル (*.json)|*.json|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImportTabExportFile(dlg.FileName);
    }

    /// <summary>ITTNエディタ形式のタブファイル(合作用、2026-07-23、TBD 5)をインポートする。
    /// ImportTabExport_ClickとD&Dの共通処理。FUJI/SKBインポートと同じくChooseImportTargetProject/
    /// FinishTabImportを流用する(現在のプロジェクトへ追加/新規プロジェクトとして/キャンセルを選べる)。</summary>
    private void ImportTabExportFile(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var result = ProjectSerializer.LoadTabExport(path);

            var project = ChooseImportTargetProject(fileName);
            if (project is null) return; // インポート先の選択をキャンセル
            var warnings = ProjectOperations.ApplyImport(project, result);
            FinishTabImport(project, warnings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"タブファイルのインポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// FUJI/SKBインポート先を選ばせる(2026-07-20: マルチプロジェクトタブ対応)。
    /// セッションが1つも無ければ問答無用で新規プロジェクト(選択の余地が無いため)。
    /// それ以外は「現在のプロジェクトに追加」か「新しいプロジェクトとして」かを尋ねる。
    /// キャンセル時はnullを返す(呼び出し元はインポート自体を中止すること)。
    /// 空のEditorDocumentは作らない(EditorDocumentはタブ0件だと構築時に例外を投げる仕様のため、
    /// 先にProjectOperations.ApplyImportでタブを追加してからEditorDocumentを作る順序を守ること)。
    /// </summary>
    private ChartProject? ChooseImportTargetProject(string fileName)
    {
        if (_sessions.Count == 0) return new ChartProject { ProjectName = "untitled" };

        var choice = MessageBox.Show(this,
            $"「{fileName}」を現在のプロジェクトに追加しますか?\n\n" +
            "「はい」= 現在のプロジェクトに追加\n「いいえ」= 新しいプロジェクトとしてインポート\n「キャンセル」= インポートを中止",
            "インポート先の選択", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice switch
        {
            MessageBoxResult.Yes => _document!.Project,
            MessageBoxResult.No => new ChartProject { ProjectName = "untitled" },
            _ => null,
        };
    }

    /// <summary>
    /// ApplyImport後の後始末。projectは呼び出し元がChooseImportTargetProject()で取得し、
    /// 実際にApplyImportへ渡したのと同一の参照でなければならない(取り違えるとインポートしたタブが
    /// 見えなくなる)。projectが現在アクティブなセッションのものと異なる(=新しいプロジェクトとして
    /// インポートされた)場合はここで新規セッションとして追加し、同一の場合はUndo履歴・選択状態を
    /// 維持したまま画面だけ更新する。
    /// addedTabCountは今回のApplyImportで新たに追加されたタブの件数。FUJI/SKB/タブファイルは常に1件だが、
    /// dos.txt(2026-07-25でこの共通フローに統合)は1ファイルに複数難易度タブを含み得るため、
    /// 「末尾のタブ」ではなく「今回追加された先頭のタブ」を選択する(末尾固定だと複数タブ追加時に
    /// 意味の薄い最後のタブが選ばれてしまうため)。
    /// </summary>
    private void FinishTabImport(ChartProject project, List<string> warnings, int addedTabCount = 1)
    {
        bool isNewProject = _document is null || !ReferenceEquals(_document.Project, project);
        if (isNewProject)
        {
            AddSession(new EditorDocument(project, _templates), null);
        }
        else
        {
            OpenDocument(_document!, _controller); // タブ一覧の再読込のみ。既存コントローラ・選択状態はそのまま
        }
        int newTabCount = Math.Max(1, addedTabCount);
        _document!.CurrentTabIndex = Math.Max(0, _document.Project.Tabs.Count - newTabCount);

        // 2026-07-16d バグ修正: OpenDocument内のDifficultyTabControl.SelectedIndex設定は、
        // 上のCurrentTabIndex代入より「前」に(古いCurrentTabIndexを使って)行われてしまうため、
        // ここで改めてUI側のタブ選択をモデルの最終値(=インポートされたタブ)に合わせないと、
        // 見た目だけ1つ前のタブのままになってしまう(モデルは正しく最終タブを指している)。
        _suppressSelectionEvent = true;
        DifficultyTabControl.SelectedIndex = _document.CurrentTabIndex;
        _suppressSelectionEvent = false;

        ReportImportWarnings(warnings);
    }

    private void ReportImportWarnings(List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            StatusText.Text = "インポート完了(警告なし)";
            return;
        }
        StatusText.Text = $"インポート完了({warnings.Count}件の警告あり)";
        MessageBox.Show(this, string.Join("\n\n", warnings), "インポート時の警告", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // =====================================================================
    // ドキュメントの画面反映
    // =====================================================================

    /// <summary>
    /// docを画面に反映する。controllerを渡した場合はそれを再利用する(2026-07-20: マルチプロジェクトタブで
    /// セッション切替時に色編集モード等のコントローラ状態を保つため)。省略時(新規プロジェクト/インポート等)は
    /// 新しいコントローラを作る。
    /// </summary>
    private void OpenDocument(EditorDocument doc, SmartToolController? controller = null)
    {
        _document = doc;
        _controller = controller ?? new SmartToolController(doc);
        _pluginManager.NotifyDocumentChanged(); // 2026-07-26: プラグインへドキュメント切替を通知

        Canvas.Document = doc;
        Canvas.Controller = _controller;
        Minimap.Document = doc;
        Minimap.TargetScrollViewer = ChartScrollViewer;
        Minimap.FocusTarget = Canvas;
        SyncMinimap2FromDocument(); // 2026-07-26b: 分割ビュー中はMinimap2(右ペイン用)も同じdocへ切り替える
        // 2026-07-23: 色編集モードのON/OFF・塗り色はセッションを跨いで保持する仕様のため、
        // アクティブになったコントローラへ都度反映する(コントローラ自体はセッションごとに使い回される)。
        // 2026-07-24: サブモード(Normal/FrzHit/Shadow)関連の状態もまとめてPushColorEditStateToControllerへ集約。
        PushColorEditStateToController();
        // 2026-07-21: SKB操作モードのコントローラもドキュメントごとに作り直す(同時押し判定・未完了フリーズは
        // 一時的な状態でよく、セッション間で引き継ぐ必要が無いため)。ON状態自体はセッションを跨いで保持する。
        _keyboardMode = new KeyboardModeController(doc) { ThresholdMs = _appSettings.SimultaneousPressThresholdMs };
        if (_keyboardModeActive) _keyboardMode.EnterMode();
        FrameEditToggle.IsChecked = false; // 新ドキュメントは拍情報モードから(仕様書7.6、2026-07-17i)
        doc.UndoCapacity = Math.Max(1, _appSettings.UndoHistorySize); // 仕様書14章(2026-07-19b、2026-08-06: 全タブの履歴へ適用)
        UpdateWindowTitle();

        ProjectTitleText.Text = $"{doc.Project.ProjectName} ({doc.Project.MusicTitle})";

        _suppressSelectionEvent = true;
        DifficultyTabControl.ItemsSource = null;
        DifficultyTabControl.ItemsSource = doc.Project.Tabs;
        DifficultyTabControl.DisplayMemberPath = nameof(DifficultyTab.DisplayLabel);
        DifficultyTabControl.SelectedIndex = Math.Min(doc.CurrentTabIndex, doc.Project.Tabs.Count - 1);
        _suppressSelectionEvent = false;

        // 右パネル③(選択中オブジェクトのプロパティ)・タイトルバー・プロジェクトタブラベルはいずれも
        // Document.Changedを購読して追随する。同じdocインスタンスでOpenDocumentが再呼び出しされる
        // ケース(インポート後の再読込、セッション切替の往復)があるため、重複購読を避けて古いdocからは外す。
        if (!ReferenceEquals(_selectionSubscribedDoc, doc))
        {
            if (_selectionSubscribedDoc is not null)
            {
                _selectionSubscribedDoc.Changed -= RefreshSelectedObjectPanel;
                _selectionSubscribedDoc.Changed -= UpdateWindowTitle;
                _selectionSubscribedDoc.Changed -= RefreshProjectTabBarLabelOnly;
                _selectionSubscribedDoc.Changed -= SyncPreviewStartFrame;
                _selectionSubscribedDoc.StatRecorded -= OnStatRecorded;
            }
            doc.Changed += RefreshSelectedObjectPanel;
            doc.Changed += UpdateWindowTitle;
            doc.Changed += RefreshProjectTabBarLabelOnly;
            // 2026-07-29要望対応: 再生開始ラインを再設置した際、目視テスト中でなくても即座にプレビューへ
            // 反映する(レーンダブルクリック等、PlaybackStartFrameを変更するあらゆる操作がNotifyChangedを
            // 呼ぶため、ここで一括して拾える)。
            doc.Changed += SyncPreviewStartFrame;
            doc.StatRecorded += OnStatRecorded;
            _selectionSubscribedDoc = doc;
        }

        // 2026-08-02要望対応: スナップ分解能をプロジェクトファイルへ永続化し、次回オープン時に
        // 前回値を復元する(未設定の旧プロジェクトファイルは既定の16分にフォールバック)。
        // ここでコンボの選択値を書き換えてから、直後のApplySnapToDocumentでdoc.Snap側へ反映させる。
        SnapDivisionCombo.SelectedItem = doc.Project.SnapDivision ?? 16;
        ApplySnapToDocument();

        // 2026-08-02要望対応: 再生音量(目視テスト・プレイテスト共通)をプロジェクトファイルへ
        // 永続化し、次回オープン時に前回値を復元する(音源によって適正音量が異なるため)。
        // 未設定の旧プロジェクトファイルはAppSettings.PlaybackVolume(エディタ全体の既定値)へ
        // フォールバックする。ApplyVolumePercentは使わない(呼ぶとAppSettings側も上書き保存されてしまい、
        // 「直前に開いたプロジェクトの音量がエディタ全体の既定値になる」という意図しない副作用が
        // 出るため、ここではUI表示とMediaPlayerへの反映のみ行う)。
        double restoredVolume = doc.Project.PlaybackVolume ?? _appSettings.PlaybackVolume;
        _suppressVolumeEvents = true;
        VolumeSlider.Value = Math.Clamp(restoredVolume, 0.0, 1.0) * 100;
        VolumeBox.Text = Math.Round(VolumeSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _suppressVolumeEvents = false;
        _audioPlayer.Volume = restoredVolume;

        ResetAudioForDocument(doc);
        RefreshProjectPropertiesPanel();
        RefreshSelectedObjectPanel();
        RefreshColorPanel();
        RefreshExtraHeadersPanel();
        RefreshMacroList(); // 2026-07-26: 現在タブのKeyTypeIdに応じて「実行」ボタンの有効/無効が変わるため
        RefreshLinkPanel(); // 2026-07-26: タブリンクパネルも同様に最新化する
        RefreshAnalysisPanel(); // 2026-07-26: 分析タブ(ITTNアナライザー/おにスター)
        InvalidateChartViews(); // 2026-07-26: 分割ビュー中はCanvas2(右ペイン)へもDocument/Controllerを反映する
    }

    /// <summary>プロジェクトタブの表示ラベル(未保存マーカー"*")をDocument.Changedのたびに更新する。
    /// ProjectSession.TabLabelはDifficultyTab等と同じくINotifyPropertyChanged非対応のため明示リフレッシュ。</summary>
    private void RefreshProjectTabBarLabelOnly() => ProjectTabControl.Items.Refresh();

    /// <summary>2026-08-09要望対応: 難易度タブ/プロジェクトタブを常に1行へ収める。標準TabPanelは
    /// 幅が足りないと2行目以降へ折り返すが、ヘッダー行の高さが固定(DockPanel Height=28)のため
    /// 2行目が見えなくなってしまう不具合があった。折り返しを起こさせないよう、タブ数と使える幅から
    /// 1タブあたりの幅を計算し、全TabItemへ明示的に設定する(タブが少ない間は自然な見た目を保つよう
    /// 上限MaxTabWidthも設ける)。TabItemのコンテナはItemsSource差し替え直後にはまだ生成されていない
    /// ことがあるため、呼び出し側はItemContainerGenerator.StatusChanged(ContainersGenerated)と
    /// SizeChangedの両方から呼ぶ(タブ数変化・ヘッダー幅変化のどちらにも追随するため)。</summary>
    private const double MaxTabHeaderWidth = 150;

    private static void AdjustTabHeaderWidths(TabControl tabControl)
    {
        int count = tabControl.Items.Count;
        if (count == 0) return;
        double available = tabControl.ActualWidth;
        if (available <= 0) return; // レイアウト確定前。後続のSizeChanged/StatusChangedで再計算される

        // タブ間の枠線・余白ぶんの安全マージンを差し引いた上で均等割りし、既定の最大幅(MaxTabHeaderWidth)
        // でも頭打ちにする(タブが少ない間は不必要に幅いっぱいへ間延びさせない)。
        double perTab = Math.Max(1, Math.Floor((available - 8) / count) - 4);
        double width = Math.Min(MaxTabHeaderWidth, perTab);

        for (int i = 0; i < count; i++)
        {
            if (tabControl.ItemContainerGenerator.ContainerFromIndex(i) is TabItem item)
                item.Width = width;
        }
    }

    // =====================================================================
    // マルチプロジェクトタブ(仕様書TBD#10、2026-07-20)
    // =====================================================================

    /// <summary>新しいプロジェクトをセッションとして追加し、アクティブにする。New/Open/dos.txtインポート/
    /// 自形式D&D、および「新しいプロジェクトとしてインポート」を選んだFUJI/SKBインポート等、
    /// 「丸ごと新しいプロジェクト」を作る全ての経路で使う。</summary>
    private void AddSession(EditorDocument doc, string? filePath)
    {
        SyncActiveSessionBeforeSwitch();
        OpenDocument(doc); // 新規docなのでコントローラも新規生成
        _currentFilePath = filePath;
        _sessions.Add(new ProjectSession { Document = doc, Controller = _controller!, FilePath = filePath });
        _activeSessionIndex = _sessions.Count - 1;
        RefreshProjectTabBar();
    }

    /// <summary>アクティブセッションを切り替える直前に、現在表示中の実行時状態(コントローラ・保存パス)を
    /// 元のセッションへ書き戻す(読み直し時に正しく復元できるように)。セッション未保持時は何もしない。</summary>
    private void SyncActiveSessionBeforeSwitch()
    {
        if (_activeSessionIndex < 0 || _activeSessionIndex >= _sessions.Count) return;
        var s = _sessions[_activeSessionIndex];
        if (_controller is not null) s.Controller = _controller;
        s.FilePath = _currentFilePath;
    }

    /// <summary>プロジェクトタブの切替本体。切替前に目視テストを止め(セッションを跨いだ再生継続は
    /// 混乱を招くため強制終了)、現在の実行時状態を元のセッションへ書き戻してから、選択先セッションの
    /// ドキュメント・コントローラ・保存パスへ差し替える。</summary>
    private void ActivateSession(int index)
    {
        if (index < 0 || index >= _sessions.Count || index == _activeSessionIndex) return;
        if (_visualTestActive) StopVisualTest(returnToStart: false);
        SyncActiveSessionBeforeSwitch();
        _keyboardSelectionAnchorTick = null; // 2026-07-26: 他タブのtick基準を持ち越さない

        _activeSessionIndex = index;
        var s = _sessions[index];
        _currentFilePath = s.FilePath;
        OpenDocument(s.Document, s.Controller);
        RefreshProjectTabBar();
    }

    /// <summary>プロジェクトタブバー(ItemsSource)を選択位置ごと再構築する</summary>
    private void RefreshProjectTabBar()
    {
        _suppressProjectTabSelectionEvent = true;
        ProjectTabControl.ItemsSource = null;
        ProjectTabControl.ItemsSource = _sessions;
        ProjectTabControl.DisplayMemberPath = nameof(ProjectSession.TabLabel);
        ProjectTabControl.SelectedIndex = _activeSessionIndex;
        _suppressProjectTabSelectionEvent = false;
    }

    private void ProjectTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectTabSelectionEvent) return;
        if (ProjectTabControl.SelectedIndex < 0) return;

        CommitPendingEdits(); // 2026-08-06: 切替前に入力中の値を確定させる(下記CommitPendingEdits参照)
        ActivateSession(ProjectTabControl.SelectedIndex);
    }

    /// <summary>
    /// 入力途中の右パネル項目を「今」確定させる(2026-08-06不具合修正、洗い出し#2)。
    ///
    /// 右パネルの数値項目は「フォーカスが外れた時点で確定」する方式(ProjectNumericField_LostFocus等)の
    /// ため、テキストボックスにフォーカスが残ったままモデルを読み書きする操作(保存・エクスポート・
    /// タブ切替・プレイテスト開始など)を実行すると、LostFocusがその処理より後に発火し、
    /// 「入力した値が保存されない」「変更前タブへの編集が変更後タブへ書き込まれる」といった
    /// 不具合になる。モデルを読み取る直前に必ずこれを呼び、フォーカスを外してLostFocusを
    /// 同期的に先へ発火させることで、常に画面の入力内容とモデルが一致した状態にしてから処理へ進む。
    ///
    /// Keyboard.ClearFocus()だけでは論理フォーカス(WPFのFocusManager)が残るケースがあるため、
    /// ウィンドウ自身へ論理フォーカスを移してからキーボードフォーカスを解除する。
    /// </summary>
    private void CommitPendingEdits()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null) return;
        // 譜面ビュー等、そもそも入力欄でない要素にフォーカスがある場合は何もしない
        // (無用なフォーカス移動でキーボードモードの操作対象が変わってしまうのを避ける)。
        if (focused is not TextBox && focused is not ComboBox) return;
        FocusManager.SetFocusedElement(this, this);
        Keyboard.ClearFocus();
    }

    /// <summary>「プロジェクトを閉じる」ボタン。未保存なら個別に確認し、最後の1つを閉じた場合は
    /// 起動直後と同じ「プロジェクト無し」の空状態に戻す(2026-07-20ユーザー指定)。</summary>
    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSessionIndex < 0 || _document is null) return;

        if (_document.IsModified)
        {
            var name = string.IsNullOrWhiteSpace(_document.Project.ProjectName) ? "Untitled" : _document.Project.ProjectName;
            var confirm = MessageBox.Show(this, $"「{name}」に未保存の変更があります。閉じてよろしいですか?",
                "プロジェクトを閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        if (_visualTestActive) StopVisualTest(returnToStart: false);
        _audioPlayer.Stop();
        _playbackTimer.Stop();

        int closingIndex = _activeSessionIndex;
        AutoSaveManager.ClearSlot(AppPaths.AutoSaveDir, _sessions[closingIndex].SlotId); // 2026-07-25
        _sessions.RemoveAt(closingIndex);

        if (_sessions.Count == 0)
        {
            ResetToEmptyState();
            return;
        }

        _activeSessionIndex = Math.Min(closingIndex, _sessions.Count - 1);
        var s = _sessions[_activeSessionIndex];
        _currentFilePath = s.FilePath;
        OpenDocument(s.Document, s.Controller);
        RefreshProjectTabBar();
    }

    /// <summary>プロジェクトが1つも無い状態(起動直後と同じ)へ戻す(CloseProject_Clickで最後の
    /// 1タブを閉じた場合専用)。</summary>
    private void ResetToEmptyState()
    {
        _activeSessionIndex = -1;
        _document = null;
        _controller = null;
        _keyboardMode = null;
        _currentFilePath = null;
        _pluginManager.NotifyDocumentChanged(); // 2026-07-26: プラグインへ「プロジェクト無し」を通知

        Canvas.Document = null;
        Canvas.Controller = null;
        Canvas.Waveform = null;
        Canvas.PlaybackTick = null;
        Minimap.Document = null;
        if (_splitViewEnabled) Minimap2.Document = null; // 2026-07-26b: 右ペイン用ミニマップもクリア
        InvalidateChartViews(); // 2026-07-26: 分割ビュー中はCanvas2(右ペイン)側もまとめてクリアする

        ProjectTitleText.Text = "(プロジェクト未作成)";
        _suppressSelectionEvent = true;
        DifficultyTabControl.ItemsSource = null;
        _suppressSelectionEvent = false;

        AudioFileText.Text = "音楽未読込";
        AudioFileText.FontStyle = FontStyles.Italic;
        AudioTimeText.Text = "-";
        _audioLoaded = false;
        _musicUrlDirty = false;
        MusicUrlLoadButton.IsEnabled = false;

        SetColorPanel.Children.Clear();
        FrzColorPanel.Children.Clear();
        ExtraHeadersPanel.Children.Clear();

        UpdateWindowTitle();
        RefreshProjectTabBar();
        RefreshMacroList(); // 2026-07-26: ドキュメント無しの間は一覧を空にし「実行」を無効化する
        RefreshLinkPanel(); // 2026-07-26: タブリンクパネルも同様に空にする
    }

    // =====================================================================
    // 音楽ファイル読み込み/再生(目テスト・プレイテスト用)
    // =====================================================================

    /// <summary>ドキュメントを開き直した時の音楽状態リセット。プロジェクトに保存済みパスがあれば自動読込を試みる</summary>
    private void ResetAudioForDocument(EditorDocument doc)
    {
        _visualTestActive = false; // ドキュメント切替時は目視テストを強制終了(2026-07-17g)
        _loopPlaybackEnabled = false; // 2026-07-29要望対応: ドキュメント切替時はループ再生も強制OFF
        LoopPlaybackToggle.IsChecked = false;
        _playbackTimer.Stop();
        _audioPlayer.Stop();
        Canvas.PlaybackTick = null;
        _waveformPeaks = null; // 波形キャッシュは曲に紐づくためクリア(2026-07-18)
        _waveformPath = null;
        Canvas.Waveform = null;
        Canvas.AudioTotalFrames = null; // 2026-07-26: 曲切替時はいったんクリア(未読込なら8小節下限に戻る)
        Minimap.AudioTotalFrames = null;
        if (_splitViewEnabled) Minimap2.AudioTotalFrames = null;

        if (!string.IsNullOrEmpty(doc.Project.AudioFilePath) && File.Exists(doc.Project.AudioFilePath))
        {
            LoadAudioFile(doc.Project.AudioFilePath);
        }
        else
        {
            AudioFileText.Text = "音楽未読込";
            AudioFileText.FontStyle = FontStyles.Italic;
            _audioLoaded = false;
            AudioTimeText.Text = "-";
        }
    }

    private void LoadAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            MessageBox.Show(this, "先にプロジェクトを作成/読み込みしてください。", "音楽ファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "音楽ファイル (*.mp3;*.wav;*.wma;*.ogg)|*.mp3;*.wav;*.wma;*.ogg|すべてのファイル (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        LoadAudioFile(dlg.FileName);
    }

    private void LoadAudioFile(string path)
    {
        try
        {
            Canvas.AudioTotalFrames = null; // 2026-07-26: 読込完了(MediaOpened)まではいったんクリア
            Minimap.AudioTotalFrames = null;
            if (_splitViewEnabled) Minimap2.AudioTotalFrames = null;
            _audioPlayer.Open(path);
            _document!.Project.AudioFilePath = path;
            AudioFileText.Text = Path.GetFileName(path);
            AudioFileText.FontStyle = FontStyles.Normal;
            _audioLoaded = true;
            if (WaveformToggle.IsChecked == true) EnsureWaveformDecoded(); // 2026-07-18
            UpdateMusicUrlLoadButtonState(); // 2026-07-26: 読込完了で「読込」ボタンをグレーアウトする
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"音楽ファイルの読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>2026-07-26: Open()は非同期のため、実際に全体長(Duration)が判明したタイミングで拾って
    /// ChartCanvas/ChartMinimapへ反映する(ノート未配置でも曲の長さぶんスクロールできるようにするための値)。</summary>
    private void AudioPlayer_MediaOpened()
    {
        double? totalFrames = _audioPlayer.Duration is { } d ? d.TotalSeconds * 60.0 : null;
        Canvas.AudioTotalFrames = totalFrames;
        Minimap.AudioTotalFrames = totalFrames;
        if (_splitViewEnabled) Minimap2.AudioTotalFrames = totalFrames;
        Canvas.InvalidateMeasure();
        if (_splitViewEnabled) Canvas2.InvalidateMeasure(); // 2026-07-26: 分割ビュー中は右ペインも再計測
        InvalidateChartViews();
        Minimap.InvalidateVisual();
        if (_splitViewEnabled) Minimap2.InvalidateVisual();
    }

    // =====================================================================
    // musicURLからの楽曲取得(2026-07-26確定仕様)。環境設定でON時のみ有効。指定フォルダを
    // カレントディレクトリとして扱い、そこからProject.MusicUrlのファイル名で楽曲を読み込む。
    // =====================================================================

    /// <summary>「読込」ボタンの活性状態を更新する。機能OFF・楽曲読込済み・未編集のいずれかでグレーアウト。</summary>
    private void UpdateMusicUrlLoadButtonState()
    {
        MusicUrlLoadButton.IsEnabled =
            _document is not null && _appSettings.MusicUrlAutoLoadEnabled && !_audioLoaded && _musicUrlDirty;
    }

    private void MusicUrlLoadButton_Click(object sender, RoutedEventArgs e) => TryLoadMusicFromUrl(autoTriggered: false);

    /// <summary>musicURL機能本体。autoTriggered=true(ITTNプロジェクトファイル読込時の自動読込)の場合は
    /// 邪魔にならないよう失敗してもダイアログを出さない(ステータスバー表示のみ)。手動("読込"ボタン)の
    /// 場合は原因をダイアログで知らせる。</summary>
    private bool TryLoadMusicFromUrl(bool autoTriggered)
    {
        if (_document is null) return false;
        if (!_appSettings.MusicUrlAutoLoadEnabled) return false;
        if (_audioLoaded) return false; // 既に読込済み(ローカルAudioFilePath復元含む)なら上書きしない

        var baseFolder = _appSettings.MusicUrlBaseFolder;
        if (string.IsNullOrWhiteSpace(baseFolder) || !Directory.Exists(baseFolder))
        {
            if (!autoTriggered)
                MessageBox.Show(this, "環境設定でmusicURL取得用の楽曲フォルダを指定してください。",
                    "musicURLからの読込", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var musicUrl = _document.Project.MusicUrl;
        if (string.IsNullOrWhiteSpace(musicUrl) || musicUrl == "noname")
        {
            if (!autoTriggered)
                MessageBox.Show(this, "musicURLが未設定です。", "musicURLからの読込", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var path = Path.Combine(baseFolder, musicUrl);
        if (!File.Exists(path))
        {
            if (autoTriggered) StatusText.Text = $"musicURLからの自動読込に失敗しました(ファイルが見つかりません: {path})";
            else MessageBox.Show(this, $"ファイルが見つかりませんでした:\n{path}", "musicURLからの読込", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        LoadAudioFile(path);
        _musicUrlDirty = false;
        UpdateMusicUrlLoadButtonState();
        return true;
    }

    // =====================================================================
    // D&Dによるファイル読み込み(仕様書TBD#7、2026-07-20)
    // =====================================================================

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// D&Dされたファイルを中身から自動判別し、対応するインポート/読込処理へ振り分ける。
    /// 拡張子だけでは判別できない形式(FUJI/SKB/dos.txtの.txt共有、自形式/SKBの.json共有)が
    /// あるため、DroppedFileClassifierで中身を見て判定する(実データ・公式wikiで確認済みのマーカー)。
    /// 楽曲ファイル(RawAudio/Base64Music)は「プロジェクトを開いていないと読み込めない」既存仕様
    /// (LoadAudioFile呼び出し前のnullチェック)があるため、他形式を先に処理してから最後に回す。
    /// これにより「プロジェクト系ファイル+楽曲ファイル」を一括ドロップした場合、前者で開かれた
    /// プロジェクトへ後者をそのまま読み込める。判別不能ファイルは個別インポートを促す。
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (paths.Length == 0) return;
        e.Handled = true;

        var immediate = new List<(string Path, DroppedFileKind Kind)>();
        var deferredAudio = new List<(string Path, DroppedFileKind Kind)>();
        var unknown = new List<string>();

        foreach (var path in paths)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"読み込みに失敗しました: {Path.GetFileName(path)}\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                continue;
            }

            var kind = DroppedFileClassifier.Classify(Path.GetFileName(path), bytes);
            switch (kind)
            {
                case DroppedFileKind.RawAudio:
                case DroppedFileKind.Base64Music:
                    deferredAudio.Add((path, kind));
                    break;
                case DroppedFileKind.Unknown:
                    unknown.Add(Path.GetFileName(path));
                    break;
                default:
                    immediate.Add((path, kind));
                    break;
            }
        }

        foreach (var (path, kind) in immediate)
        {
            switch (kind)
            {
                case DroppedFileKind.OwnProject: OpenProjectFile(path); break;
                case DroppedFileKind.OwnTabExport: ImportTabExportFile(path); break;
                case DroppedFileKind.Fuji: ImportFujiFile(path); break;
                case DroppedFileKind.Skb: ImportSkbFile(path); break;
                case DroppedFileKind.Dos: ImportDosFile(path); break;
            }
        }

        if (deferredAudio.Count > 0)
        {
            if (_document is null)
            {
                var names = string.Join("\n", deferredAudio.Select(a => Path.GetFileName(a.Path)));
                MessageBox.Show(this,
                    $"先にプロジェクトを作成/読み込みしてください。以下の楽曲ファイルは読み込めませんでした:\n{names}",
                    "音楽ファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                foreach (var (path, kind) in deferredAudio)
                {
                    if (kind == DroppedFileKind.RawAudio) LoadAudioFile(path);
                    else LoadBase64MusicFile(path);
                }
            }
        }

        if (unknown.Count > 0)
        {
            MessageBox.Show(this,
                $"以下のファイルは形式を判別できませんでした:\n{string.Join("\n", unknown)}\n\n" +
                "ファイルメニューの個別インポート機能をお使いください。",
                "判別できないファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>BASE64エンコードされた楽曲データJS/txt(TBD#6、dos-h0011-musicUrl)をデコードし、
    /// 一時ファイルへ書き出してから既存のLoadAudioFileへ渡す。元の音声形式はJS側に残らないため、
    /// デコード後のバイト列をマジックバイトで判定して拡張子を復元する(Base64MusicDecoder参照)。</summary>
    private void LoadBase64MusicFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var bytes = Core.Audio.Base64MusicDecoder.DecodeToBytes(content);
            if (bytes is null)
            {
                MessageBox.Show(this, $"楽曲データ(BASE64)のデコードに失敗しました: {Path.GetFileName(path)}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var ext = Core.Audio.Base64MusicDecoder.GuessExtension(bytes);
            var tempPath = Path.Combine(Path.GetTempPath(), $"danoni_music_{Guid.NewGuid():N}{ext}");
            File.WriteAllBytes(tempPath, bytes);
            LoadAudioFile(tempPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"楽曲データ(BASE64)の読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 2026-07-17g: Play/Pause/Stopボタンとそのハンドラは撤去。音楽再生はテスト専用のため
    // Space(目視テスト開始/終了)・Ctrl+Space(現在位置で終了)に一本化した。

    /// <summary>
    /// 再生位置(曲頭からの経過時間)をtickへ変換してカレントフレーム線を更新する。
    /// frame = 経過秒 × 60(仕様書の60fps基準)。この値はStartNumber/blankFrameを含む絶対フレーム軸と
    /// 同じ基準(曲頭=0)なので、TimingEngine.FrameToTickへそのまま渡せる。
    /// </summary>
    // =====================================================================
    // 自動保存・クラッシュ復旧(2026-07-25、TBD)。B案: 通常の保存(Ctrl+S)とは別領域
    // (AppPaths.AutoSaveDir)へ、変更のあるプロジェクトタブだけを一定間隔で控える。
    // 実際のI/Oロジックは DanoniEditor.Core.Persistence.AutoSaveManager 側に集約してあり、
    // ここではタイマーの起動/停止と、どのセッションを対象にするかの選定のみを担う。
    // =====================================================================

    /// <summary>環境設定のAutoSaveEnabled/AutoSaveIntervalMinutesを_autoSaveTimerへ反映する。
    /// 起動時・環境設定を閉じた直後(OpenPreferences)の両方から呼ぶ。</summary>
    private void ApplyAutoSaveTimerSettings()
    {
        _autoSaveTimer.Stop();
        if (!_appSettings.AutoSaveEnabled) return;
        _autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Max(0.1, _appSettings.AutoSaveIntervalMinutes));
        _autoSaveTimer.Start();
    }

    /// <summary>自動保存タイマーのTick。変更のある(IsModified)プロジェクトタブだけを対象に、
    /// 各セッションごとのスロットへ書き込む。変更が無いタブは何もしない(無駄な書き込み回避、
    /// ユーザー確定仕様)。1タブの書き込みに失敗しても他タブ・編集作業自体は継続する。</summary>
    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (!_appSettings.AutoSaveEnabled || _sessions.Count == 0) return;
        SyncActiveSessionBeforeSwitch(); // アクティブセッションのFilePathを最新化してから読む

        foreach (var s in _sessions)
        {
            if (!s.Document.IsModified) continue;
            try
            {
                var name = string.IsNullOrWhiteSpace(s.Document.Project.ProjectName) ? "Untitled" : s.Document.Project.ProjectName;
                var json = ProjectSerializer.Serialize(s.Document.Project);
                AutoSaveManager.WriteSlot(AppPaths.AutoSaveDir, s.SlotId, _instanceId, s.FilePath, name, json);
            }
            catch
            {
                // 自動保存の失敗で編集作業自体を止めたくないため、ここでは静かに無視する
                // (次回のTickで再試行される)。
            }
        }
    }

    /// <summary>2026-07-26: 予期しない例外を検出した際の緊急保存(App.OnDispatcherUnhandledException/
    /// AppDomain.UnhandledExceptionから呼ばれる)。変更のある全セッションを自動保存スロットへ
    /// 書き込む(AutoSaveTimer_Tickと同じ仕組みを流用)。AutoSaveEnabled設定に関わらず常に実行する
    /// (緊急時なので環境設定は問わない)。戻り値は実際に保存できたセッション数。</summary>
    public int EmergencySaveAllSessions()
    {
        int saved = 0;
        try { SyncActiveSessionBeforeSwitch(); } catch { /* 緊急時はベストエフォート */ }
        foreach (var s in _sessions)
        {
            if (!s.Document.IsModified) continue;
            try
            {
                var name = string.IsNullOrWhiteSpace(s.Document.Project.ProjectName) ? "Untitled" : s.Document.Project.ProjectName;
                var json = ProjectSerializer.Serialize(s.Document.Project);
                AutoSaveManager.WriteSlot(AppPaths.AutoSaveDir, s.SlotId, _instanceId, s.FilePath, name, json);
                saved++;
            }
            catch { /* 1件失敗しても他セッションの保存は続ける */ }
        }
        return saved;
    }

    /// <summary>起動時にクラッシュが疑われた場合、App.OnStartupから呼ばれる。manifestに記録された
    /// スロットを1件ずつ「復元しますか?」と尋ね、はいの場合は新規プロジェクトタブとして開く
    /// (復元後もあえてMarkSavedはせず、未保存状態のまま維持してユーザー自身の目で確認・保存を促す)。
    /// 復元してもしなくても、確認済みのスロットは古い控えとして削除する。</summary>
    public void OfferCrashRecovery(List<AutoSaveSlotInfo> slots)
    {
        foreach (var slot in slots)
        {
            var pathLabel = slot.LastKnownPath ?? "(未保存の新規プロジェクト)";
            var r = MessageBox.Show(this,
                $"前回、正常に終了しなかった形跡があります。\n\n" +
                $"プロジェクト: {slot.ProjectName}\n元のファイル: {pathLabel}\n" +
                $"自動保存日時: {slot.SavedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}\n\n" +
                "前回の続きから復元しますか?",
                "クラッシュ復旧", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r == MessageBoxResult.Yes)
            {
                try
                {
                    var json = AutoSaveManager.ReadSlotContent(AppPaths.AutoSaveDir, slot.SlotId);
                    var project = ProjectSerializer.Deserialize(json);
                    var doc = new EditorDocument(project, _templates);
                    doc.NotifyChanged(); // 復元直後は「未保存の変更あり」状態にする(既定markModified:true)
                    AddSession(doc, slot.LastKnownPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"復元に失敗しました: {ex.Message}", "クラッシュ復旧", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            AutoSaveManager.ClearSlot(AppPaths.AutoSaveDir, slot.SlotId);
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        // 2026-09-07要望対応: 診断情報(タイマー自体が回っているかの確認用、後段のnullチェックより前で数える)
        if (_lastVisualTestDiag is { } diagInvoked) diagInvoked.TickInvokedCount++;

        if (_document is null || _audioPlayer.Duration is null) return;
        var pos = _audioPlayer.Position;

        // 2026-09-07要望対応: 診断情報(実処理まで進んだ回数・再生位置の推移を記録)
        if (_lastVisualTestDiag is { } diag)
        {
            diag.TickProcessedCount++;
            diag.FirstTickPositionSeconds ??= pos.TotalSeconds;
            diag.LatestPositionSeconds = pos.TotalSeconds;
            diag.LatestSampledAtUtc = DateTime.UtcNow;
            diag.LatestOutputState = _audioPlayer.DiagOutputState;
            diag.LatestPlayingFlag = _audioPlayer.DiagIsPlayingFlag;
        }

        // 2026-09-13要望対応: WASAPI出力の自動復旧(スタック検知)。_playingフラグ・WASAPI出力状態は
        // 「再生中」のままなのに、内部の読み取りカーソルだけが進まなくなる不具合(NAudioのイベント同期
        // 起因と見られる、環境報告の診断情報で確認済み)への対策。再生位置が一定時間(StallRecoveryThreshold)
        // 変化しなければ、出力デバイスストリームだけを作り直して同じ位置から再生を再開する
        // (音声データ自体・_framePosは変更しない、_output=WasapiOutインスタンスのみ再構築)。
        if (_visualTestActive)
        {
            // 2026-09-13: 「前回サンプル値と完全一致しているか」で判定する(僅かな誤差を許容するあいまいな
            // 比較にすると、極端なスロー再生(PlaybackSpeedを小さくした場合)で1Tickあたりの進みが
            // その許容誤差を下回り、正常再生中でも「進んでいない」と誤検知しうるため)。本当に音声が
            // 進んでいれば、どれほど微小でも_framePosの値自体は毎回変化するため、完全一致判定でも
            // 実際の停止だけを正しく検知できる。
            if (_lastTickPositionSeconds < 0 || pos.TotalSeconds != _lastTickPositionSeconds)
            {
                _lastTickPositionSeconds = pos.TotalSeconds;
                _lastTickPositionChangedUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _lastTickPositionChangedUtc >= StallRecoveryThreshold)
            {
                _audioPlayer.RecoverOutput();
                _audioPlayer.Position = TimeSpan.FromSeconds(pos.TotalSeconds);
                _audioPlayer.Play();
                if (_lastVisualTestDiag is { } diagRecover) diagRecover.AutoRecoveryCount++;
                StatusText.Text = "目視テストの音声出力が停止していたため、自動的に復旧しました。";
                _lastTickPositionChangedUtc = DateTime.UtcNow; // 復旧直後に連続で再判定しないようリセット
            }
        }

        // 2026-07-26: 曲の末尾に到達した場合、音が止まった後も再生位置ラインとスクロールが同じ位置に
        // 固定されたまま(無音で)残り続けてしまう(前段のstartFrame超過チェックとは別経路で同じ症状に
        // なりうるため、こちらでも保険として自動終了させる)。
        if (_visualTestActive && pos.TotalSeconds >= _audioPlayer.Duration.Value.TotalSeconds - 0.05)
        {
            StopVisualTest(returnToStart: true, reason: "自動(曲の末尾到達)");
            return;
        }

        AudioTimeText.Text = pos.ToString(@"mm\:ss\.ff");

        double frame = pos.TotalSeconds * 60.0;
        var engine = _document.Project.CreateTimingEngine();

        // 2026-07-29要望対応: 選択範囲(時間情報レーンの範囲選択)のループ再生(DAW風、既定OFF)。
        // 目視テストはあくまで再生開始ラインから開始し、ループ終点に到達したらループ始点へ戻って
        // そのまま再生を続ける(停止しない)。始点・終点マーカーは個別ドラッグ可能なため、
        // 逆転している場合(始点>終点)に備えMath.Min/Maxで実際のループ区間を求める。
        if (_visualTestActive && _loopPlaybackEnabled
            && _document.CurrentTab.TimeRangeSelectionStartTick is { } loopTickA
            && _document.CurrentTab.TimeRangeSelectionEndTick is { } loopTickB)
        {
            double loopEndFrame = engine.TickToFrame(Math.Max(loopTickA, loopTickB));
            if (frame >= loopEndFrame)
            {
                double loopStartFrame = engine.TickToFrame(Math.Min(loopTickA, loopTickB));
                _audioPlayer.Position = TimeSpan.FromSeconds(loopStartFrame / 60.0);
                return; // 次のTickで巻き戻し後の位置を反映させる
            }
        }

        // 2026-07-29要望対応: 目視テスト自動復帰(再生開始ラインから指定小節数/秒数経過したら
        // 自動で再生開始ラインへ戻る、練習用の単純なループ機能。DAWループ(上記、時間情報レーンの
        // 範囲選択によるもの)とは独立した機能で、既定OFF)。
        if (_visualTestActive && _appSettings.VisualTestAutoReturnEnabled)
        {
            double startFrame = _document.CurrentTab.PlaybackStartFrame ?? 0;
            double thresholdFrame;
            if (_appSettings.VisualTestAutoReturnUnit == "seconds")
            {
                thresholdFrame = startFrame + _appSettings.VisualTestAutoReturnSeconds * 60.0;
            }
            else
            {
                long startTick = (long)engine.FrameToTick(startFrame);
                var (startMeasure, _) = engine.TickToMeasurePosition(startTick);
                long targetTick = engine.MeasureStartTick(startMeasure + _appSettings.VisualTestAutoReturnMeasures);
                thresholdFrame = engine.TickToFrame(targetTick);
            }

            if (frame >= thresholdFrame)
            {
                if (_appSettings.VisualTestAutoReturnContinuePlayback)
                {
                    _audioPlayer.Position = TimeSpan.FromSeconds(startFrame / 60.0);
                    return; // 次のTickで巻き戻し後の位置を反映させる
                }

                StopVisualTest(returnToStart: true, reason: "自動(自動復帰設定)");
                return;
            }
        }

        Canvas.PlaybackTick = engine.FrameToTick(frame);
        InvalidateChartViews();
        _previewSurface.CurrentFrame = frame; // 2026-07-29要望対応: 目視テスト中はプレビューも連動して動く

        // 2026-07-26f: ハンドクラップの発音判定・PCM重ね合わせは_audioPlayer(NAudioBgmPlayer)自身の
        // レンダースレッド内で直接行われるため、ここでの処理は不要になった(StartVisualTest参照)。

        // 2026-07-17f: 目視テスト中の追従スクロール(未解決事項§2-1、方式はAppSettingsで選択)
        // 2026-07-22: 譜面ビューReverse時はラインが画面上方向へ進むため、寄せる側の端を入れ替える
        // (進行方向が逆になる=「自然に画面外へ出る側」が上下逆になるため)。
        if (_visualTestActive && Canvas.PlaybackTick is { } lineTick)
        {
            bool reverse = _appSettings.ChartViewReverse;
            double lineY = _document.CurrentLayout.TickToY(lineTick);
            double off = ChartScrollViewer.VerticalOffset;
            double vh = ChartScrollViewer.ViewportHeight;
            if (_appSettings.VisualTestFollowMode == "smooth")
            {
                // (B)スムーズスクロール: ラインを画面の固定位置(通常=上端から35%、Reverse=下端から35%)に据える
                double target = reverse ? lineY - vh * 0.65 : lineY - vh * 0.35;
                ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
            }
            else
            {
                // (A)ページ送り: ラインが画面外へ出た瞬間、寄せる側の端(+8pxマージン)へ切替
                bool exited = reverse ? (lineY < off + 8 || lineY > off + vh) : (lineY > off + vh - 8 || lineY < off);
                if (exited)
                {
                    double target = reverse ? lineY - vh + 8 : lineY - 8;
                    ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
                }
            }
        }
    }

    // =====================================================================
    // 上段パネル(スナップ・難易度タブ)
    // =====================================================================

    /// <summary>選択中の難易度タブを閉じる(2026-07-17: 「タブを閉じるができない」要望対応)。
    /// 最低1タブは残す(0件になるとCurrentTab等が参照できなくなるため)。</summary>
    /// <summary>「新規譜面を追加」ボタン(2026-07-26要望対応)。新規プロジェクト作成時と同じダイアログ
    /// (NewProjectDialog)を再利用し、キー種・難易度名を指定してカレントプロジェクトへタブを追加する。
    /// BPMはプロジェクト全体で共通の値のためダイアログ上の入力は使用しない(タブ追加では変更しない)。</summary>
    private void AddDifficultyTab_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        // 2026-07-26要望対応: カレントプロジェクトは既にBPMを持っており、ここで入力してもタブ追加処理では
        // 使用しない(プロジェクト共通のBPMがそのまま使われる)ため、ダイアログのBPM欄は非表示にする。
        var choice = NewProjectDialog.Ask(this, _templates, _appSettings.DefaultBpm, showBpm: false);
        if (choice is not { } c) return;

        var template = _templates.Get(c.KeyTypeId);
        var newTab = DifficultyTab.CreateFor(template, c.DifficultyName);
        _document.Project.Tabs.Add(newTab);
        _document.NotifyTabsChanged(_document.Project.Tabs.Count - 1);
        // 2026-08-08: 予防的統一(進捗まとめ2-1/5-3)。従来はOpenDocument(_document)のみを呼んでおり
        // SmartToolControllerが毎回作り直されていた。実害は無いと調査済みだったが、「色編集状態は
        // 再適用される」「ドラッグ中には呼ばれない」という暗黙の前提に支えられた構造だったため、
        // D&D並び替え(DifficultyTabControl_Drop)と同じくコントローラを使い回す形へ揃える。
        OpenDocument(_document, _controller); // タブ一覧・各右パネルをまとめて再構築する
    }

    /// <summary>「タブを複製」ボタン(2026-07-26要望対応)。選択中のタブをノート・ゲージ設定等ごと
    /// 丸ごとディープコピーし、複製元の直後へ挿入する。タブ追加/削除/並び替えと同じ既存の方式に
    /// 揃え、Undoスタックには積まない(ユーザー確定仕様)。</summary>
    private void DuplicateDifficultyTab_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        DuplicateTabAt(_document.CurrentTabIndex);
    }

    /// <summary>指定インデックスのタブを複製する(2026-08-02: 右クリックメニューから任意のタブを
    /// 直接操作できるよう、DuplicateDifficultyTab_Clickの本体をインデックス指定版として分離)。</summary>
    private void DuplicateTabAt(int idx)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから複製する(複製元に確実に反映させる)
        if (_document is null) return;
        var tabs = _document.Project.Tabs;
        if (idx < 0 || idx >= tabs.Count) return;

        var clone = tabs[idx].Clone(_appSettings.CarryOverPlaybackStartOnTabDuplicate);
        clone.DifficultyName = $"{clone.DifficultyName} のコピー";
        tabs.Insert(idx + 1, clone);
        _document.NotifyTabsChanged(idx + 1);
        // 2026-08-08: 予防的統一(進捗まとめ2-1/5-3)。理由はAddDifficultyTab_Clickのコメント参照。
        OpenDocument(_document, _controller); // タブ一覧・各右パネルをまとめて再構築する
    }

    private void CloseCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        CloseTabAt(_document.CurrentTabIndex);
    }

    /// <summary>指定インデックスのタブを閉じる(2026-08-02: 右クリックメニューから任意のタブを
    /// 直接操作できるよう、CloseCurrentTab_Clickの本体をインデックス指定版として分離)。</summary>
    private void CloseTabAt(int idx)
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値を確定してから閉じる(残るタブへの誤コミットを防ぐ)
        if (_document is null) return;
        var tabs = _document.Project.Tabs;
        if (tabs.Count <= 1)
        {
            MessageBox.Show(this, "最後の1タブは閉じられません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (idx < 0 || idx >= tabs.Count) return;

        var target = tabs[idx];
        var confirm = MessageBox.Show(this, $"タブ「{target.DifficultyName}」を閉じますか？(この操作はUndoできません)",
            "タブを閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // 2026-07-26要望対応: リンク中のタブを閉じる場合、相手タブ側の参照が宙に浮かないようリンクを
        // 解除する(タブリンク機能)。2026-08-06: この解除処理はProjectOperations.RemoveTab側へ集約した
        // (呼び出し側の作法に依存せず、どの経路から削除しても安全になるように)。
        ProjectOperations.RemoveTab(_document.Project, idx);
        // 2026-07-24: 単純に CurrentTabIndex に代入するだけだと、閉じたタブが末尾以外の場合
        // 「数値としては変わらないインデックス」になり得て(例: 3件中の2番目を閉じると2→2のまま)、
        // setterの早期returnガードに阻まれてレイアウトキャッシュが古いタブのテンプレートを
        // 指したまま残ってしまう(タブを閉じるとクラッシュする不具合の原因)。
        // NotifyTabsChangedは値の異同に関わらず無条件でキャッシュ等を作り直すため、これを使う。
        _document.NotifyTabsChanged(Math.Min(idx, tabs.Count - 1));
        // 2026-08-08: 予防的統一(進捗まとめ2-1/5-3)。理由はAddDifficultyTab_Clickのコメント参照。
        OpenDocument(_document, _controller); // タブ一覧・各右パネルをまとめて再構築する
    }

    /// <summary>指定インデックスのタブの「dosロック」(ExcludeFromDosExport)を切り替える
    /// (2026-08-02要望対応)。dosロック中のタブはDosExporterがdifData一覧・データブロックの
    /// どちらからも除外する(制作中で未公開にしたい譜面向け)。DisplayLabelはINotifyPropertyChanged非対応の
    /// ためOpenDocumentで一覧を作り直し、先頭の"[×]"表示を反映させる。</summary>
    private void ToggleDosLock(int idx)
    {
        if (_document is null) return;
        var tabs = _document.Project.Tabs;
        if (idx < 0 || idx >= tabs.Count) return;

        tabs[idx].ExcludeFromDosExport = !tabs[idx].ExcludeFromDosExport;
        // 2026-08-08: 予防的統一(進捗まとめ2-1/5-3)。理由はAddDifficultyTab_Clickのコメント参照。
        OpenDocument(_document, _controller); // タブ一覧の[×]表示を更新するため作り直す
    }

    /// <summary>難易度タブ行の右クリックメニュー(2026-08-02要望対応)。タブの上で右クリックした場合は
    /// 「dosロック」(切替)・「タブを複製」・「タブを削除」、タブの無い場所(行の余白)で右クリックした
    /// 場合は「タブを追加」を表示する。既存のD&D(FindTabItemAncestor/IndexFromContainer)と同じ
    /// ヒットテスト方式でどのタブが右クリックされたかを判定する。</summary>
    private void DifficultyTabControl_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_document is null) return;
        var tc = (TabControl)sender;
        var item = FindTabItemAncestor(e.OriginalSource as DependencyObject);
        var menu = new ContextMenu();

        if (item is not null)
        {
            int idx = tc.ItemContainerGenerator.IndexFromContainer(item);
            var tabs = _document.Project.Tabs;
            if (idx < 0 || idx >= tabs.Count) return;
            bool locked = tabs[idx].ExcludeFromDosExport;

            var lockItem = new MenuItem { Header = locked ? "dosロックを解除" : "dosロック" };
            lockItem.Click += (_, _) => ToggleDosLock(idx);
            menu.Items.Add(lockItem);

            var dupItem = new MenuItem { Header = "タブを複製" };
            dupItem.Click += (_, _) => DuplicateTabAt(idx);
            menu.Items.Add(dupItem);

            var delItem = new MenuItem { Header = "タブを削除" };
            delItem.Click += (_, _) => CloseTabAt(idx);
            menu.Items.Add(delItem);
        }
        else
        {
            var addItem = new MenuItem { Header = "タブを追加" };
            addItem.Click += (_, _) => AddDifficultyTab_Click(sender, new RoutedEventArgs());
            menu.Items.Add(addItem);
        }

        menu.PlacementTarget = tc;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // =====================================================================
    // タブのD&D並び替え(仕様書6.1「難易度タブはD&Dで並び替え」/TBD#10、2026-07-20。
    // 2026-08-06要望対応: 「離した場所のタブと入替え」ではなく「離した位置に挿入」する挙動へ変更し、
    // 挿入先を示すインジケータ(縦線)を表示するようにした)。
    // ProjectTabControl/DifficultyTabControlの両方で共用。DisplayMemberPath運用のまま
    // (ItemTemplate等を変更せず)ヒットテストでTabItem・そのIndexFromContainerを求める方式。
    // 同じ行同士でしかドラッグを開始しないため、行をまたいだ入れ替えは起こらない。
    // =====================================================================

    private Point _tabDragStartPoint;
    private int _tabDragSourceIndex = -1;
    private TabControl? _tabDragControl;

    private static TabItem? FindTabItemAncestor(DependencyObject? source)
    {
        while (source is not null and not TabItem)
            source = VisualTreeHelper.GetParent(source);
        return source as TabItem;
    }

    /// <summary>マウス位置(tc基準の座標)から「挿入先index」(0～Items.Count、Countなら末尾へ挿入)を
    /// 判定する(2026-08-06要望対応)。TabItem上にカーソルがあれば、そのTabItemの左右どちらの半分に
    /// あるかで「そのタブの前」か「そのタブの後」かを決める。TabItemの外(タブ行の余白)にカーソルが
    /// ある場合は、先頭タブより左なら先頭、末尾タブより右なら末尾として扱う。</summary>
    private static int ComputeTabInsertIndex(TabControl tc, Point posOnTabControl, DependencyObject? hitSource)
    {
        int count = tc.Items.Count;
        if (count == 0) return 0;

        var item = FindTabItemAncestor(hitSource);
        if (item is not null)
        {
            int idx = tc.ItemContainerGenerator.IndexFromContainer(item);
            if (idx < 0) return count;
            double itemLeft = item.TranslatePoint(new Point(0, 0), tc).X;
            double midX = itemLeft + item.ActualWidth / 2.0;
            return posOnTabControl.X < midX ? idx : idx + 1;
        }

        // TabItemそのものには乗っていない(タブ行の余白部分)。先頭・末尾タブとの位置関係で判定する。
        if (tc.ItemContainerGenerator.ContainerFromIndex(0) is TabItem first &&
            posOnTabControl.X < first.TranslatePoint(new Point(0, 0), tc).X)
            return 0;
        return count;
    }

    /// <summary>挿入先index(ComputeTabInsertIndexの戻り値)に対応する、インジケータ線を引くべきX座標
    /// (tc基準)を求める(2026-08-06要望対応)。</summary>
    private static double TabInsertIndicatorX(TabControl tc, int insertIndex)
    {
        int count = tc.Items.Count;
        if (count == 0) return 0;
        if (insertIndex <= 0)
            return tc.ItemContainerGenerator.ContainerFromIndex(0) is TabItem first
                ? first.TranslatePoint(new Point(0, 0), tc).X : 0;
        if (insertIndex >= count)
            return tc.ItemContainerGenerator.ContainerFromIndex(count - 1) is TabItem last
                ? last.TranslatePoint(new Point(0, 0), tc).X + last.ActualWidth : 0;
        return tc.ItemContainerGenerator.ContainerFromIndex(insertIndex) is TabItem mid
            ? mid.TranslatePoint(new Point(0, 0), tc).X : 0;
    }

    private Border TabInsertIndicatorFor(TabControl tc) =>
        ReferenceEquals(tc, ProjectTabControl) ? ProjectTabInsertIndicator : DifficultyTabInsertIndicator;

    private void ShowTabInsertIndicator(TabControl tc, int insertIndex)
    {
        var indicator = TabInsertIndicatorFor(tc);
        double x = TabInsertIndicatorX(tc, insertIndex);
        indicator.Margin = new Thickness(x - indicator.Width / 2.0, 0, 0, 0);
        indicator.Visibility = Visibility.Visible;
    }

    private void HideTabInsertIndicator(TabControl tc) => TabInsertIndicatorFor(tc).Visibility = Visibility.Collapsed;

    private void TabControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tc = (TabControl)sender;
        var item = FindTabItemAncestor(e.OriginalSource as DependencyObject);
        if (item is null) return;
        _tabDragStartPoint = e.GetPosition(null);
        _tabDragSourceIndex = tc.ItemContainerGenerator.IndexFromContainer(item);
        _tabDragControl = tc;
    }

    private void TabControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(_tabDragControl, sender) || _tabDragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var tc = (TabControl)sender;
        int from = _tabDragSourceIndex;
        _tabDragSourceIndex = -1;
        _tabDragControl = null;
        DragDrop.DoDragDrop(tc, from, DragDropEffects.Move);
        // DoDragDropはドラッグ操作が終わる(Drop完了・ESCキャンセル・行外へのドロップ等)まで戻らないため、
        // 終了理由によらずここで確実にインジケータを消す(Drop/DragLeaveの呼び忘れ経路をカバーする保険)。
        HideTabInsertIndicator(tc);
    }

    private void TabControl_PreviewDragOver(object sender, DragEventArgs e)
    {
        var tc = (TabControl)sender;
        if (!e.Data.GetDataPresent(typeof(int)))
        {
            e.Effects = DragDropEffects.None;
            HideTabInsertIndicator(tc);
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int insertIndex = ComputeTabInsertIndex(tc, e.GetPosition(tc), e.OriginalSource as DependencyObject);
        ShowTabInsertIndicator(tc, insertIndex);
        e.Handled = true;
    }

    private void TabControl_DragLeave(object sender, DragEventArgs e) => HideTabInsertIndicator((TabControl)sender);

    /// <summary>プロジェクトタブ行内での並び替え。2026-08-06要望対応:「離した場所のタブと入替え」ではなく
    /// 「離した位置(カーソルがタブの左右どちらの半分にあるか)に挿入」する。挿入後も同じセッションを
    /// アクティブにする(順番だけ変わり、選択中プロジェクトは変わらない)。</summary>
    private void ProjectTabControl_Drop(object sender, DragEventArgs e)
    {
        HideTabInsertIndicator(ProjectTabControl);
        if (!e.Data.GetDataPresent(typeof(int))) return;
        int from = (int)e.Data.GetData(typeof(int));
        if (from < 0 || from >= _sessions.Count) return;

        int rawTarget = ComputeTabInsertIndex(ProjectTabControl, e.GetPosition(ProjectTabControl), e.OriginalSource as DependencyObject);
        int to = rawTarget > from ? rawTarget - 1 : rawTarget; // fromを取り除いた後のインデックスへ変換
        if (to == from) return;

        SyncActiveSessionBeforeSwitch(); // 並び替え前に現在の実行時状態を書き戻しておく
        var moved = _sessions[from];
        var activeSession = _activeSessionIndex >= 0 ? _sessions[_activeSessionIndex] : null;
        _sessions.RemoveAt(from);
        _sessions.Insert(to, moved);
        if (activeSession is not null) _activeSessionIndex = _sessions.IndexOf(activeSession);
        RefreshProjectTabBar();
    }

    /// <summary>難易度タブ行内での並び替え。2026-08-06要望対応:「離した場所のタブと入替え」ではなく
    /// 「離した位置に挿入」する。既存のProjectOperations.MoveTab(1タブ目の色実体入替ルール込み、
    /// 仕様書6.4.2)はfromIndex/toIndexとも「挿入後」の絶対indexを取るため、ここで求めた挿入先を
    /// from除去後のindexへ変換してから渡す。</summary>
    private void DifficultyTabControl_Drop(object sender, DragEventArgs e)
    {
        HideTabInsertIndicator(DifficultyTabControl);
        if (_document is null || !e.Data.GetDataPresent(typeof(int))) return;
        int from = (int)e.Data.GetData(typeof(int));
        var tabs = _document.Project.Tabs;
        if (from < 0 || from >= tabs.Count) return;

        int rawTarget = ComputeTabInsertIndex(DifficultyTabControl, e.GetPosition(DifficultyTabControl), e.OriginalSource as DependencyObject);
        int to = rawTarget > from ? rawTarget - 1 : rawTarget; // fromを取り除いた後のインデックスへ変換
        if (to == from) return;

        ProjectOperations.MoveTab(_document.Project, from, to);
        // 2026-07-24: 並び替え後もtoが元のCurrentTabIndexと同値になり得る(例: 自分より後ろのタブと
        // 入れ替える場合)。単純代入だとsetterの早期returnで弾かれるため、CloseCurrentTab_Clickと同じく
        // NotifyTabsChangedで無条件に反映する。
        _document.NotifyTabsChanged(to);
        OpenDocument(_document, _controller);
    }

    private void DifficultyTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent || _document is null) return;
        if (DifficultyTabControl.SelectedIndex < 0) return;

        // 2026-08-06不具合修正: 右パネルのテキストボックス(InitialSpeedBox等、LostFocus確定方式の項目)に
        // フォーカスが残ったままタブを切り替えると、LostFocus(値のコミット)がCurrentTabIndexの切替後に
        // 発火し、変更前タブへの編集内容が変更後タブへ誤って書き込まれてしまう不具合があった。
        // 「まだ変更前タブがCurrentTabの状態」で確実にコミットさせてから切り替える(CommitPendingEdits参照)。
        CommitPendingEdits();

        _document.CurrentTabIndex = DifficultyTabControl.SelectedIndex;
        Canvas.InvalidateMeasure();
        if (_splitViewEnabled) Canvas2.InvalidateMeasure(); // 2026-07-26: 分割ビュー中は右ペインも再計測
        InvalidateChartViews();
        RefreshProjectPropertiesPanel();
        RefreshColorPanel();

        // 2026-07-26: プレイテストのReverseをキー種ごとの既定値に合わせて自動切替する(環境設定「プレイテスト」
        // カテゴリのキー種別一覧で設定した値。一覧に無いキー種はOFF扱い)。PlaytestReverseCheck.IsChecked代入は
        // PlaytestSetting_Changed経由でAppSettings.PlaytestReverseへも反映・保存される。
        // 2026-07-29要望対応: この上書きは、下のRefreshMacroList(→プレビュー再構築)より必ず先に行う
        // (でないと、切替後のプレビューに「切替前のタブのReverse」が一瞬反映されてしまうバグになる)。
        bool reverseDefault = _appSettings.PlaytestReverseByKeyType.TryGetValue(_document.CurrentTab.KeyTypeId, out var rev) && rev;
        PlaytestReverseCheck.IsChecked = reverseDefault;

        RefreshMacroList(); // 2026-07-26: タブのKeyTypeIdが変わるため一覧の内容自体を切り替える(プレビューの再構築もここで行われる)
        RefreshLinkPanel(); // 2026-07-26: タブリンクパネルも同様に切り替える
        RefreshAnalysisPanel(); // 2026-07-26: タブが変わればTotalRating等も変わるため結果表示をリセットする
    }

    // =====================================================================
    // 右パネル②: 色設定(setColor/frzColor、仕様書6.4.2)
    // =====================================================================

    private bool _suppressColorPanelEvents;

    private static readonly string[] DefaultSetColorPalette = ["#99FFFF", "#CCCCCC", "#FFFFFF", "#FF0066", "#99FFFF"];
    private static readonly string[] DefaultFrzColorSlots = ["#66FFFF", "#6666FF", "#FFFF66", "#FFFF66"];
    private static readonly string[] FrzSlotLabels = ["始点終点(通常)", "帯(通常)", "始点終点(判定中)", "帯(判定中)"];

    private int ColorGroupCount() =>
        _document!.CurrentTemplate.Lanes.Select(l => l.ColorGroup).DefaultIfEmpty(0).Max() + 1;

    private static List<string> DefaultSetColors(int groupCount) =>
        Enumerable.Range(0, groupCount).Select(i => DefaultSetColorPalette[i % DefaultSetColorPalette.Length]).ToList();

    /// <summary>2026-07-26確定仕様: frzColorは色グループ数に関わらず常に4スロット固定
    /// (danoniplus本体の仕様通り。従来の「色グループ数×4」は誤りだった)。</summary>
    private static List<string> DefaultFrzColors() => [.. DefaultFrzColorSlots];

    /// <summary>②タブ表示専用: tab.SetColorOverrideをgroupCount件ぶんの表示用リストとして返す
    /// (2026-08-06不具合修正: 以前はここでtab.SetColorOverride自体へ既定色を書き込んでいたため、
    /// 未設定のプロジェクトを開いた・タブを追加しただけで勝手にsetColorが設定されてしまう不具合が
    /// あった。表示専用のため、未設定分は既定色ではなく空欄で埋め、モデルは一切変更しない
    /// (実際にモデルへ書き込むのはユーザーが値を編集した時のみ、ColorField_LostFocus参照)。</summary>
    private static List<string> DisplaySetColors(DifficultyTab tab, int groupCount)
    {
        var list = tab.SetColorOverride is null ? [] : new List<string>(tab.SetColorOverride);
        while (list.Count < groupCount) list.Add("");
        return list;
    }

    /// <summary>②タブ表示専用: tab.FrzColorOverrideを4件ぶんの表示用リストとして返す(2026-07-26:
    /// 色グループ数に関わらず固定4スロット、2026-08-06不具合修正: DisplaySetColors同様、モデルへの
    /// 書き込みは行わない表示専用の読み取りへ変更した)。</summary>
    private static List<string> DisplayFrzColors(DifficultyTab tab)
    {
        var list = tab.FrzColorOverride is null ? [] : new List<string>(tab.FrzColorOverride);
        while (list.Count < 4) list.Add("");
        return list;
    }

    /// <summary>hexとしてパースできればそのブラシ、できなければ(グラデーション等の生文字列)灰色のプレビュー。</summary>
    private static Brush SafeColorBrush(string text)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)!); }
        catch { return Brushes.LightGray; }
    }

    private void AddColorField(Panel parent, string labelText, string value, bool enabled, (string Kind, int Group, int Slot) tag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var label = new TextBlock { Text = labelText, Width = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var swatch = new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0),
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Background = SafeColorBrush(value),
        };
        var box = new TextBox { Width = 110, Text = value, IsEnabled = enabled, Tag = tag };
        box.TextChanged += (_, _) => swatch.Background = SafeColorBrush(box.Text);
        box.PreviewKeyDown += CommitOnEnter_PreviewKeyDown;
        box.LostFocus += ColorField_LostFocus;
        box.LostFocus += (_, _) =>
        {
            if (!box.IsEnabled) return;
            ColorHistoryPicker.Record(_appSettings, box.Text);
            _appSettings.Save(AppPaths.SettingsFilePath);
        };

        row.Children.Add(label);
        row.Children.Add(swatch);
        row.Children.Add(box);
        if (enabled)
        {
            // 2026-08-08: 「履歴」ボタンを統合カラーピッカー(履歴+お気に入り+HSV視覚選択+RGB/HEX入力、
            // ColorPickerPopup)の呼び出しへ置き換え、隣に「☆登録」ボタン(現在値をお気に入りへ追加)を
            // 新設した(進捗まとめ5-2、お気に入りの色機能)。押しても見た目の変化が分かりづらいとの
            // 指摘を受け、登録成功時に「OK」を2秒間表示する(TransientOkFeedback)。
            var pickerBtn = new Button { Content = "色", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
            pickerBtn.Click += (_, _) => ColorPickerPopup.Show(_appSettings, pickerBtn, box.Text, hex =>
            {
                box.Text = hex;
                ColorField_LostFocus(box, new RoutedEventArgs());
            });
            row.Children.Add(pickerBtn);

            var favBtn = new Button { Content = "☆登録", Width = 44, Margin = new Thickness(4, 0, 0, 0) };
            var favOkText = new TextBlock
            {
                Text = "OK", Foreground = Brushes.Green, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            favBtn.Click += (_, _) =>
            {
                if (FavoriteColorPicker.Register(_appSettings, box.Text))
                {
                    _appSettings.Save(AppPaths.SettingsFilePath);
                    TransientOkFeedback.Show(favOkText);
                }
            };
            row.Children.Add(favBtn);
            row.Children.Add(favOkText);
        }
        parent.Children.Add(row);
    }

    /// <summary>タブ切替・ドキュメント読込・共通チェック切替のたびに呼ばれ、②タブの表示を再構築する。</summary>
    private void RefreshColorPanel()
    {
        if (_document is null) return;
        SetColorPanel.Children.Clear();
        FrzColorPanel.Children.Clear();

        bool isFirstTab = _document.CurrentTabIndex == 0;
        ColorTab0NoticeText.Visibility = isFirstTab ? Visibility.Visible : Visibility.Collapsed;
        SetColorCommonCheck.Visibility = isFirstTab ? Visibility.Collapsed : Visibility.Visible;
        FrzColorCommonCheck.Visibility = isFirstTab ? Visibility.Collapsed : Visibility.Visible;

        var tab0 = _document.Project.Tabs[0];
        var currentTab = _document.CurrentTab;
        int groupCount = ColorGroupCount();

        _suppressColorPanelEvents = true;

        // 2026-08-06要望対応: setColor/frzColorの「共通を使う」をそれぞれ独立して判定する
        // (以前はSetColorOverrideの有無だけで両方まとめて判定していたため、「setは変えるがfrzは
        // 共通のまま」のような組み合わせが選べなかった)。
        bool useCommonSet = !isFirstTab && currentTab.SetColorOverride is null;
        bool useCommonFrz = !isFirstTab && currentTab.FrzColorOverride is null;
        if (!isFirstTab)
        {
            SetColorCommonCheck.IsChecked = useCommonSet;
            FrzColorCommonCheck.IsChecked = useCommonFrz;
        }

        bool editableSet = isFirstTab || !useCommonSet;
        bool editableFrz = isFirstTab || !useCommonFrz;
        var setSource = editableSet && !isFirstTab ? DisplaySetColors(currentTab, groupCount) : DisplaySetColors(tab0, groupCount);
        // 2026-07-26: frzColorは色グループ数に関わらず常に4スロット固定の1セットのみ(danoniplus本体の仕様通り)
        var frzSource = editableFrz && !isFirstTab ? DisplayFrzColors(currentTab) : DisplayFrzColors(tab0);

        for (int g = 0; g < groupCount; g++)
            AddColorField(SetColorPanel, $"色グループ{g}", g < setSource.Count ? setSource[g] : "", editableSet, ("set", g, -1));

        // 2026-08-05不具合修正: defaultFrzColorUse(dos-h0063)がONでも、frzColorのHit(判定中、[2]/[3])は
        // 本体側で引き続き有効(生きる)ため、frzColor入力欄自体を無効化してはいけない(以前は4スロット
        // まとめて編集不可にしていたが、これだと判定中の色を指定できなくなってしまう不具合だった)。
        // ONの間は始点終点(通常)/帯(通常)([0]/[1])の値のみが無視される旨を案内するに留める。
        bool defaultFrzColorUse = _document.Project.ExtraHeaders.TryGetValue("defaultFrzColorUse", out var dfu) && dfu == "true";
        if (defaultFrzColorUse)
        {
            FrzColorPanel.Children.Add(new TextBlock
            {
                Text = "④タブのdefaultFrzColorUseが有効なため、始点終点(通常)/帯(通常)の値は無視されます(空欄可)。始点終点(判定中)/帯(判定中)は引き続き有効です。",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        // 2026-07-26: frzColorは色グループの概念を持たないため、色グループ見出しなしで4スロットのみ表示する
        for (int s = 0; s < 4; s++)
            AddColorField(FrzColorPanel, FrzSlotLabels[s], s < frzSource.Count ? frzSource[s] : "", editableFrz, ("frz", -1, s));

        _suppressColorPanelEvents = false;
    }

    private void ColorField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressColorPanelEvents || _document is null) return;
        var box = (TextBox)sender;
        if (!box.IsEnabled) return;
        var (kind, group, slot) = ((string Kind, int Group, int Slot))box.Tag;

        bool isFirstTab = _document.CurrentTabIndex == 0;
        var targetTab = isFirstTab ? _document.Project.Tabs[0] : _document.CurrentTab;

        if (kind == "set")
        {
            // 2026-08-06不具合修正: 表示側(DisplaySetColors)がモデルへ書き込まなくなったため、
            // ユーザーが実際に値を編集したこの時点で初めてSetColorOverrideを実体化する
            // (未設定分は既定色ではなく空欄"" で埋める。「新規プロジェクト作成時のみ既定色を
            // 自動補完する」というユーザー確定仕様により、ここでは補完しない)。
            targetTab.SetColorOverride ??= [];
            while (targetTab.SetColorOverride.Count <= group) targetTab.SetColorOverride.Add("");
            if (targetTab.SetColorOverride[group] == box.Text) return;
            targetTab.SetColorOverride[group] = box.Text;
        }
        else
        {
            // 2026-07-26: frzColorは色グループを持たない固定4スロットのため、slotがそのままインデックス
            // (2026-08-06不具合修正: 上記SetColorOverride同様、編集時に初めて実体化する)
            targetTab.FrzColorOverride ??= [];
            while (targetTab.FrzColorOverride.Count <= slot) targetTab.FrzColorOverride.Add("");
            if (targetTab.FrzColorOverride[slot] == box.Text) return;
            targetTab.FrzColorOverride[slot] = box.Text;
        }
        _document.NotifyChanged();
        InvalidateChartViews(); // レーン色プレビュー(LaneBrush)へ反映
    }

    /// <summary>setColorの「全ての難易度で共通」切替(2026-08-06要望対応: frzColorとは独立して
    /// 切り替えられるよう分離した。「setは変えるがfrzは共通のまま」等の組み合わせに対応するため)。</summary>
    private void SetColorCommonCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressColorPanelEvents || _document is null || _document.CurrentTabIndex == 0) return;
        var tab = _document.CurrentTab;
        var tab0 = _document.Project.Tabs[0];
        int groupCount = ColorGroupCount();

        if (SetColorCommonCheck.IsChecked == true)
        {
            tab.SetColorOverride = null;
        }
        else
        {
            // 2026-08-06: ユーザーが明示的に「共通を使う」を外した(=このタブだけ独自のsetColorに
            // したい)操作なので、その時点のtab0の表示値をコピーして開始点にする(tab0が未設定なら
            // 空欄のままコピーする。「新規プロジェクト作成時のみ既定色を自動補完する」の対象外の
            // 操作のため、ここで既定色を持ち出すことはしない)。
            tab.SetColorOverride = DisplaySetColors(tab0, groupCount);
        }
        _document.NotifyChanged();
        RefreshColorPanel();
    }

    /// <summary>frzColorの「全ての難易度で共通」切替(2026-08-06要望対応: SetColorCommonCheck_Changed参照)。</summary>
    private void FrzColorCommonCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressColorPanelEvents || _document is null || _document.CurrentTabIndex == 0) return;
        var tab = _document.CurrentTab;
        var tab0 = _document.Project.Tabs[0];

        if (FrzColorCommonCheck.IsChecked == true)
        {
            tab.FrzColorOverride = null;
        }
        else
        {
            tab.FrzColorOverride = DisplayFrzColors(tab0);
        }
        _document.NotifyChanged();
        RefreshColorPanel();
    }

    // =====================================================================
    // 右パネル④: その他のヘッダー機能(仕様書6.4.4)
    // =====================================================================

    /// <summary>ドキュメント読込時に一度だけ構築する(ExtraHeadersはプロジェクト共通・タブ非依存のため、
    /// タブ切替のたびに作り直す必要はない)。</summary>
    private void RefreshExtraHeadersPanel()
    {
        if (_document is null) return;
        ExtraHeadersPanel.Children.Clear();
        var headers = _document.Project.ExtraHeaders;

        // 2026-07-26: チェックボックスの羅列で視認性が悪いとの要望対応。大項目(Category)ごとに
        // Expanderで折りたたむ。既定では「そのカテゴリ内に既に設定済みの項目が1つでもあれば展開、
        // 無ければ折りたたみ」とし、見落とし防止と一覧性のバランスを取る。
        string? lastCategory = null;
        StackPanel? categoryPanel = null;
        List<HeaderParamDef>? categoryDefs = null;

        void FlushCategory()
        {
            if (lastCategory is null || categoryPanel is null || categoryDefs is null) return;
            bool hasActiveValue = categoryDefs.Any(d => headers.ContainsKey(d.Name));
            ExtraHeadersPanel.Children.Add(new Expander
            {
                Header = lastCategory,
                IsExpanded = hasActiveValue,
                Margin = new Thickness(0, 0, 0, 8),
                Content = categoryPanel,
            });
        }

        foreach (var def in ExtraHeaderDefs.All)
        {
            if (def.Category != lastCategory)
            {
                FlushCategory();
                lastCategory = def.Category;
                categoryPanel = new StackPanel { Margin = new Thickness(4, 8, 0, 4) };
                categoryDefs = [];
            }
            categoryDefs!.Add(def);
            AddExtraHeaderRow(def, headers, categoryPanel!);
        }
        FlushCategory();
    }

    private void AddExtraHeaderRow(HeaderParamDef def, Dictionary<string, string> headers, Panel targetPanel)
    {
        bool hasValue = headers.TryGetValue(def.Name, out var existing);
        string initial = hasValue ? existing! : def.Default;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = def.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center });

        var useCheck = new CheckBox { Content = "使用する", IsChecked = hasValue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        row.Children.Add(useCheck);

        switch (def.Type)
        {
            case HeaderParamType.Bool:
                {
                    // 2026-08-05要望対応: 真偽値パラメータも他の型と同じ「使用する」チェック+値選択の
                    // 形式へ統一する(旧: 単一チェックボックスでON=true出力・OFF=未出力のみだったため、
                    // 明示的なfalse出力ができなかった)。「使用する」OFFの間は未出力(エンジン既定値)、
                    // ONの間はtrue/falseラジオボタンで選んだ値を明示的に出力する。
                    bool initialTrue = initial == "true";
                    var trueRadio = new RadioButton
                    {
                        Content = "true", GroupName = $"boolval_{def.Name}",
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                        IsEnabled = hasValue, IsChecked = initialTrue,
                    };
                    var falseRadio = new RadioButton
                    {
                        Content = "false", GroupName = $"boolval_{def.Name}",
                        VerticalAlignment = VerticalAlignment.Center,
                        IsEnabled = hasValue, IsChecked = !initialTrue,
                    };

                    void CommitBoolValue()
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = trueRadio.IsChecked == true ? "true" : "false";
                        _document!.NotifyChanged();
                    }
                    trueRadio.Checked += (_, _) => CommitBoolValue();
                    falseRadio.Checked += (_, _) => CommitBoolValue();

                    useCheck.Checked += (_, _) =>
                    {
                        trueRadio.IsEnabled = true;
                        falseRadio.IsEnabled = true;
                        CommitBoolValue();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        trueRadio.IsEnabled = false;
                        falseRadio.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };

                    if (def.Name == "defaultFrzColorUse")
                    {
                        // 2026-08-05不具合修正: defaultFrzColorUseがtrueになっても、frzColorのHit
                        // (判定中、[2]/[3])は本体側で引き続き有効なため、FrzColorOverrideを丸ごとクリア
                        // してはいけない(以前はtrue化のたびに全タブの値を消していたが、判定中の色設定が
                        // 失われてしまう不具合だった)。②タブの案内表示(始点終点/帯(通常)が無視される旨)を
                        // 更新するためだけにRefreshColorPanelを呼ぶ。
                        trueRadio.Checked += (_, _) => RefreshColorPanel();
                        falseRadio.Checked += (_, _) => RefreshColorPanel();
                        useCheck.Unchecked += (_, _) => RefreshColorPanel();
                    }

                    row.Children.Add(trueRadio);
                    row.Children.Add(falseRadio);
                    break;
                }

            case HeaderParamType.Dropdown:
                {
                    var combo = new ComboBox { ItemsSource = def.Options, Width = 140, IsEnabled = hasValue, SelectedItem = initial };
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = combo.SelectedItem as string ?? def.Default;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        combo.IsEnabled = true;
                        headers[def.Name] = combo.SelectedItem as string ?? def.Default;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        combo.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(combo);
                    break;
                }

            case HeaderParamType.Color:
                {
                    var swatch = new Border
                    {
                        Width = 14, Height = 14, Margin = new Thickness(4, 0, 4, 0),
                        BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                        Background = SafeColorBrush(initial),
                    };
                    var box = new TextBox { Width = 120, Text = initial, IsEnabled = hasValue };
                    box.TextChanged += (_, _) => swatch.Background = SafeColorBrush(box.Text);
                    box.PreviewKeyDown += CommitOnEnter_PreviewKeyDown;
                    box.LostFocus += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        box.IsEnabled = true;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        box.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(swatch);
                    row.Children.Add(box);
                    break;
                }

            default: // Number / Text / Raw
                {
                    var box = new TextBox { Width = def.Type == HeaderParamType.Raw ? 220 : 140, Text = initial, IsEnabled = hasValue };
                    box.PreviewKeyDown += CommitOnEnter_PreviewKeyDown;
                    box.LostFocus += (_, _) =>
                    {
                        if (useCheck.IsChecked != true) return;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Checked += (_, _) =>
                    {
                        box.IsEnabled = true;
                        headers[def.Name] = box.Text;
                        _document!.NotifyChanged();
                    };
                    useCheck.Unchecked += (_, _) =>
                    {
                        box.IsEnabled = false;
                        headers.Remove(def.Name);
                        _document!.NotifyChanged();
                    };
                    row.Children.Add(box);
                    break;
                }
        }

        targetPanel.Children.Add(row);
    }

    // =====================================================================
    // 右パネル①: プロジェクトのプロパティ(仕様書6.4.1)
    // =====================================================================

    /// <summary>ダンおに本体の「レベル計算ツール++」アルゴリズム(danoni_main.jsのcalcLevelを移植した
    /// DifficultyLevelCalculator、2026-07-26)で現在タブのツール値(難易度、参考値)を算出する。
    /// フレーム値は本体の実データと同じ整数フレームに丸めてから渡す(TicksPerBeat等tick単位のままでは
    /// 「10フレーム未満」等の閾値判定が本体と一致しなくなるため)。</summary>
    private string CalculateToolValueLabel(DifficultyTab tab)
    {
        if (_document is null) return "-";
        var engine = _document.Project.CreateTimingEngine();
        long ToFrame(long tick) => (long)Math.Round(engine.TickToFrame(tick), MidpointRounding.AwayFromZero);

        var arrowFramesPerLane = tab.Lanes
            .Select(lane => (IReadOnlyList<long>)lane.Notes.Select(ToFrame).ToList())
            .ToList();
        var freezeFramesPerLane = tab.Lanes
            .Select(lane => (IReadOnlyList<(long Start, long End)>)lane.Freezes
                .Select(f => (ToFrame(f.StartTick), ToFrame(f.EndTick))).ToList())
            .ToList();

        var result = DifficultyLevelCalculator.Calculate(arrowFramesPerLane, freezeFramesPerLane);
        return result.Tool;
    }

    /// <summary>現在のProject/CurrentTabの値をプロパティパネルへ反映する(ドキュメント読込・タブ切替時)。</summary>
    private void RefreshProjectPropertiesPanel()
    {
        if (_document is null) return;
        _suppressPropertyPanelEvents = true;

        var p = _document.Project;
        MusicTitleBox.Text = p.MusicTitle;
        ArtistNameBox.Text = p.ArtistName;
        ArtistUrlBox.Text = p.ArtistUrl;
        BpmBox.Text = (p.BpmEvents.Count > 0 ? p.BpmEvents[0].Bpm : 120).ToString(CultureInfo.InvariantCulture);
        // 2026-07-25: 表示のみ小数点以下2桁に丸める(実値StartNumber自体はフル精度のまま保持)
        StartNumberBox.Text = p.StartNumber.ToString("F2", CultureInfo.InvariantCulture);
        StartFrameBox.Text = p.StartFrame.ToString(CultureInfo.InvariantCulture);
        BlankFrameBox.Text = p.BlankFrame.ToString(CultureInfo.InvariantCulture);
        MusicUrlBox.Text = p.MusicUrl;
        TuningBox.Text = p.Tuning;
        FrzAttemptBox.Text = p.FrzAttempt.ToString(CultureInfo.InvariantCulture);
        AllowNegativeFramePlacementCheck.IsChecked = p.AllowNegativeFramePlacement; // 2026-08-08要望対応(再設計版)

        var tab = _document.CurrentTab;
        DifficultyNameBox.Text = tab.DifficultyName;
        InitialSpeedBox.Text = tab.InitialSpeed.ToString(CultureInfo.InvariantCulture);
        ToolValueText.Text = CalculateToolValueLabel(tab);

        _suppressPropertyPanelEvents = false;

        UpdateRequiredFieldWarning(MusicTitleBox, MusicTitleWarning);
        UpdateRequiredFieldWarning(DifficultyNameBox, DifficultyNameWarning);

        // 2026-07-26: ドキュメント読込・タブ切替のたびに「読込」ボタンの活性状態をリセットする
        // (このビューでmusicURLを編集していない状態からスタート)
        _musicUrlDirty = false;
        UpdateMusicUrlLoadButtonState();
    }

    /// <summary>
    /// 必須項目の未入力表示(仕様書6.7): 空欄なら赤枠+警告テキストを出す共通ルール。
    /// 今後の他タブ(色設定・その他ヘッダー等)の必須項目もこの関数を使い回す想定。
    /// </summary>
    private static void UpdateRequiredFieldWarning(TextBox box, TextBlock warning)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(box.Text);
        warning.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        box.BorderBrush = isEmpty ? Brushes.Red : SystemColors.ActiveBorderBrush;
        box.BorderThickness = new Thickness(isEmpty ? 2 : 1);
    }

    /// <summary>文字列項目: 入力の都度、即座にモデルへ反映する。</summary>
    private void ProjectStringField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressPropertyPanelEvents || _document is null) return;
        var box = (TextBox)sender;
        var p = _document.Project;
        var tab = _document.CurrentTab;

        if (box == MusicTitleBox)
        {
            p.MusicTitle = box.Text;
            UpdateRequiredFieldWarning(MusicTitleBox, MusicTitleWarning);
            ProjectTitleText.Text = $"{p.ProjectName} ({p.MusicTitle})";
        }
        else if (box == ArtistNameBox) p.ArtistName = box.Text;
        else if (box == ArtistUrlBox) p.ArtistUrl = box.Text;
        else if (box == MusicUrlBox)
        {
            p.MusicUrl = box.Text;
            // 2026-07-26: musicURLを編集したら「読込」ボタンを有効化する(機能ON・未読込が前提)
            _musicUrlDirty = true;
            UpdateMusicUrlLoadButtonState();
        }
        else if (box == TuningBox) p.Tuning = box.Text;
        else if (box == DifficultyNameBox)
        {
            tab.DifficultyName = box.Text;
            UpdateRequiredFieldWarning(DifficultyNameBox, DifficultyNameWarning);
            DifficultyTabControl.Items.Refresh(); // DifficultyTabはINotifyPropertyChanged非対応のため明示リフレッシュ
        }
        _document.NotifyChanged();
    }

    /// <summary>2026-08-08要望対応(再設計版): 「マイナスフレームを許容する」チェックボックス。
    /// ONの間、譜面ビューのクリック配置・ペーストでtick&lt;0(frame&lt;0)への配置が可能になる
    /// (SmartToolController.SnappedTickAt/Paste/PasteWithLaneMapping参照、BPMイベントは対象外)。
    /// 新規配置の可否のみを切り替える設定で、既にtick&lt;0にあるオブジェクトへは影響しない。</summary>
    private void AllowNegativeFramePlacementCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPropertyPanelEvents || _document is null) return;
        _document.Project.AllowNegativeFramePlacement = AllowNegativeFramePlacementCheck.IsChecked == true;
        _document.NotifyChanged();
        InvalidateChartViews();
    }

    /// <summary>
    /// 数値項目: 入力の都度ではなくフォーカスが外れた時点で確定する(タイプ中の不完全な文字列で
    /// パースエラーを起こさないため)。パース失敗時はモデルを変更せず、表示だけ直前の値に戻す。
    /// </summary>
    private void ProjectNumericField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var box = (TextBox)sender;
        var p = _document.Project;
        var tab = _document.CurrentTab;
        bool ok;
        // 共通BPM・StartNumberはUndo履歴を通らない直接書換のため、フレーム情報モード中は
        // OnTimingChangedDirectlyで逆算再配置を行う(2026-07-17i)
        bool timingChangedDirectly = false;

        if (box == BpmBox)
        {
            ok = TryParseDouble(box.Text, out var v) && v > 0;
            if (ok && p.BpmEvents.Count > 0 && p.BpmEvents[0].Tick == 0)
            {
                p.BpmEvents[0] = p.BpmEvents[0] with { Bpm = v };
                timingChangedDirectly = true;
            }
        }
        else if (box == StartNumberBox)
        {
            ok = TryParseDouble(box.Text, out var v);
            if (ok) { p.StartNumber = v; timingChangedDirectly = true; }
        }
        else if (box == StartFrameBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.StartFrame = v;
        }
        else if (box == BlankFrameBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.BlankFrame = v;
        }
        else if (box == FrzAttemptBox)
        {
            ok = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            if (ok) p.FrzAttempt = v;
        }
        else if (box == InitialSpeedBox)
        {
            ok = TryParseDouble(box.Text, out var v) && v > 0;
            if (ok) tab.InitialSpeed = v;
        }
        else
        {
            ok = true;
        }

        if (!ok) { RefreshProjectPropertiesPanel(); return; } // 不正入力は直前の値に戻す
        if (timingChangedDirectly) _document.OnTimingChangedDirectly();
        else _document.NotifyChanged();
        InvalidateChartViews();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // =====================================================================
    // 右パネル③: 選択中オブジェクトのプロパティ(仕様書6.4.3)
    // =====================================================================

    private void AutoSwitchToObjectTab()
    {
        if (ObjectTabPinCheck.IsChecked == true) return; // ピン留め中は自動切替しない(仕様書6.4.3)
        ObjectPropertyPane.IsActive = true;
    }

    /// <summary>Document.Changed購読(選択状態を含む変化全般)のたびに呼ばれ、③タブの表示を同期する。</summary>
    private void RefreshSelectedObjectPanel()
    {
        UpdateStartFrameText(); // Changedイベントごとに再生開始フレーム表示も更新する(2026-07-17f、専用購読を増やさないための相乗り)
        RefreshMarkerList(); // 2026-07-26要望対応: マーカー一覧もここへ相乗りで最新化する
        if (_document is null) return;
        var sel = _document.Selection;

        if (sel.Count == 0)
        {
            _currentPropertyObject = null;
            ObjectNoSelectionText.Visibility = Visibility.Visible;
            ObjectMultiSelectText.Visibility = Visibility.Collapsed;
            ObjectDetailPanel.Visibility = Visibility.Collapsed;
            // 2026-08-09要望対応: ③→①への自動復帰も、下の①→③方向(2026-08-01対応)と同じくWPF標準の
            // タブ切替に伴う自動フォーカス移動(新しく表示されたタブの先頭フォーカス可能コントロールへ
            // 自動的にフォーカスが移ってしまう)の対象だが、従来はこちらの方向にだけ対策が漏れていた。
            // グリッドをクリックして選択解除すると、直後のこのタブ自動復帰でフォーカスが①タブのTextBox等へ
            // 奪われ、ショートカットキーが効かなくなる不具合があった。実際にタブを切り替える場合のみ
            // (=このifブロックへ入った場合のみ)Canvasへ明示的に戻す。
            if (ObjectTabPinCheck.IsChecked != true && ObjectPropertyPane.IsActive)
            {
                ProjectPropertyPane.IsActive = true; // 選択解除→プロジェクトタブへ自動復帰(仕様書6.4.3)
                Keyboard.Focus(Canvas);
            }
            return;
        }

        if (sel.Count > 1)
        {
            _currentPropertyObject = null;
            ObjectNoSelectionText.Visibility = Visibility.Collapsed;
            ObjectMultiSelectText.Text = $"{sel.Count}個のオブジェクトを選択中(複数選択時は個別編集非対応。移動・削除はキャンバス上の操作をご利用ください)";
            ObjectMultiSelectText.Visibility = Visibility.Visible;
            ObjectDetailPanel.Visibility = Visibility.Collapsed;
            // 2026-07-26: 複数選択時は右パネルを自動切替しない(単体オブジェクトクリック時のみ切替える方針)。
            // 複数選択のたびに③タブへ切り替わるのが煩わしいというフィードバックへの対応。
            return;
        }

        // --- 単一選択 ---
        // 2026-08-01要望対応: 選択状態がある状態でオブジェクトタブが開いているとShift+A等の
        // ショートカットが効かなくなる不具合の修正。原因は、③タブが「未表示→表示」または
        // 「他タブ→オブジェクトタブ」へ切り替わる瞬間、WPF標準の挙動で新しく表示された
        // ObjectFrameBox(先頭のフォーカス可能コントロール)へ自動的にフォーカスが移ってしまい、
        // それ以降ショートカットがtextInputFocused判定で無効化されていたこと。
        // 既にオブジェクトタブが表示されていた場合(=プロパティ編集のコミット等による再描画)は
        // このWPFの自動フォーカス移動自体が起こらないため、ここでの判定・復帰処理は不要
        // (毎回復帰させるとTabキーでのフィールド間移動や、Enter確定後の継続編集を妨げてしまう)。
        bool objectTabAlreadyShowing = ObjectPropertyPane.IsActive
            && ObjectDetailPanel.Visibility == Visibility.Visible;

        ObjectNoSelectionText.Visibility = Visibility.Collapsed;
        ObjectMultiSelectText.Visibility = Visibility.Collapsed;
        ObjectDetailPanel.Visibility = Visibility.Visible;
        AutoSwitchToObjectTab();
        if (!objectTabAlreadyShowing) Keyboard.Focus(Canvas); // WPFの自動フォーカス移動を打ち消し、譜面ビューへ戻す

        var r = sel.Single();
        _currentPropertyObject = r;
        var engine = _document.Project.CreateTimingEngine();
        var tab = _document.CurrentTab;

        _suppressObjectPanelEvents = true;
        ObjectFrameLabel.Text = "Frame";
        ObjectFrameBox.IsEnabled = true;
        ObjectEndFrameLabel.Visibility = Visibility.Collapsed;
        ObjectEndFrameBox.Visibility = Visibility.Collapsed;
        ObjectValueLabel.Visibility = Visibility.Collapsed;
        ObjectValueBox.Visibility = Visibility.Collapsed;
        ObjectSigNumeratorLabel.Visibility = Visibility.Collapsed;
        ObjectSigNumeratorBox.Visibility = Visibility.Collapsed;
        ObjectSigDenominatorLabel.Visibility = Visibility.Collapsed;
        ObjectSigDenominatorBox.Visibility = Visibility.Collapsed;
        ObjectCommentLabel.Visibility = Visibility.Collapsed;
        ObjectCommentBox.Visibility = Visibility.Collapsed;
        ObjectCommentLabel.Text = "Comment";
        ObjectWarningCheck.Visibility = Visibility.Collapsed;
        ObjectShowCommentIconCheck.Visibility = Visibility.Collapsed;
        ObjectLinkPrevPanel.Visibility = Visibility.Collapsed;
        ObjectLinkNextPanel.Visibility = Visibility.Collapsed;
        ObjectWordPositionLabel.Visibility = Visibility.Collapsed;
        ObjectWordPositionBox.Visibility = Visibility.Collapsed;
        ObjectWordFadeFrameLabel.Visibility = Visibility.Collapsed;
        ObjectWordFadeFrameBox.Visibility = Visibility.Collapsed;

        // 2026-07-26: ノート/フリーズのコメント・警告(Annotations、tick=フリーズはStartTickで同定)を
        // ③タブへ表示する共通処理。マーカーのCommentとは別系統(こちらはlane付きオブジェクト用)。
        void ShowAnnotationFields()
        {
            var a = tab.Lanes[r.Lane].Annotations.FirstOrDefault(x => x.Tick == r.Tick);
            ObjectCommentLabel.Visibility = Visibility.Visible;
            ObjectCommentBox.Visibility = Visibility.Visible;
            ObjectCommentBox.Text = a?.Comment ?? "";
            ObjectWarningCheck.Visibility = Visibility.Visible;
            ObjectWarningCheck.IsChecked = a?.Warning ?? false;
            ObjectShowCommentIconCheck.Visibility = Visibility.Visible;
            ObjectShowCommentIconCheck.IsChecked = a?.ShowIcon ?? false;
        }

        switch (r.Kind)
        {
            case ObjectKind.Note:
                ObjectKindText.Text = "ノート";
                ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));
                ShowAnnotationFields();
                break;

            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var f = tab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                    ObjectKindText.Text = "フリーズアロー";
                    ObjectFrameLabel.Text = "Frame(開始)";
                    ObjectFrameBox.Text = f is null ? "-" : FormatFrame(ToDisplayFrame(engine.TickToFrame(f.StartTick)));
                    ObjectEndFrameLabel.Visibility = Visibility.Visible;
                    ObjectEndFrameBox.Visibility = Visibility.Visible;
                    ObjectEndFrameBox.Text = f is null ? "-" : FormatFrame(ToDisplayFrame(engine.TickToFrame(f.EndTick)));
                    ShowAnnotationFields();
                    break;
                }

            case ObjectKind.Speed:
                {
                    var ev = tab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "速度変更(speed_data)";
                    ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                    ShowValueEventLinkFields(ValueEventKind.Speed, r.Tick);
                    break;
                }

            case ObjectKind.Boost:
                {
                    var ev = tab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "ブースト変更(boost_data)";
                    ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Value ?? 0).ToString(CultureInfo.InvariantCulture);
                    ShowValueEventLinkFields(ValueEventKind.Boost, r.Tick);
                    break;
                }

            case ObjectKind.Bpm:
                {
                    var ev = _document.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick);
                    bool isFixed = r.Tick == 0;
                    ObjectKindText.Text = isFixed ? "BPM変更(曲頭・移動/削除不可)" : "BPM変更";
                    ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));
                    ObjectFrameBox.IsEnabled = !isFixed;
                    ObjectValueLabel.Visibility = Visibility.Visible;
                    ObjectValueBox.Visibility = Visibility.Visible;
                    ObjectValueBox.Text = (ev?.Bpm ?? 120).ToString(CultureInfo.InvariantCulture);
                    ShowValueEventLinkFields(ValueEventKind.Bpm, r.Tick);
                    break;
                }

            case ObjectKind.Marker:
                {
                    var m = _document.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = "マーカー";
                    ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));
                    ObjectCommentLabel.Visibility = Visibility.Visible;
                    ObjectCommentBox.Visibility = Visibility.Visible;
                    ObjectCommentBox.Text = m?.Comment ?? "";
                    break;
                }

            case ObjectKind.Word:
                {
                    var lane = tab.WordLanes[r.Lane];
                    var w = lane.Entries.FirstOrDefault(x => x.Tick == r.Tick);
                    ObjectKindText.Text = $"歌詞({lane.Name}{(lane.IsReverse ? "・Reverse専用" : "")})";
                    ObjectFrameBox.Text = FormatFrame(ToDisplayFrame(engine.TickToFrame(r.Tick)));

                    ObjectWordPositionLabel.Visibility = Visibility.Visible;
                    ObjectWordPositionBox.Visibility = Visibility.Visible;
                    ObjectWordPositionBox.Text = (w?.Position ?? 0).ToString(CultureInfo.InvariantCulture);

                    ObjectCommentLabel.Text = "本文(歌詞、または[fadein]/[fadeout]/[left]/[center]/[right]/[fontSize=XX]の制御キーワード)";
                    ObjectCommentLabel.Visibility = Visibility.Visible;
                    ObjectCommentBox.Visibility = Visibility.Visible;
                    ObjectCommentBox.Text = w?.Text ?? "";

                    bool isFadeControl = w?.Kind == WordEntryKind.Control &&
                        (w.Text.Equals("[fadein]", StringComparison.OrdinalIgnoreCase) || w.Text.Equals("[fadeout]", StringComparison.OrdinalIgnoreCase));
                    ObjectWordFadeFrameLabel.Visibility = isFadeControl ? Visibility.Visible : Visibility.Collapsed;
                    ObjectWordFadeFrameBox.Visibility = isFadeControl ? Visibility.Visible : Visibility.Collapsed;
                    ObjectWordFadeFrameBox.Text = (w?.FadeFrame ?? 30).ToString(CultureInfo.InvariantCulture);
                    break;
                }

            case ObjectKind.TimeSignature:
                {
                    var sig = _document.Project.TimeSignatures.FirstOrDefault(s => s.MeasureIndex == r.Tick);
                    ObjectKindText.Text = sig is null
                        ? "拍子(データ取得失敗)"
                        : $"拍子(小節番号{sig.MeasureIndex})";
                    ObjectFrameLabel.Text = "小節番号";
                    ObjectFrameBox.Text = r.Tick.ToString();
                    ObjectFrameBox.IsEnabled = false; // 小節番号は物理小節頭固定のため移動不可(仕様書7.5)
                    ObjectSigNumeratorLabel.Visibility = Visibility.Visible;
                    ObjectSigNumeratorBox.Visibility = Visibility.Visible;
                    ObjectSigNumeratorBox.Text = (sig?.Numerator ?? 4).ToString(CultureInfo.InvariantCulture);
                    ObjectSigDenominatorLabel.Visibility = Visibility.Visible;
                    ObjectSigDenominatorBox.Visibility = Visibility.Visible;
                    ObjectSigDenominatorBox.Text = (sig?.Denominator ?? 4).ToString(CultureInfo.InvariantCulture);
                    break;
                }
        }
        _suppressObjectPanelEvents = false;
    }

    /// <summary>右パネルの単一行入力欄でEnterキーを押した際、LostFocusを待たずに即座に値を確定させる
    /// (2026-07-26要望対応)。フォーカスを次のコントロールへ移すことで既存のLostFocusハンドラを
    /// そのまま起動させる方式(コミット処理自体は複製しない)。複数行入力(AcceptsReturn=true、
    /// ObjectCommentBox等)は対象外とし、Enterは通常通り改行として機能させる。</summary>
    private void CommitOnEnter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox { AcceptsReturn: true }) return;
        e.Handled = true;
        (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static string FormatFrame(double frame) => frame.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>2026-08-08要望対応: 「フレーム数をblankFrame込みの値で表示する」設定がONの場合、
    /// 内部フレーム値へ右パネルBlankFrameを加算した値を返す(dos.txt出力値と一致させるため)。
    /// OFFの場合は内部フレーム値をそのまま返す(従来動作)。</summary>
    private double ToDisplayFrame(double internalFrame)
        => _appSettings.ShowFrameWithBlankFrame && _document is not null
            ? internalFrame + _document.Project.BlankFrame
            : internalFrame;

    /// <summary>ToDisplayFrameの逆変換。ObjectFrameBox等、blankFrame込みで表示している値を
    /// ユーザーが編集した際、内部フレーム値へ戻すために使う(対称性を保つ)。</summary>
    private double ToInternalFrame(double displayFrame)
        => _appSettings.ShowFrameWithBlankFrame && _document is not null
            ? displayFrame - _document.Project.BlankFrame
            : displayFrame;

    /// <summary>フリーズの選択参照を、リサイズ後の実際の開始tickへ更新する(ResizeFreezeActionはSelectionを
    /// 更新しないため、③タブが古いtickを指したままにならないよう明示的に合わせる)。</summary>
    private void ReselectFreeze(int lane, long newStartTick)
    {
        _document!.Selection.Clear();
        _document.Selection.Add(new ObjectRef(ObjectKind.FreezeStart, lane, newStartTick));
        _document.NotifyChanged();
    }

    private void ObjectFrame_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!TryParseDouble(ObjectFrameBox.Text, out var displayFrame)) { RefreshSelectedObjectPanel(); return; }
        double frame = ToInternalFrame(displayFrame);

        var engine = _document.Project.CreateTimingEngine();
        long newTick = _document.Snap.Snap(engine.FrameToTick(frame));

        if (r.Kind is ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)
        {
            var f = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
            if (f is null) { RefreshSelectedObjectPanel(); return; }
            if (newTick == f.StartTick) return;
            _document.Execute(new ResizeFreezeAction(r.Lane, f, newTick, f.EndTick));
            var moved = _document.CurrentTab.Lanes[r.Lane].Freezes.First(x => x.EndTick == f.EndTick || x.StartTick == newTick);
            ReselectFreeze(r.Lane, moved.StartTick);
            return;
        }

        if (r.Kind == ObjectKind.Bpm && r.Tick == 0) { RefreshSelectedObjectPanel(); return; } // 移動不可(不変条件)

        long tickDelta = newTick - r.Tick;
        if (tickDelta == 0) return;
        _document.Execute(new MoveObjectsAction([r], 0, tickDelta));
    }

    private void ObjectEndFrame_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody)) return;
        if (!TryParseDouble(ObjectEndFrameBox.Text, out var displayFrame)) { RefreshSelectedObjectPanel(); return; }
        double frame = ToInternalFrame(displayFrame);

        var f = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
        if (f is null) { RefreshSelectedObjectPanel(); return; }

        var engine = _document.Project.CreateTimingEngine();
        long newTick = _document.Snap.Snap(engine.FrameToTick(frame));
        if (newTick == f.EndTick) return;

        _document.Execute(new ResizeFreezeAction(r.Lane, f, f.StartTick, newTick));
        var moved = _document.CurrentTab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == f.StartTick)
                    ?? _document.CurrentTab.Lanes[r.Lane].Freezes.First();
        ReselectFreeze(r.Lane, moved.StartTick);
    }

    private void ObjectValue_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!TryParseDouble(ObjectValueBox.Text, out var v)) { RefreshSelectedObjectPanel(); return; }

        switch (r.Kind)
        {
            case ObjectKind.Speed:
                {
                    // 2026-07-30: リンク設定(LinkGridDivision)を削除→再配置後も保持する。
                    var link = _document.CurrentTab.SpeedEvents.FirstOrDefault(x => x.Tick == r.Tick)?.LinkGridDivision;
                    _document.Execute(new CompositeEditAction(
                        [new DeleteValueEventAction(ValueEventKind.Speed, r.Tick), new PlaceValueEventAction(ValueEventKind.Speed, r.Tick, v, link)],
                        "速度変更値編集"));
                    break;
                }

            case ObjectKind.Boost:
                {
                    var link = _document.CurrentTab.BoostEvents.FirstOrDefault(x => x.Tick == r.Tick)?.LinkGridDivision;
                    _document.Execute(new CompositeEditAction(
                        [new DeleteValueEventAction(ValueEventKind.Boost, r.Tick), new PlaceValueEventAction(ValueEventKind.Boost, r.Tick, v, link)],
                        "ブースト変更値編集"));
                    break;
                }

            case ObjectKind.Bpm when r.Tick == 0:
                // tick0のBPMはDelete/Place系アクションの不変条件で弾かれるため直接書き換える
                // (右パネル①のプロジェクト共通BPM欄と同じ扱い、Undo非対応)。
                if (v > 0)
                {
                    var idx = _document.Project.BpmEvents.FindIndex(x => x.Tick == 0);
                    if (idx >= 0)
                    {
                        _document.Project.BpmEvents[idx] = _document.Project.BpmEvents[idx] with { Bpm = v };
                        _document.OnTimingChangedDirectly(); // フレーム情報モード中は全オブジェクトを逆算再配置(2026-07-17i)
                    }
                }
                else RefreshSelectedObjectPanel();
                break;

            case ObjectKind.Bpm:
                if (v > 0)
                {
                    // 2026-08-23: リンク設定(LinkGridDivision)を削除→再配置後も保持する(speed/boostと同様)。
                    var link = _document.Project.BpmEvents.FirstOrDefault(x => x.Tick == r.Tick)?.LinkGridDivision;
                    _document.Execute(new CompositeEditAction(
                        [new DeleteValueEventAction(ValueEventKind.Bpm, r.Tick), new PlaceValueEventAction(ValueEventKind.Bpm, r.Tick, v, link)],
                        "BPM変更値編集"));
                }
                else RefreshSelectedObjectPanel();
                break;
        }
    }

    /// <summary>ノート/フリーズかどうか(コメント・警告Annotationsの対象種別、2026-07-26)</summary>
    private static bool IsAnnotatableKind(ObjectKind kind) =>
        kind is ObjectKind.Note or ObjectKind.FreezeStart or ObjectKind.FreezeEnd or ObjectKind.FreezeBody;

    // =====================================================================
    // speed/boost 始点終点オートスムージング出力(2026-07-30要望対応)
    // =====================================================================

    private static int GridDivisionToComboIndex(int div) => div switch { 4 => 0, 8 => 1, 16 => 2, 32 => 3, _ => 1 };
    private static int ComboIndexToGridDivision(int idx) => idx switch { 0 => 4, 1 => 8, 2 => 16, _ => 32 };

    /// <summary>2026-08-23要望対応(BPMリンク): speed/boost(タブ別のValueEvent)とBPM(プロジェクト共通の
    /// BpmEvent)は型が異なるため、リンクUI側では(Tick, LinkGridDivision)のタプル列へ統一して扱う。</summary>
    private List<(long Tick, int? Link)> GetValueEventTicksAndLinks(ValueEventKind kind) => kind switch
    {
        ValueEventKind.Speed => _document!.CurrentTab.SpeedEvents.OrderBy(e => e.Tick).Select(e => (e.Tick, e.LinkGridDivision)).ToList(),
        ValueEventKind.Boost => _document!.CurrentTab.BoostEvents.OrderBy(e => e.Tick).Select(e => (e.Tick, e.LinkGridDivision)).ToList(),
        _ => _document!.Project.BpmEvents.OrderBy(e => e.Tick).Select(e => (e.Tick, e.LinkGridDivision)).ToList(),
    };

    /// <summary>③タブでspeed/boost/BPMマーカーを選択した際、「前の同種マーカーとのリンク」「次の同種マーカーとの
    /// リンク」の2パネルを、直近手前・直近直後の同種イベントの有無に応じて表示/更新する。
    /// リンクは常に「tick順で早い方のイベントがLinkGridDivisionを持つ」形で内部表現しているため、
    /// 「前とのリンク」パネルは直前のイベント自身のLinkGridDivisionを、「次とのリンク」パネルは
    /// 選択中のイベント自身のLinkGridDivisionを、それぞれ参照/更新する。
    /// 2026-08-23要望対応: BPM変更マーカーでも共用する(BPMは「値」ではなく「拍位置に対する直線ランプ」
    /// という意味になるが、UIの見た目・操作感はspeed/boostと統一する)。</summary>
    private void ShowValueEventLinkFields(ValueEventKind kind, long tick)
    {
        var sorted = GetValueEventTicksAndLinks(kind);
        int idx = sorted.FindIndex(e => e.Tick == tick);
        if (idx < 0)
        {
            ObjectLinkPrevPanel.Visibility = Visibility.Collapsed;
            ObjectLinkNextPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (idx > 0)
        {
            var prev = sorted[idx - 1];
            ObjectLinkPrevPanel.Visibility = Visibility.Visible;
            ObjectLinkPrevCheck.IsChecked = prev.Link is not null;
            ObjectLinkPrevGridCombo.IsEnabled = prev.Link is not null;
            ObjectLinkPrevGridCombo.SelectedIndex = GridDivisionToComboIndex(prev.Link ?? 8);
        }
        else
        {
            ObjectLinkPrevPanel.Visibility = Visibility.Collapsed;
        }

        if (idx + 1 < sorted.Count)
        {
            var self = sorted[idx];
            ObjectLinkNextPanel.Visibility = Visibility.Visible;
            ObjectLinkNextCheck.IsChecked = self.Link is not null;
            ObjectLinkNextGridCombo.IsEnabled = self.Link is not null;
            ObjectLinkNextGridCombo.SelectedIndex = GridDivisionToComboIndex(self.Link ?? 8);
        }
        else
        {
            ObjectLinkNextPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static ValueEventKind ToValueEventKind(ObjectKind kind) => kind switch
    {
        ObjectKind.Speed => ValueEventKind.Speed,
        ObjectKind.Boost => ValueEventKind.Boost,
        _ => ValueEventKind.Bpm,
    };

    private void ObjectLinkPrev_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.Speed or ObjectKind.Boost or ObjectKind.Bpm)) return;
        var kind = ToValueEventKind(r.Kind);
        var sorted = GetValueEventTicksAndLinks(kind);
        int idx = sorted.FindIndex(x => x.Tick == r.Tick);
        if (idx <= 0) return;
        var prev = sorted[idx - 1];

        bool linked = ObjectLinkPrevCheck.IsChecked == true;
        int div = ComboIndexToGridDivision(ObjectLinkPrevGridCombo.SelectedIndex < 0 ? 1 : ObjectLinkPrevGridCombo.SelectedIndex);
        if (linked == (prev.Link is not null)) return; // 変化なし
        _document.Execute(new SetValueEventLinkAction(kind, prev.Tick, linked ? div : null));
        RefreshSelectedObjectPanel();
    }

    private void ObjectLinkPrevGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.Speed or ObjectKind.Boost or ObjectKind.Bpm)) return;
        if (ObjectLinkPrevCheck.IsChecked != true) return; // 未リンク時のコンボ初期化は無視(チェック時に反映)
        var kind = ToValueEventKind(r.Kind);
        var sorted = GetValueEventTicksAndLinks(kind);
        int idx = sorted.FindIndex(x => x.Tick == r.Tick);
        if (idx <= 0) return;
        var prev = sorted[idx - 1];
        int div = ComboIndexToGridDivision(ObjectLinkPrevGridCombo.SelectedIndex);
        if (prev.Link == div) return;
        _document.Execute(new SetValueEventLinkAction(kind, prev.Tick, div));
    }

    private void ObjectLinkNext_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.Speed or ObjectKind.Boost or ObjectKind.Bpm)) return;
        var kind = ToValueEventKind(r.Kind);
        var sorted = GetValueEventTicksAndLinks(kind);
        int selfIdx = sorted.FindIndex(x => x.Tick == r.Tick);
        if (selfIdx < 0) return;
        var self = sorted[selfIdx];

        bool linked = ObjectLinkNextCheck.IsChecked == true;
        int div = ComboIndexToGridDivision(ObjectLinkNextGridCombo.SelectedIndex < 0 ? 1 : ObjectLinkNextGridCombo.SelectedIndex);
        if (linked == (self.Link is not null)) return; // 変化なし
        _document.Execute(new SetValueEventLinkAction(kind, r.Tick, linked ? div : null));
        RefreshSelectedObjectPanel();
    }

    private void ObjectLinkNextGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (r.Kind is not (ObjectKind.Speed or ObjectKind.Boost or ObjectKind.Bpm)) return;
        if (ObjectLinkNextCheck.IsChecked != true) return;
        var kind = ToValueEventKind(r.Kind);
        var sorted = GetValueEventTicksAndLinks(kind);
        int selfIdx = sorted.FindIndex(x => x.Tick == r.Tick);
        if (selfIdx < 0) return;
        var self = sorted[selfIdx];
        int div = ComboIndexToGridDivision(ObjectLinkNextGridCombo.SelectedIndex);
        if (self.Link == div) return;
        _document.Execute(new SetValueEventLinkAction(kind, r.Tick, div));
    }

    /// <summary>拍子(TimeSignature)の分子/分母編集(2026-07-26要望対応)。既存のPlaceTimeSignatureActionは
    /// 「同じ小節番号の既存拍子を削除→新しい拍子を追加」を1操作でUndo対応しているため、そのまま
    /// 「編集」用途にも流用できる(小節番号自体は不変、値だけが変わる)。</summary>
    private void ObjectTimeSignature_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { Kind: ObjectKind.TimeSignature } r) return;
        if (!int.TryParse(ObjectSigNumeratorBox.Text, out var num) || num < 1 ||
            !int.TryParse(ObjectSigDenominatorBox.Text, out var denom) || denom < 1)
        { RefreshSelectedObjectPanel(); return; }

        var sig = _document.Project.TimeSignatures.FirstOrDefault(s => s.MeasureIndex == r.Tick);
        if (sig is not null && sig.Numerator == num && sig.Denominator == denom) return; // 変更なしならUndo履歴を汚さない

        _document.Execute(new PlaceTimeSignatureAction((int)r.Tick, num, denom));
    }

    private void ObjectComment_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;

        if (r.Kind == ObjectKind.Marker)
        {
            var m = _document.Project.Markers.FirstOrDefault(x => x.Tick == r.Tick);
            if (m is null || m.Comment == ObjectCommentBox.Text) return; // 変更なしならUndo履歴を汚さない
            _document.Execute(new CompositeEditAction(
                [new DeleteMarkerAction(r.Tick), new PlaceMarkerAction(r.Tick, ObjectCommentBox.Text)],
                "マーカーコメント編集"));
            return;
        }

        if (r.Kind == ObjectKind.Word) { CommitWordEntryEdit(r); return; } // 2026-07-23(TBD 4)

        // 2026-07-26: ノート/フリーズのコメント編集(Annotations)
        if (!IsAnnotatableKind(r.Kind)) return;
        var a = _document.CurrentTab.Lanes[r.Lane].Annotations.FirstOrDefault(x => x.Tick == r.Tick);
        if ((a?.Comment ?? "") == ObjectCommentBox.Text) return; // 変更なしならUndo履歴を汚さない
        _document.Execute(new SetAnnotationAction(r.Lane, r.Tick, ObjectCommentBox.Text, a?.Warning ?? false, a?.ShowIcon ?? false));
        InvalidateChartViews();
    }

    /// <summary>歌詞エントリ(Word)専用フィールド(段/フェードフレーム数)のLostFocus共通処理(2026-07-23、TBD 4)。</summary>
    private void ObjectWordEntry_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { Kind: ObjectKind.Word } r) return;
        CommitWordEntryEdit(r);
    }

    /// <summary>歌詞エントリの本文(ObjectCommentBox)・段(ObjectWordPositionBox)・
    /// フェードフレーム数(ObjectWordFadeFrameBox)をまとめて1つのEditWordEntryActionとして確定する
    /// (2026-07-23、TBD 4)。種別(通常歌詞/制御)は本文が"[...]"形式かどうかで自動判定する
    /// (専用UIは設けない、ユーザー確定仕様)。</summary>
    private void CommitWordEntryEdit(ObjectRef r)
    {
        var lane = _document!.CurrentTab.WordLanes[r.Lane];
        var w = lane.Entries.FirstOrDefault(x => x.Tick == r.Tick);
        if (w is null) return;

        if (!int.TryParse(ObjectWordPositionBox.Text, out var position)) { RefreshSelectedObjectPanel(); return; }
        string text = ObjectCommentBox.Text;
        bool isControl = text.Length >= 2 && text[0] == '[' && text[^1] == ']';
        bool isFadeControl = isControl && (text.Equals("[fadein]", StringComparison.OrdinalIgnoreCase) || text.Equals("[fadeout]", StringComparison.OrdinalIgnoreCase));
        int? fadeFrame = null;
        if (isFadeControl)
        {
            if (!int.TryParse(ObjectWordFadeFrameBox.Text, out var ff)) { RefreshSelectedObjectPanel(); return; }
            fadeFrame = ff;
        }

        var kind = isControl ? WordEntryKind.Control : WordEntryKind.Lyrics;
        var newEntry = new WordEntry(r.Tick, position, kind, text, fadeFrame);
        if (w.Position == newEntry.Position && w.Kind == newEntry.Kind && w.Text == newEntry.Text && w.FadeFrame == newEntry.FadeFrame)
            return; // 変更なしならUndo履歴を汚さない

        _document.Execute(new EditWordEntryAction(r.Lane, r.Tick, newEntry));
        InvalidateChartViews();
    }

    /// <summary>警告フラグのON/OFF(2026-07-26)。インポート時に自動ONになったものを、内容確認後に
    /// ユーザーが手動でOFFにするのが主用途(自動クリアはしない仕様)。手動ONも可能。</summary>
    private void ObjectWarning_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!IsAnnotatableKind(r.Kind)) return;

        bool warning = ObjectWarningCheck.IsChecked == true;
        var a = _document.CurrentTab.Lanes[r.Lane].Annotations.FirstOrDefault(x => x.Tick == r.Tick);
        if ((a?.Warning ?? false) == warning) return; // 変更なしならUndo履歴を汚さない
        _document.Execute(new SetAnnotationAction(r.Lane, r.Tick, a?.Comment ?? ObjectCommentBox.Text, warning, a?.ShowIcon ?? false));
        InvalidateChartViews();
    }

    /// <summary>コメントお知らせアイコン表示フラグのON/OFF(2026-07-30要望対応)。Warningとは独立した
    /// ユーザー任意のチェックボックスで、ONの間は譜面ビューにコメント有りお知らせアイコン
    /// (SystemIcons.Application)を重ね描きする。</summary>
    private void ObjectShowCommentIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressObjectPanelEvents || _document is null || _currentPropertyObject is not { } r) return;
        if (!IsAnnotatableKind(r.Kind)) return;

        bool showIcon = ObjectShowCommentIconCheck.IsChecked == true;
        var a = _document.CurrentTab.Lanes[r.Lane].Annotations.FirstOrDefault(x => x.Tick == r.Tick);
        if ((a?.ShowIcon ?? false) == showIcon) return; // 変更なしならUndo履歴を汚さない
        _document.Execute(new SetAnnotationAction(r.Lane, r.Tick, a?.Comment ?? ObjectCommentBox.Text, a?.Warning ?? false, showIcon));
        InvalidateChartViews();
    }

    /// <summary>波形表示トグル(2026-07-18)。初回ONで音声をバックグラウンドデコードする</summary>
    private void WaveformToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        bool on = WaveformToggle.IsChecked == true;
        Canvas.ShowWaveform = on;
        if (on)
        {
            if (!_audioLoaded)
            {
                MessageBox.Show(this, "音楽ファイルが読み込まれていません。波形表示には音楽の読み込みが必要です。", "波形表示", MessageBoxButton.OK, MessageBoxImage.Information);
                WaveformToggle.IsChecked = false;
                return;
            }
            EnsureWaveformDecoded();
        }
        InvalidateChartViews();
    }

    /// <summary>音声ファイルをデコードして波形ピークを用意する(非同期、結果はキャッシュ)(2026-07-18)</summary>
    private async void EnsureWaveformDecoded()
    {
        if (_document is null || _waveformDecoding) return;
        var path = _document.Project.AudioFilePath;
        if (path == _waveformPath && _waveformPeaks is not null)
        {
            Canvas.Waveform = _waveformPeaks;
            InvalidateChartViews();
            return;
        }
        _waveformDecoding = true;
        try
        {
            var peaks = await System.Threading.Tasks.Task.Run(() => WaveformDecoder.Decode(path));
            _waveformPeaks = peaks;
            _waveformPath = path;
            Canvas.Waveform = peaks;
            InvalidateChartViews();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"波形の解析に失敗しました: {ex.Message}\n(ogg等、未対応の形式の可能性があります)", "波形表示", MessageBoxButton.OK, MessageBoxImage.Warning);
            WaveformToggle.IsChecked = false;
            Canvas.ShowWaveform = false;
        }
        finally
        {
            _waveformDecoding = false;
        }
    }

    /// <summary>StartNumber編集モード切替(2026-07-18、要望メモ07-15項目8)。
    /// ON中は通常編集無効・ドラッグ=StartNumber調整・クリック=ガイド線。波形は自動ON(手動OFF可)。
    /// フレーム情報モードとは排他(フレーム固定とStartNumber移動は意味が衝突するため)。</summary>
    private void StartNumberEditToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        bool on = StartNumberEditToggle.IsChecked == true;
        if (on)
        {
            if (_document is null)
            {
                StartNumberEditToggle.IsChecked = false;
                return;
            }
            if (_document.IsFrameEditMode)
            {
                MessageBox.Show(this, "フレーム情報モード中はStartNumber編集モードに切り替えられません。先にフレーム情報モードを終了してください。", "StartNumber編集", MessageBoxButton.OK, MessageBoxImage.Information);
                StartNumberEditToggle.IsChecked = false;
                return;
            }
            Canvas.StartNumberEditMode = true;
            // モード依存デフォルト: 波形は自動ON(要望メモ07-15項目7。手動でOFFにも戻せる)
            if (WaveformToggle.IsChecked != true && _audioLoaded) WaveformToggle.IsChecked = true;
        }
        else
        {
            Canvas.StartNumberEditMode = false;
            RefreshProjectPropertiesPanel(); // StartNumber数値表示を最終同期
        }
        InvalidateChartViews();
    }

    /// <summary>拍情報/フレーム情報モード切替(仕様書7.6、2026-07-17i)。OFF時は丸め衝突を検査し、
    /// 「統合して続行」か「フレーム情報モードに留まる」かをユーザーが選ぶ(確定仕様)。</summary>
    private void FrameEditToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _document is null)
        {
            if (_document is null) FrameEditToggle.IsChecked = false;
            return;
        }
        bool on = FrameEditToggle.IsChecked == true;
        if (on == _document.IsFrameEditMode) return; // 再入(IsChecked書き戻し時)ガード

        if (on)
        {
            if (Canvas.StartNumberEditMode)
            {
                MessageBox.Show(this, "StartNumber編集モード中はフレーム情報モードに切り替えられません。先にStartNumber編集を終了してください。", "フレーム情報モード", MessageBoxButton.OK, MessageBoxImage.Information);
                FrameEditToggle.IsChecked = false;
                return;
            }
            _document.EnterFrameEditMode();
        }
        else
        {
            var collisions = _document.FindFrameEditCollisions();
            if (collisions.Count > 0)
            {
                var head = string.Join("\n", collisions.Take(10));
                var more = collisions.Count > 10 ? $"\n…ほか{collisions.Count - 10}件" : "";
                var r = MessageBox.Show(this,
                    $"丸め込みにより同一位置へ重なったオブジェクトがあります:\n{head}{more}\n\n" +
                    "「はい」= 重複を統合して拍情報モードへ戻る(統合はUndo可能)\n「いいえ」= フレーム情報モードに留まる",
                    "フレーム情報モード終了", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                {
                    FrameEditToggle.IsChecked = true; // モード継続
                    return;
                }
                _document.ExitFrameEditMode(mergeDuplicates: true);
            }
            else
            {
                _document.ExitFrameEditMode(mergeDuplicates: false);
            }
        }
        InvalidateChartViews();
    }

    private void SnapToggle_Changed(object sender, RoutedEventArgs e) => ApplySnapToDocument();
    private void SnapDivision_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplySnapToDocument();
        SnapKeyboardCursorToNearestGrid(); // 2026-07-26: 上部パネルのプルダウンからの分解能変更も対象に含める
    }

    private void ApplySnapToDocument()
    {
        if (_document is null) return;
        _document.Snap.Enabled = SnapEnabledCheck.IsChecked == true;
        if (SnapDivisionCombo.SelectedItem is int division)
        {
            _document.Snap.Division = division;
            // 2026-08-02要望対応: スナップ分解能をプロジェクトファイルへ永続化する
            // (再起動・開き直し後も前回値を復元できるようにする)。
            _document.Project.SnapDivision = division;
        }
        InvalidateChartViews();
    }

    // --- 2026-07-26: Ctrl+1〜9,0,-,^ グリッド分解能ショートカット ---

    /// <summary>数字キー列の物理キー(Ctrl+Shift+1〜9)をキーマクロのスロット番号(1〜9)へ変換する
    /// (2026-07-26要望対応)。テンキーは対象外(グリッド分解能ショートカットと同じ慣習)。</summary>
    private static int? KeyMacroSlot(Key key) => key switch
    {
        Key.D1 => 1,
        Key.D2 => 2,
        Key.D3 => 3,
        Key.D4 => 4,
        Key.D5 => 5,
        Key.D6 => 6,
        Key.D7 => 7,
        Key.D8 => 8,
        Key.D9 => 9,
        _ => null,
    };

    /// <summary>数字キー列の物理キー(Ctrl+1,2,...,9,0,-,^)を0〜11の位置インデックスへ変換する。
    /// テンキーは対象外(SKBエディタの慣習に合わせ、メイン列のみ)。対応外のキーはnull。</summary>
    private static int? GridShortcutKeyIndex(Key key) => key switch
    {
        Key.D1 => 0,
        Key.D2 => 1,
        Key.D3 => 2,
        Key.D4 => 3,
        Key.D5 => 4,
        Key.D6 => 5,
        Key.D7 => 6,
        Key.D8 => 7,
        Key.D9 => 8,
        Key.D0 => 9,
        Key.OemMinus => 10, // "-"(JIS/US共通の物理位置)
        Key.OemPlus => 11,  // "^"(JIS配列で0の右隣。US配列の"="と同じ物理キー、WPFのOem*名はスキャンコード基準)
        _ => null,
    };

    /// <summary>環境設定で選んだプリセット(GridShortcutPreset)に従い、指定インデックスに
    /// 対応する分解能をスナップへ適用する。ショートカットで分解能を選んだ場合はスナップ自体も
    /// 自動的に有効化する(ユーザーが明示的に分解能を選ぶ操作なので、スナップOFFのままだと
    /// 意図が反映されず分かりにくいための挙動、2026-07-26設計判断)。</summary>
    private void ApplyGridShortcut(int index)
    {
        if (_document is null) return;
        var division = GridShortcutPresets.DivisionForIndex(_appSettings.GridShortcutPreset, index);
        if (division is null) return;
        _document.Snap.Enabled = true;
        SnapEnabledCheck.IsChecked = true;
        _document.Snap.Division = division.Value;
        SnapDivisionCombo.SelectedItem = division.Value;
        SnapKeyboardCursorToNearestGrid();
        InvalidateChartViews();
    }

    /// <summary>キーボードモード中は「再生開始ライン」(PlaybackStartFrame)がそのままカーソル位置を
    /// 兼ねている。グリッド分解能を変えた瞬間、カーソルが旧グリッドには沿っていても新グリッドには
    /// 沿っていない「半端な位置」のまま取り残されてしまうため、常に現在位置から最も近い新グリッド線へ
    /// スナップし直す(2026-07-26要望対応、Ctrl+数字ショートカット/上部パネルのプルダウン両方から呼ぶ)。
    /// 2026-08-01不具合修正: スナップOFF中に上部パネルの分解能プルダウンだけを操作した場合、
    /// スナップ自体はOFFのままなのにここが無条件にグリッドスナップを適用してしまい、
    /// 「スナップをオフにしてもスナップしてしまう」不具合になっていた。Snap.Enabled=falseなら
    /// 何もしない(ApplyGridShortcut経由の場合は呼び出し前にEnabled=trueへ変更済みのため影響なし)。</summary>
    private void SnapKeyboardCursorToNearestGrid()
    {
        if (_document is null || !_keyboardModeActive || !_document.Snap.Enabled
            || _document.CurrentTab.PlaybackStartFrame is not { } f) return;
        var engine = _document.Project.CreateTimingEngine();
        long cur = (long)Math.Round(engine.FrameToTick(f));
        long step = _document.Snap.GridTicks;
        long snapped = Math.Max(0, (long)Math.Round((double)cur / step) * step);
        _document.CurrentTab.PlaybackStartFrame = engine.TickToFrame(snapped);
        ScrollKeyboardCursorIntoView();
        _document.NotifyChanged(markModified: false);
    }

    // =====================================================================
    // スクロール連動(ChartCanvasの可視範囲カリング用)
    // =====================================================================

    private void ChartScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var rect = new Rect(ChartScrollViewer.HorizontalOffset, ChartScrollViewer.VerticalOffset,
            ChartScrollViewer.ViewportWidth, ChartScrollViewer.ViewportHeight);
        Canvas.UpdateViewport(rect);
        Minimap.InvalidateVisual(); // 2026-07-26: 現在の表示範囲インジケータを最新化
        LaneHeaderBar1.UpdateHorizontalOffset(rect.Left); // 2026-08-08: ヘッダーの列位置を横スクロールに追従させる
    }

    /// <summary>分割ビュー(2026-07-26要望対応)の右ペイン(Canvas2)用スクロール連動。左ペインとは
    /// 完全に独立したスクロール位置を持つため、ChartScrollViewer_ScrollChangedとは別にビューポートを
    /// 計算する(ミニマップは左ペイン基準のまま、右ペインには連動させない)。</summary>
    private void ChartScrollViewer2_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var rect = new Rect(ChartScrollViewer2.HorizontalOffset, ChartScrollViewer2.VerticalOffset,
            ChartScrollViewer2.ViewportWidth, ChartScrollViewer2.ViewportHeight);
        Canvas2.UpdateViewport(rect);
        Minimap2.InvalidateVisual(); // 2026-07-26b: 右ペイン用ミニマップの表示範囲インジケータを最新化
        LaneHeaderBar2.UpdateHorizontalOffset(rect.Left); // 2026-08-08: ヘッダーの列位置を横スクロールに追従させる
    }

    /// <summary>2026-08-01要望対応: 譜面ビュー内でもスクロールバー等、ChartCanvas自身が
    /// マウスイベントを受け取らない領域(ScrollViewerの既定テンプレートが処理する部分)をクリックすると、
    /// ChartCanvas.OnMouseXXXButtonDownのFocus()呼び出しが発生せず、以前フォーカスを持っていた
    /// 他コントロール(ツールバーのテキストボックス等)にフォーカスが残ったままになり、結果として
    /// ショートカットキーがtextInputFocused判定で無効化されてしまう不具合があった。
    /// ChartMinimap.RestoreFocusと同様に、ScrollViewer内でのクリックをPreviewMouseDownで検知し、
    /// 対応するChartCanvasへ明示的にフォーカスを戻す。</summary>
    private void ChartScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Keyboard.Focus(Canvas);

    /// <summary>右ペイン(Canvas2)用。ChartScrollViewer_PreviewMouseDown参照。</summary>
    private void ChartScrollViewer2_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Keyboard.Focus(Canvas2);

    /// <summary>2026-08-09要望対応: ChartCanvasの実描画幅(レーン列合計)がペインの表示幅より狭い場合、
    /// BPMレーンより右の余白部分はChartCanvas/ScrollViewerどちらの描画範囲にも含まれず、既定のままだと
    /// クリックしてもフォーカスがどこにも移らなかった(MainWindow.xaml側でこのGridにBackground=
    /// "Transparent"を設定してヒットテスト可能にした上で、ここへ委譲させている)。
    /// ChartScrollViewer_PreviewMouseDownと同じ「Canvasへ明示的にフォーカスを戻す」役割。</summary>
    private void ChartPane1HostGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Keyboard.Focus(Canvas);

    /// <summary>右ペイン(Canvas2)用。ChartPane1HostGrid_PreviewMouseDown参照。</summary>
    private void ChartPane2HostGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Keyboard.Focus(Canvas2);

    /// <summary>2026-08-01要望対応(2026-09-21: AvalonDock移行に伴い実装変更)。右パネルの表示内容を
    /// 切り替えると、WPF標準の挙動により新しく表示された内容内の先頭フォーカス可能コントロール
    /// (TextBox等)へフォーカスが移ってしまい、以降ショートカットキーが譜面ビューに届かなくなる
    /// 不具合があった。従来はマウスクリックによるタブ切替のみを対象としていたが(TabItemの
    /// PreviewMouseLeftButtonDownを利用)、LayoutAnchorableはFrameworkElementではなくマウスイベントを
    /// 持たないため、DockingManager.ActiveContentChanged(マウス・キーボード・コードいずれの原因でも
    /// 発火)を使い、原因を問わず常にDispatcher経由でCanvasへフォーカスを戻す方式へ単純化した
    /// (意図的な挙動変更。キーボード操作時のTabキー移動等に支障が出ないか実機確認で要確認)。</summary>
    private void PropertyDockingManager_ActiveContentChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(Canvas)), DispatcherPriority.Input);
    }

    /// <summary>2026-09-21要望対応: 右パネルのタブをフローティング(切り離し)した状態で閉じる(×)と、
    /// AvalonDockの既定挙動ではCanClose="False"のため完全には削除されず「非表示(Hidden)」状態になるが、
    /// 再表示する手段がUI上に無かった。「表示」メニュー内の「右パネル」サブメニューを開くたびに、
    /// 現在ドッキング中・フローティング中・非表示中を問わず全ての右パネルタブ(固定10個+プラグイン
    /// パネル動的追加分)を洗い出してチェック付きメニュー項目を作り直す(RecentFilesMenu_SubmenuOpenedと
    /// 同じ動的構築パターン)。チェックはIsVisibleと連動し、クリックでShow()/Hide()を切り替える。
    /// 2026-09-21追記(並び順固定要望対応): 当初DockingManager.Layout.Descendents()/Hiddenを走査する
    /// 実装だったが、この走査順はドッキング中・フローティング中・非表示中のどれかで変わりうるため、
    /// 表示状態を切り替えるたびにメニューの並びが変わってしまう不具合があった。固定10タブは
    /// XAML宣言順の配列、プラグインパネルは追加時に記録した_pluginPanelAnchorablesを使い、
    /// AvalonDock内部の走査順に一切依存しない固定順で列挙する。</summary>
    private void RightPanelViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RightPanelViewMenu.Items.Clear();

        LayoutAnchorable[] fixedOrder =
        {
            ProjectPropertyPane, ColorSettingsPane, ObjectPropertyPane, OtherPropertyPane,
            ColorEditTabItem, MacroTabItem, MarkerTabItem, LinkTabItem,
            AnalysisTabItem, PreviewTabItem, ParticipantsTabItem,
        };

        foreach (var anchorable in fixedOrder.Concat(_pluginPanelAnchorables))
        {
            var item = new MenuItem { Header = anchorable.Title, IsCheckable = true, IsChecked = anchorable.IsVisible };
            item.Click += (_, _) =>
            {
                if (anchorable.IsVisible) anchorable.Hide();
                else anchorable.Show();
            };
            RightPanelViewMenu.Items.Add(item);
        }
    }

    // =====================================================================
    // 譜面ビュー分割表示(2026-07-26要望対応、第三者提案)
    // =====================================================================

    /// <summary>上パネルの分割トグル。ON/OFFをAppSettingsへ永続化し、レイアウトを切り替える。</summary>
    private void SplitViewToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _splitViewEnabled = SplitViewToggle.IsChecked == true;
        _appSettings.SplitViewEnabled = _splitViewEnabled;
        ApplySplitViewLayout();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>分割ON/OFFに応じて列幅・可視性を切り替え、ONにした直後はCanvas2の表示状態を
    /// Canvas(左ペイン)から同期する(スクロール位置だけは独立、それ以外の表示設定・Document・
    /// Controllerは共有)。分割をONにするたびアクティブペインは左(既定)にリセットする。</summary>
    private void ApplySplitViewLayout()
    {
        if (_splitViewEnabled)
        {
            ChartPaneColumn1.Width = new GridLength(1, GridUnitType.Star);
            ChartSplitterColumn.Width = new GridLength(4);
            ChartPaneColumn2.Width = new GridLength(1, GridUnitType.Star);
            Minimap2Column.Width = new GridLength(48);
            ChartSplitGridSplitter.Visibility = Visibility.Visible;
            ChartPane2Border.Visibility = Visibility.Visible;
            Minimap2.Visibility = Visibility.Visible;
            _activePaneIsSecondary = false;
            SyncCanvas2FromCanvas();
            SyncMinimap2FromDocument();
            Canvas2.InvalidateVisual();
            Minimap2.InvalidateVisual();
        }
        else
        {
            ChartSplitterColumn.Width = new GridLength(0);
            ChartPaneColumn2.Width = new GridLength(0);
            Minimap2Column.Width = new GridLength(0);
            ChartSplitGridSplitter.Visibility = Visibility.Collapsed;
            ChartPane2Border.Visibility = Visibility.Collapsed;
            Minimap2.Visibility = Visibility.Collapsed;
        }
        UpdateActivePaneIndicator();
    }

    /// <summary>レーンラベルヘッダー(LaneHeaderBar)の表示位置を環境設定(AppSettings.LaneLabelHeaderPosition:
    /// "top"/"bottom"/"hidden")へ合わせて左右ペインへ適用する(2026-08-08要望対応)。
    /// ScrollViewerと同じGrid内の別行(Row0=上部、Row2=下部)へLaneHeaderBarを配置し、使わない側の行高は
    /// 0にする(RowDefinition自体は常に両方存在させ、Grid.SetRowで配置行だけ切り替える方式)。</summary>
    private void ApplyLaneLabelHeaderPosition()
    {
        ApplyLaneLabelHeaderPositionForPane(LaneHeaderBar1, ChartPane1HeaderTopRow, ChartPane1HeaderBottomRow);
        ApplyLaneLabelHeaderPositionForPane(LaneHeaderBar2, ChartPane2HeaderTopRow, ChartPane2HeaderBottomRow);
    }

    private void ApplyLaneLabelHeaderPositionForPane(LaneHeaderBar bar, RowDefinition topRow, RowDefinition bottomRow)
    {
        switch (_appSettings.LaneLabelHeaderPosition)
        {
            case "bottom":
                Grid.SetRow(bar, 2);
                bar.Visibility = Visibility.Visible;
                topRow.Height = new GridLength(0);
                bottomRow.Height = GridLength.Auto;
                break;
            case "hidden":
                bar.Visibility = Visibility.Collapsed;
                topRow.Height = new GridLength(0);
                bottomRow.Height = new GridLength(0);
                break;
            default: // "top"(既定)
                Grid.SetRow(bar, 0);
                bar.Visibility = Visibility.Visible;
                topRow.Height = GridLength.Auto;
                bottomRow.Height = new GridLength(0);
                break;
        }
        bar.InvalidateMeasure();
        bar.InvalidateVisual();
    }

    /// <summary>キーボードモード中のカーソル追従スクロール(ScrollKeyboardCursorIntoView)が対象とする
    /// ScrollViewer。分割OFF、またはアクティブペインが左の間は従来通りChartScrollViewer、分割ON中に
    /// 右ペインをアクティブにしている間だけChartScrollViewer2を返す(2026-07-26要望対応:
    /// 「1アクションで両方のペインが動くのは避けたい」ため、非アクティブ側は一切追従させない)。</summary>
    private ScrollViewer ActiveChartScrollViewer =>
        _splitViewEnabled && _activePaneIsSecondary ? ChartScrollViewer2 : ChartScrollViewer;

    /// <summary>キーボードモード中、Tabキーでアクティブペインを切り替える(2026-07-26要望対応)。
    /// 分割OFF中は呼ばれない(HandleKeyboardModeKey側でガード済み)。</summary>
    private void TogglePaneActive()
    {
        _activePaneIsSecondary = !_activePaneIsSecondary;
        UpdateActivePaneIndicator();
        StatusText.Text = _activePaneIsSecondary ? "分割ビュー: 右側がアクティブです" : "分割ビュー: 左側がアクティブです";
    }

    /// <summary>アクティブペインの目印(枠線)を更新する。分割ONかつキーボードモード中のみ表示する
    /// (マウスモード中・分割OFF中はどちらのペインで操作しても同じなので目印は不要、2026-07-26要望対応)。</summary>
    private void UpdateActivePaneIndicator()
    {
        bool show = _splitViewEnabled && _keyboardModeActive;
        bool leftActive = show && !_activePaneIsSecondary;
        bool rightActive = show && _activePaneIsSecondary;
        ChartPane1Border.BorderBrush = leftActive ? ActivePaneIndicatorBrush : Brushes.Transparent;
        ChartPane1Border.BorderThickness = new Thickness(leftActive ? 3 : 0);
        ChartPane2Border.BorderBrush = rightActive ? ActivePaneIndicatorBrush : Brushes.Transparent;
        ChartPane2Border.BorderThickness = new Thickness(rightActive ? 3 : 0);
    }

    /// <summary>Canvas(左ペイン)の再描画に合わせてCanvas2(右ペイン、分割ON時のみ)も再描画する
    /// (2026-07-26要望対応)。これまで散在していたCanvas.InvalidateVisual()呼び出しをすべて
    /// このメソッド経由に統一することで、両ペインの再描画・表示設定同期を1箇所に集約している。</summary>
    private void InvalidateChartViews()
    {
        Canvas.InvalidateVisual();
        // 2026-08-08: レーンラベルヘッダー(LaneHeaderBar)は別領域化に伴い専用のMeasureOverrideを持つため、
        // (ShowLaneNoteCount等のトグルで1行/2行表示が切り替わり高さが変わる場合があるので)
        // VisualだけでなくMeasureも合わせて無効化しておく(再計測コスト自体は軽微)。
        LaneHeaderBar1.InvalidateMeasure();
        LaneHeaderBar1.InvalidateVisual();
        if (!_splitViewEnabled) return;
        SyncCanvas2FromCanvas();
        Canvas2.InvalidateVisual();
        LaneHeaderBar2.InvalidateMeasure();
        LaneHeaderBar2.InvalidateVisual();
    }

    /// <summary>Canvas2(右ペイン)の表示設定・参照をCanvas(左ペイン)から丸ごとコピーする。
    /// ViewportRect(スクロール位置に依存する可視範囲)だけは対象外(分割ビューの目的である
    /// 「独立スクロール」を保つため、Canvas2自身のChartScrollViewer2_ScrollChangedで別途更新する)。
    /// StartNumberEditMode(専用モード)は現状Canvas(左ペイン)のみを対象とする仕様のため、
    /// Canvas2側は常にOFFのままにする(2つのペインで別々の特殊モードが同時に有効になる事故を
    /// 避けるための簡略化)。時間情報レーンの時間範囲選択(2026-07-27)はモードを持たず、
    /// 両ペインとも同じタブのTimeRangeSelectionStartTick/EndTickを共有して素直に動作する。</summary>
    private void SyncCanvas2FromCanvas()
    {
        Canvas2.Document = Canvas.Document;
        Canvas2.Controller = Canvas.Controller;
        Canvas2.CollabSession = Canvas.CollabSession; // 2026-09-20: ノート所有者アイコン用の参照も追従させる
        Canvas2.OverlayPlugins = Canvas.OverlayPlugins;
        Canvas2.Reverse = Canvas.Reverse;
        Canvas2.ShowNoteImages = Canvas.ShowNoteImages;
        Canvas2.ShowHighlightGrid = Canvas.ShowHighlightGrid;
        Canvas2.HighlightLineWidth = Canvas.HighlightLineWidth;
        Canvas2.HighlightLineColor = Canvas.HighlightLineColor;
        Canvas2.ExcludeFreezeEndFromHighlight = Canvas.ExcludeFreezeEndFromHighlight;
        Canvas2.UseNoteColorForHighlight = Canvas.UseNoteColorForHighlight;
        Canvas2.PlaybackStartLineWidth = Canvas.PlaybackStartLineWidth;
        Canvas2.PlaybackStartLineColor = Canvas.PlaybackStartLineColor;
        Canvas2.CursorLineWidth = Canvas.CursorLineWidth;
        Canvas2.CursorLineColor = Canvas.CursorLineColor;
        Canvas2.CursorHighlightWidth = Canvas.CursorHighlightWidth;
        Canvas2.CursorHighlightColor = Canvas.CursorHighlightColor;
        Canvas2.MacroRangeMarkerWidth = Canvas.MacroRangeMarkerWidth;
        Canvas2.MacroRangeHighlightColor = Canvas.MacroRangeHighlightColor;
        Canvas2.LinkedNoteSizeRatio = Canvas.LinkedNoteSizeRatio;
        Canvas2.LinkedNoteColor = Canvas.LinkedNoteColor;
        Canvas2.LinkedHighlightWidthRatio = Canvas.LinkedHighlightWidthRatio;
        Canvas2.LinkedHighlightHeight = Canvas.LinkedHighlightHeight;
        Canvas2.LinkedHighlightColor = Canvas.LinkedHighlightColor;
        Canvas2.MarkerCommentFull = Canvas.MarkerCommentFull;
        Canvas2.MarkerCommentHeadChars = Canvas.MarkerCommentHeadChars;
        Canvas2.TimeInfoFontSize = Canvas.TimeInfoFontSize;
        Canvas2.MarkerFontSize = Canvas.MarkerFontSize;
        Canvas2.ShowLaneNoteCount = Canvas.ShowLaneNoteCount;
        Canvas2.ShowLaneNameLabel = Canvas.ShowLaneNameLabel;
        Canvas2.ShowFrameWithBlankFrame = Canvas.ShowFrameWithBlankFrame;
        Canvas2.ShowWaveform = Canvas.ShowWaveform;
        Canvas2.Waveform = Canvas.Waveform;
        Canvas2.AudioTotalFrames = Canvas.AudioTotalFrames;
        Canvas2.PlaybackTick = Canvas.PlaybackTick;
        Canvas2.KeyboardModeActive = Canvas.KeyboardModeActive;
    }

    /// <summary>Minimap2(右ペイン用ミニマップ、2026-07-26b要望対応「ビュー1つにつき1つのミニマップ」)を
    /// Minimap(左ペイン用)から同期する。TargetScrollViewer/FocusTargetだけは右ペイン自身
    /// (ChartScrollViewer2/Canvas2)を指すようにし、そこだけはコピーしない。</summary>
    private void SyncMinimap2FromDocument()
    {
        if (!_splitViewEnabled) return;
        Minimap2.Document = Minimap.Document;
        Minimap2.AudioTotalFrames = Minimap.AudioTotalFrames;
        Minimap2.TargetScrollViewer = ChartScrollViewer2;
        Minimap2.FocusTarget = Canvas2;
    }

    // =====================================================================
    // ショートカット(仕様書13章): Ctrl+S/E/Z/Y
    // =====================================================================

    /// <summary>統計情報(2026-07-26、Undo/Redo実行回数、両方合計)。実際に履歴を消費した場合
    /// (何も無い状態でCtrl+Z/Yを空押ししただけの場合は増やさない)のみカウントする。</summary>
    private void RecordUndoRedoStatIfChanged(bool didSomething)
    {
        if (!didSomething) return;
        _appSettings.StatUndoRedoCount++;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>単独のAltキー押下/解放を握りつぶし、メニューへのアクセスキーフォーカス移動を防ぐ
    /// (2026-07-26、Alt+ホイールで横ズーム後にAltを離すと「ファイル(F)」メニューへフォーカスが
    /// 飛んでしまう不具合対応)。他のキーと組み合わせている場合(Ctrl+Alt+◯◯等)はここでは何もしない。</summary>
    private static void MainWindow_SuppressLoneAltMenuFocus(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
            e.Handled = true;
    }

    /// <summary>_appSettings.Shortcutsから chord(キー+修飾キー)→ShortcutId の解決テーブルを再構築する
    /// (2026-07-27要望対応)。構築時・環境設定確定時に呼ぶ。</summary>
    private void RebuildShortcutChordMap()
    {
        _shortcutChordMap.Clear();
        foreach (ShortcutId id in Enum.GetValues<ShortcutId>())
        {
            var b = _appSettings.GetShortcut(id);
            if (Enum.TryParse<Key>(b.Key, out var key))
            {
                _shortcutChordMap[(key, b.Ctrl, b.Shift, b.Alt)] = id;
            }
        }
    }

    /// <summary>_appSettings.KeyboardModeShortcutsから Key→KeyboardModeShortcutId の解決テーブルを
    /// 再構築する(2026-07-29要望対応)。構築時・環境設定確定時に呼ぶ。</summary>
    private void RebuildKeyboardShortcutChordMap()
    {
        _keyboardShortcutChordMap.Clear();
        foreach (KeyboardModeShortcutId id in Enum.GetValues<KeyboardModeShortcutId>())
        {
            var keyName = _appSettings.GetKeyboardModeShortcutKey(id);
            if (Enum.TryParse<Key>(keyName, out var key))
            {
                _keyboardShortcutChordMap[key] = id;
            }
        }
    }

    /// <summary>ShortcutIdに対応する処理を実行する(2026-07-27要望対応)。実際に何らかの動作をした場合
    /// (=キーイベントを消費したとみなしてよい場合)はtrueを返す。目視テスト中断系のように、実行時の
    /// モード・状態によっては何もしない(既存の同名ハードコード条件と同じガード)ものはfalseを返す。</summary>
    private bool ExecuteShortcut(ShortcutId id)
    {
        switch (id)
        {
            case ShortcutId.Undo:
                RecordUndoRedoStatIfChanged(_document!.Undo());
                InvalidateChartViews();
                return true;
            case ShortcutId.Redo:
                RecordUndoRedoStatIfChanged(_document!.Redo());
                InvalidateChartViews();
                return true;
            case ShortcutId.SaveProject:
                SaveProject_Click(this, new RoutedEventArgs());
                return true;
            case ShortcutId.ExportDos:
                ExportDos_Click(this, new RoutedEventArgs());
                return true;
            case ShortcutId.ScrollToStart:
                // 2026-07-22: 譜面先頭(tick0)は通常=上端、Reverse時=下端
                ChartScrollViewer.ScrollToVerticalOffset(_appSettings.ChartViewReverse ? ChartScrollViewer.ScrollableHeight : 0);
                return true;
            case ShortcutId.ScrollToEnd:
                ScrollToLastNote(); // 末尾ノートを画面中央へ
                return true;
            // 2026-07-26: 目視テストを「現在位置で終了」するショートカット。マウスモード(Spaceで開始)と
            // キーボードモード(Enterで開始)で既定キーを分けている(Spaceはキーボードモード中カーソル
            // 前進に割り当て済みで紛らわしいため)。
            case ShortcutId.InterruptVisualTestMouseMode:
                if (_keyboardModeActive || !_visualTestActive) return false;
                // 2026-09-07要望対応: キーボードモード側(Ctrl+Enter)と挙動を揃え、その場中断した際は
                // 中断タイミングの最寄りグリッドへ再生開始ラインを設定する(次回の目視テスト・
                // プレイテストが続きから始まるように)。
                SnapPlaybackStartLineToLivePlaybackTick();
                StopVisualTest(returnToStart: false);
                return true;
            case ShortcutId.InterruptVisualTestKeyboardMode:
                if (!_keyboardModeActive || !_visualTestActive) return false;
                // 2026-07-26要望対応: キーボードモード中、その場中断した際は、中断タイミングの
                // 最寄りグリッドへ再生開始ラインを設定する(次回の目視テスト・プレイテストが続きから始まるように)。
                SnapPlaybackStartLineToLivePlaybackTick();
                StopVisualTest(returnToStart: false);
                return true;
            case ShortcutId.StartPlaytest: // 2026-07-17g: プレイテスト開始(仕様書12.2)
                StartPlaytest();
                return true;
            case ShortcutId.ToggleKeyboardMode: // 2026-07-21: SKB操作モード切替
                ToggleKeyboardMode();
                return true;
            // --- 2026-07-20: Cut/Copy/Paste(仕様書13章) ---
            case ShortcutId.CutSelection:
                if (_controller is not null && _controller.CutSelection()) InvalidateChartViews();
                return true;
            case ShortcutId.CopySelection:
                _controller?.CopySelection();
                return true;
            case ShortcutId.PasteSelection:
                ExecutePaste();
                return true;
            // --- 2026-07-21: 全選択・選択解除(仕様書13章TBD) ---
            case ShortcutId.SelectAllTargets:
            {
                var s = _appSettings;
                var options = new SelectAllOptions(
                    s.SelectAllTargetNote, s.SelectAllTargetFreeze, s.SelectAllTargetSpeed,
                    s.SelectAllTargetBoost, s.SelectAllTargetBpm, s.SelectAllTargetTimeSignature, s.SelectAllTargetMarker);
                if (_controller is not null && _controller.SelectAllTargets(options)) InvalidateChartViews();
                return true;
            }
            case ShortcutId.SelectAllNotes:
                if (_controller is not null && _controller.SelectAllNotes()) InvalidateChartViews();
                return true;
            case ShortcutId.DeselectAll: // 2026-07-27要望対応: オブジェクト選択解除(旧Escapeから移設)
                if (_controller is not null && _controller.ClearSelection()) InvalidateChartViews();
                return true;
            case ShortcutId.ClearTimeRangeSelection: // 2026-07-27要望対応: 時間情報レーンの時間範囲選択を解除する
                if (Canvas.ClearTimeRangeSelection()) InvalidateChartViews();
                return true;
            case ShortcutId.ScrollPageUp:
                ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, ChartScrollViewer.VerticalOffset - ChartScrollViewer.ViewportHeight));
                return true;
            case ShortcutId.ScrollPageDown:
                ChartScrollViewer.ScrollToVerticalOffset(ChartScrollViewer.VerticalOffset + ChartScrollViewer.ViewportHeight);
                return true;
            case ShortcutId.DeleteSelection: // 選択中オブジェクトの削除(未解決事項§2-3)
                if (_controller is not null && _controller.DeleteSelection()) InvalidateChartViews();
                return true;
            case ShortcutId.ClearPlaybackStartLine: // 再生開始フレームのリセット
                if (_document!.CurrentTab.PlaybackStartFrame is not null)
                {
                    _document.CurrentTab.PlaybackStartFrame = null;
                    _document.NotifyChanged();
                }
                return true;
            case ShortcutId.ToggleVisualTest: // 目視テスト開始/終了(終了後はテスト開始位置へ復帰)
                ToggleVisualTest();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ctrl+V(貼り付け)の実処理(2026-07-31)。コピー元タブと現在のタブでキー種が異なり、
    /// かつコピー内容にノート・フリーズが含まれる場合はコピーマネージャー(CopyManagerWindow)を
    /// 表示してレーン対応を指定させる(キャンセル可)。それ以外は従来通り即座に貼り付ける。</summary>
    private void ExecutePaste()
    {
        if (_document is null || _controller is null) return;

        if (_controller.ClipboardNeedsLaneMapping())
        {
            CopyManagerWindow win;
            try
            {
                win = new CopyManagerWindow(_document, _controller, _appSettings) { Owner = this };
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"コピーマネージャーを開けませんでした: {ex.Message}", "コピーマネージャー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (win.ShowDialog() == true && win.Pasted) InvalidateChartViews();
            return;
        }

        if (_controller.Paste()) InvalidateChartViews();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_document is null) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        // テキスト入力中はエディタショートカット(Delete/BackSpace/Space等)を奪わない
        // (2026-07-17f、未解決事項§2-3の条件「フォーカスがテキストボックスに無い」)。
        // Ctrl系ショートカットは入力欄フォーカス中でも有効のまま(一般的なエディタの慣習)。
        bool textInputFocused = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

        if (ctrl)
        {
            // --- 2026-07-26要望対応(第三者要望): Ctrl+Shift+1〜9によるキーマクロ実行。
            // スロット番号自体がトリガーキーを兼ねる設計のため、ショートカットカスタマイズの対象外。
            // Ctrl+1〜9(グリッド分解能切替、Shiftなし)と衝突しないよう、Shift併用時のみここで処理し、
            // 下のグリッド分解能切替(Shiftの有無を見ない)より先に判定する。
            if (!textInputFocused && shift && KeyMacroSlot(e.Key) is int macroSlot)
            {
                RunKeyMacro(macroSlot);
                e.Handled = true;
                return;
            }

            // --- 2026-07-21: キーボードモード中のCtrl+←/→(2小節移動)・Shift+Ctrl+←/→(4小節移動)。
            // 同一キーに対しShiftで移動量が変わる特殊挙動のため、ショートカットカスタマイズの対象外
            // (2026-07-27要望対応時の設計方針通り、ハードコードのまま維持)。
            // 2026-07-26: 既定(環境設定「表示」のKeyboardModeLeftRightMode="visual")では譜面ビューReverse時に
            // 「画面上の見た目方向」を維持するため時間方向を反転する(←=常に画面上方向、→=常に画面下方向。
            // 修飾なし←/→やキーボードモードの全移動キーと同一方針。HandleKeyboardModeKeyの解説コメント参照)。
            // 2026-07-26: "time"モードでは常に←=後退・→=前進に固定し、Reverse中は画面上の方向が逆になる。
            if (_keyboardModeActive && _keyboardMode is not null && e.Key == Key.Left)
            {
                int amount = shift ? 4 : 2;
                bool timeMode = _appSettings.KeyboardModeLeftRightMode == "time";
                _keyboardMode.MoveCursorByMeasure(timeMode ? -amount : (_appSettings.ChartViewReverse ? amount : -amount));
                ScrollKeyboardCursorIntoView();
                InvalidateChartViews();
                e.Handled = true;
                return;
            }
            if (_keyboardModeActive && _keyboardMode is not null && e.Key == Key.Right)
            {
                int amount = shift ? 4 : 2;
                bool timeMode = _appSettings.KeyboardModeLeftRightMode == "time";
                _keyboardMode.MoveCursorByMeasure(timeMode ? amount : (_appSettings.ChartViewReverse ? -amount : amount));
                ScrollKeyboardCursorIntoView();
                InvalidateChartViews();
                e.Handled = true;
                return;
            }

            // --- 2026-07-26: Ctrl+1〜9,0,-,^(数字キー列12個)によるグリッド分解能切替。
            // キー割り当ては既存のGridShortcutPreset設定で管理しているため、ショートカットカスタマイズの
            // 対象外(仕様書TBD、ユーザー指定)。マウスモード・キーボードモードどちらでも常時有効
            // (SKB本家に合わせ、モードに依存しない)。
            if (!textInputFocused && GridShortcutKeyIndex(e.Key) is int gridIdx)
            {
                ApplyGridShortcut(gridIdx);
                e.Handled = true;
                return;
            }
        }

        // --- 2026-07-21: SKB操作モード(キーボード操作)が有効な間は、カーソル移動キー(↑/↓/Space/B)・
        // Backspace(カーソル位置削除)・ノート入力キーを、マウスモードのショートカットより先に処理する。
        // 2026-07-29要望対応: マウスモードとキーボードモードで同じ物理キーが重複して割り当てられる
        // ことを許容し、キーボードモードのON/OFFによって挙動を変える(例: BackSpaceはマウスモードでは
        // 「再生開始ラインの指定解除」、キーボードモードでは「カーソル位置のノート/フリーズを削除」)。
        // ※HandleKeyboardModeKeyはCtrl修飾を見ないため、Ctrl押下中はここへ進めない(旧実装通り、
        // Ctrl系キーはショートカット/専用処理のみが処理対象で、キーボードモードのカーソル移動には
        // 一切反応しない)。
        if (!ctrl && _keyboardModeActive && !textInputFocused && HandleKeyboardModeKey(e)) return;

        // 2026-07-27要望対応: 上記の専用処理(キーマクロ/キーボードモード小節移動/グリッド分解能)・
        // キーボードモード専用ショートカット以外の全ショートカットは、環境設定「ショートカットキー」で
        // 管理する割り当てテーブルから解決する(ハードコードのif/switchチェーンを廃し、_shortcutChordMap
        // 経由のデータ駆動ディスパッチへ変更)。
        if (_shortcutChordMap.TryGetValue((e.Key, ctrl, shift, alt), out var shortcutId))
        {
            var meta = ShortcutDefaults.All[shortcutId];
            if (!(textInputFocused && !meta.IgnoresTextFocus) && ExecuteShortcut(shortcutId))
            {
                e.Handled = true;
                return;
            }
        }
    }

    // =====================================================================
    // SKB操作モード(キーボード操作、2026-07-21確定仕様)
    // =====================================================================

    /// <summary>左パネルのトグルボタン(Ctrl+,と同じ動作)</summary>
    private void ColorEditModeToggle_Click(object sender, RoutedEventArgs e) => ToggleColorEditMode();

    /// <summary>色編集モード(ncolor_data、2026-07-23)のON/OFF切替。ON中はマーカーレーン以外の
    /// 新規配置・移動・通常削除を一切受け付けなくなる(誤操作防止、SmartToolController側の制御)。</summary>
    private void ToggleColorEditMode()
    {
        _colorEditModeActive = !_colorEditModeActive;
        ColorEditModeToggle.IsChecked = _colorEditModeActive;
        PushColorEditStateToController();
        if (_colorEditModeActive) ColorEditTabItem.IsActive = true;
        InvalidateChartViews();
        StatusText.Text = _colorEditModeActive
            ? "色編集モード: ON(左クリック=着色/Shift・ホイールクリック=端点+帯同時/右クリック=解除、他の配置・移動は無効)"
            : "色編集モード: OFF";
    }

    // =====================================================================
    // 色編集モード 右パネル(ncolor_data、2026-07-23)
    // =====================================================================

    private void NColorField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        RebuildNColorListPanel();
        UpdateNColorPreview();
        PushPaintColorToController();
    }

    private void NColorAddColorButton_Click(object sender, RoutedEventArgs e)
    {
        _nColorColors.Add("#ffffff");
        _nColorUseOpacity.Add(false);
        _nColorOpacity.Add("");
        RebuildNColorListPanel();
        UpdateNColorPreview();
        PushPaintColorToController();
    }

    private void NColorBulkFillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _controller is null) return;
        var code = ComposeNColorCode();
        if (code is null) return;
        if (_controller.BulkFillSelection(code))
        {
            _document.NotifyChanged();
            InvalidateChartViews();
        }
    }

    private void NColorClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _controller is null) return;
        var result = MessageBox.Show(this, "現在の難易度タブの色指定(ncolor_data)を全て削除します。よろしいですか？",
            "ncolor_dataを全て削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        if (_controller.ClearAllNoteColors())
        {
            _document.NotifyChanged();
            InvalidateChartViews();
        }
    }

    // =====================================================================
    // 右パネル: マクロ(レーン入替マクロ、仕様書11章、2026-07-26)
    // =====================================================================

    /// <summary>マクロ一覧の表示用ラッパー(「キー種 - マクロ名」形式、テンプレート一覧と同じ書式)</summary>
    private sealed record MacroListEntry(LaneSwapMacro Macro)
    {
        public override string ToString() => $"{Macro.TargetKeyTypeId} - {Macro.MacroName}";
    }

    private void SaveMacros() => LaneSwapMacroFile.SaveAll(AppPaths.SettingsDir, _macros);

    /// <summary>右パネルの一覧は「現在開いている難易度タブのキー種に対応するものだけ」表示する
    /// (2026-07-26要望。タブ切替でキー種が変わればここも切り替わる)。ドキュメント未オープン時は
    /// キー種を判定できないため空表示にする。</summary>
    private void RefreshMacroList()
    {
        var selectedId = (MacroListBox.SelectedItem as MacroListEntry)?.Macro.MacroId;
        MacroListBox.Items.Clear();

        string? currentKeyTypeId = _document?.CurrentTab.KeyTypeId;
        if (currentKeyTypeId is not null)
        {
            foreach (var m in _macros
                         .Where(m => string.Equals(m.TargetKeyTypeId, currentKeyTypeId, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.MacroName, StringComparer.OrdinalIgnoreCase))
                MacroListBox.Items.Add(new MacroListEntry(m));
        }

        if (selectedId is not null)
            MacroListBox.SelectedItem = MacroListBox.Items.Cast<MacroListEntry>()
                .FirstOrDefault(e => e.Macro.MacroId == selectedId);
        UpdateMacroButtonStates();
        RefreshTimeRangeStatus();
    }

    /// <summary>時間範囲選択の状態を右パネル(マクロタブ)へ反映する(2026-07-27要望対応、旧
    /// RefreshMacroRangeStatus)。時間情報レーンのドラッグで設定される範囲は、マクロタブに限らず
    /// 常時有効な汎用選択のため、モードのON/OFF切替は無い(状態表示のみ)。</summary>
    private void RefreshTimeRangeStatus()
    {
        var tab = _document?.CurrentTab;
        if (tab is null)
        {
            TimeRangeStatusText.Text = "(未オープン)";
            UpdateMacroButtonStates();
            RefreshLoopPlaybackStatus();
            RefreshPreviewPanel();
            return;
        }

        long? st = tab.TimeRangeSelectionStartTick, et = tab.TimeRangeSelectionEndTick;
        TimeRangeStatusText.Text = st is null && et is null
            ? "未設定(時間情報レーンをドラッグして指定)"
            : $"始点: {(st?.ToString() ?? "未設定")}  終点: {(et?.ToString() ?? "未設定")}";
        UpdateMacroButtonStates();
        RefreshLoopPlaybackStatus();
        RefreshPreviewPanel();
    }

    /// <summary>DAWループ再生(2026-07-29要望対応)の上部パネル表示を、現在タブの時間範囲選択に
    /// 合わせて更新する。範囲未選択の間はループトグルを無効化し、選択が解除されたらループも自動OFFにする。</summary>
    private void RefreshLoopPlaybackStatus()
    {
        var tab = _document?.CurrentTab;
        if (_document is not null && tab?.TimeRangeSelectionStartTick is { } stTick && tab.TimeRangeSelectionEndTick is { } etTick)
        {
            var engine = _document.Project.CreateTimingEngine();
            LoopStartFrameText.Text = $"{ToDisplayFrame(engine.TickToFrame(Math.Min(stTick, etTick))):0.#}F";
            LoopEndFrameText.Text = $"{ToDisplayFrame(engine.TickToFrame(Math.Max(stTick, etTick))):0.#}F";
            LoopPlaybackToggle.IsEnabled = true;
        }
        else
        {
            LoopStartFrameText.Text = "-";
            LoopEndFrameText.Text = "-";
            LoopPlaybackToggle.IsEnabled = false;
            if (LoopPlaybackToggle.IsChecked == true) LoopPlaybackToggle.IsChecked = false; // 選択解除時はループも自動OFF
        }
    }

    /// <summary>「ループ ON/OFF」トグル変更(2026-07-29要望対応)。</summary>
    private void LoopPlaybackToggle_Changed(object sender, RoutedEventArgs e)
    {
        _loopPlaybackEnabled = LoopPlaybackToggle.IsChecked == true;
    }

    /// <summary>プレイ画面プレビュー(2026-07-29要望対応)を現在のドキュメント・難易度タブ・環境設定に
    /// 合わせて再構築する(幾何・speed/boost等の重い再計算を伴う)。タブ切替・ドキュメント開閉・
    /// 環境設定変更時にのみ呼ぶこと。再生開始ラインだけを同期したい場合はSyncPreviewStartFrameを使う。</summary>
    private void RefreshPreviewPanel()
    {
        _previewSurface.Rebuild(_document, _appSettings);
        SyncPreviewStartFrame();
    }

    /// <summary>プレイ画面プレビューの「再生開始ライン」基準を現在のドキュメントに合わせて同期する軽量な
    /// 更新(2026-07-29要望対応: レーンダブルクリック等で再生開始ラインを再設置した際、目視テスト中で
    /// なくても即座にプレビューへ反映するため)。幾何(speed/boost等)の再計算は行わない。</summary>
    private void SyncPreviewStartFrame()
    {
        double startFrame = _document?.CurrentTab.PlaybackStartFrame ?? 0;
        _previewSurface.SetStartFrame(startFrame);
        if (!_visualTestActive) _previewSurface.CurrentFrame = startFrame;
        // 2026-07-29要望対応: ノート配置・色編集等、doc.Changedを伴うあらゆる編集操作の結果を
        // プレビューへ即座に反映する(SetStartFrame/CurrentFrameの代入だけでは値が変化しない限り
        // 再描写されないため、ここで無条件に再描写を予約する)。
        _previewSurface.InvalidateVisual();
    }

    /// <summary>プレイ画面プレビューの「ノートの表示期限」ラジオボタン変更(2026-07-29要望対応)。</summary>
    private void PreviewExpiryMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // XAML初期値設定によるInitializeComponent中の発火を無視(ShowNoteImagesToggle等と同様)
        _appSettings.PreviewNoteExpiryMode = PreviewExpiryOverlapRadio.IsChecked == true ? "overlap" : "passThrough";
        _previewSurface.SetNoteExpiryIncludesEqual(PreviewExpiryOverlapRadio.IsChecked == true);
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>プレイ画面プレビューの「表示サイズ倍率」コンボ変更(2026-07-29要望対応)。
    /// 論理座標(ノート配置等)には影響させず、PlaytestWindowのウィンドウサイズ倍率と同じくLayoutTransform
    /// (ScaleTransform)で表示のみ拡縮する。</summary>
    private void PreviewScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (PreviewScaleCombo.SelectedItem is not double scale) return;
        _appSettings.PreviewDisplayScale = scale;
        ApplyPreviewDisplayScale();
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    private void ApplyPreviewDisplayScale()
    {
        double scale = Math.Clamp(_appSettings.PreviewDisplayScale, 0.25, 2.0);
        _previewSurface.LayoutTransform = Math.Abs(scale - 1.0) > 0.001 ? new ScaleTransform(scale, scale) : Transform.Identity;
    }

    private void UpdateMacroButtonStates()
    {
        bool hasSelection = MacroListBox.SelectedItem is MacroListEntry;
        MacroEditButton.IsEnabled = hasSelection;
        MacroDeleteButton.IsEnabled = hasSelection;

        MacroRunButton.IsEnabled = hasSelection && _document is not null
            && MacroListBox.SelectedItem is MacroListEntry sel
            && string.Equals(sel.Macro.TargetKeyTypeId, _document.CurrentTab.KeyTypeId, StringComparison.OrdinalIgnoreCase)
            && sel.Macro.LaneMapping.Count == _document.CurrentTab.Lanes.Count;
    }

    private void MacroListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMacroButtonStates();

    private void MacroListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => MacroEditButton_Click(sender, e);

    private void MacroAddButton_Click(object sender, RoutedEventArgs e)
    {
        var existingNames = _macros.Select(m => m.MacroName).ToList();
        // 2026-07-26要望対応: 新規作成時はカレント難易度タブのキー種を初期選択しておく
        var win = new MacroEditorWindow(_templates, null, existingNames, _document?.CurrentTab.KeyTypeId) { Owner = this };
        if (win.ShowDialog() != true || win.SavedMacro is null) return;
        _macros.Add(win.SavedMacro);
        SaveMacros();
        RefreshMacroList();
    }

    private void MacroEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (MacroListBox.SelectedItem is not MacroListEntry entry) return;
        var existingNames = _macros
            .Where(m => m.MacroId != entry.Macro.MacroId)
            .Select(m => m.MacroName).ToList();
        var win = new MacroEditorWindow(_templates, entry.Macro, existingNames) { Owner = this };
        if (win.ShowDialog() != true || win.SavedMacro is null) return;
        int idx = _macros.FindIndex(m => m.MacroId == entry.Macro.MacroId);
        if (idx >= 0) _macros[idx] = win.SavedMacro;
        SaveMacros();
        RefreshMacroList();
    }

    private void MacroDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (MacroListBox.SelectedItem is not MacroListEntry entry) return;
        var confirm = MessageBox.Show(this, $"マクロ '{entry.Macro.MacroName}' を削除します。よろしいですか？",
            "マクロ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        _macros.RemoveAll(m => m.MacroId == entry.Macro.MacroId);
        SaveMacros();
        RefreshMacroList();
    }

    /// <summary>マクロ実行(仕様書11.1)。現在の難易度タブへ順列を適用する。1操作としてUndo履歴に積む
    /// (ユーザー確定仕様、2026-07-26)。時間情報レーンで時間範囲選択が設定されていれば、その範囲内の
    /// 要素だけを対象にする(2026-07-27要望対応: 専用モードのトグルは廃止し、範囲が設定されている
    /// 状態そのものが「範囲内のみ適用」の条件になる。未設定ならタブ全体に適用する)。
    /// 範囲の境界をまたぐフリーズがある場合は適用前に警告し、「適用(フリーズ込み)」
    /// 「適用(フリーズ抜き)」「再設定」の3択から選ばせる(ユーザー確定仕様)。</summary>
    private void MacroRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || MacroListBox.SelectedItem is not MacroListEntry entry) return;

        if (_document.CurrentTab.TimeRangeSelectionStartTick is { } rangeStart
            && _document.CurrentTab.TimeRangeSelectionEndTick is { } rangeEnd)
        {
            bool includeStraddling = false;
            if (LaneSwapMacroRangeHelper.HasStraddlingFreezes(_document.CurrentTab, rangeStart, rangeEnd))
            {
                var result = MessageBox.Show(this,
                    "選択範囲の境界をまたぐフリーズアローがあります。どのように適用しますか？\n" +
                    "「はい」= フリーズ込みで適用(またぐフリーズも範囲内として移動)\n" +
                    "「いいえ」= フリーズ抜きで適用(またぐフリーズは範囲外として据え置き)\n" +
                    "「キャンセル」= 適用せず範囲を再設定する",
                    "境界をまたぐフリーズがあります", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return;
                includeStraddling = result == MessageBoxResult.Yes;
            }

            _document.Execute(new ApplyLaneSwapMacroRangeAction(
                entry.Macro.LaneMapping, entry.Macro.MacroName, rangeStart, rangeEnd, includeStraddling));
            StatusText.Text = $"マクロ実行(範囲選択): {entry.Macro.MacroName}";
        }
        else
        {
            _document.Execute(new ApplyLaneSwapMacroAction(entry.Macro.LaneMapping, entry.Macro.MacroName));
            StatusText.Text = $"マクロ実行: {entry.Macro.MacroName}";
        }

        _appSettings.StatMacroRunCount++; // 2026-07-26: 統計情報
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    // =====================================================================
    // 右パネル: リンク(同キー種タブ同士のリンク、2026-07-26要望対応)
    // アクティブタブ(カレントタブ)と非アクティブタブ(リンク相手)の関係を結び、
    // アクティブタブの背景に非アクティブタブのノートを薄く表示する(描画自体はChartCanvas側)。
    // =====================================================================

    /// <summary>リンク候補一覧の表示用ラッパー(タブ一覧と同じDisplayLabel書式)</summary>
    private sealed record LinkCandidateEntry(DifficultyTab Tab)
    {
        public override string ToString() => Tab.DisplayLabel;
    }

    /// <summary>右パネル「リンク」タブの表示を現在タブの状態に合わせて更新する。
    /// リンク中は相手タブ名のみ表示(候補一覧は隠す)、未リンクなら同キー種かつ未リンクの
    /// タブを候補一覧に出す(既に他タブとリンク中の候補は、二重リンクを防ぐため除外する)。</summary>
    private void RefreshLinkPanel()
    {
        var tab = _document?.CurrentTab;
        if (tab is null)
        {
            LinkStatusText.Text = "(未オープン)";
            LinkUnlinkButton.Visibility = Visibility.Collapsed;
            LinkCandidatePanel.Visibility = Visibility.Collapsed;
            LinkCandidateListBox.Items.Clear();
            return;
        }

        if (tab.LinkedTabId is { } linkedId)
        {
            var partner = _document!.Project.Tabs.FirstOrDefault(t => t.TabId == linkedId);
            LinkStatusText.Text = partner is not null
                ? $"リンク中: {partner.DisplayLabel}"
                : "リンク中(相手タブが見つかりませんでした。リンク解除をお試しください)";
            LinkUnlinkButton.Visibility = Visibility.Visible;
            LinkCandidatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        LinkStatusText.Text = "未リンク";
        LinkUnlinkButton.Visibility = Visibility.Collapsed;
        LinkCandidatePanel.Visibility = Visibility.Visible;

        LinkCandidateListBox.Items.Clear();
        foreach (var candidate in _document!.Project.Tabs
                     .Where(t => t != tab && t.KeyTypeId == tab.KeyTypeId && t.LinkedTabId is null)
                     .OrderBy(t => t.DifficultyName, StringComparer.OrdinalIgnoreCase))
            LinkCandidateListBox.Items.Add(new LinkCandidateEntry(candidate));
        LinkButton.IsEnabled = false;
    }

    private void LinkCandidateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LinkButton.IsEnabled = LinkCandidateListBox.SelectedItem is LinkCandidateEntry;

    /// <summary>リンク開始(2026-07-26要望対応)。相互参照(双方が互いのTabIdを持つ)で結ぶ。
    /// Undo対象外(タブの並び替え等と同じ、編集用の補助情報のため)。</summary>
    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || LinkCandidateListBox.SelectedItem is not LinkCandidateEntry entry) return;
        var tab = _document.CurrentTab;
        tab.LinkedTabId = entry.Tab.TabId;
        entry.Tab.LinkedTabId = tab.TabId;
        _document.NotifyChanged();
        RefreshLinkPanel();
        InvalidateChartViews();
    }

    /// <summary>リンク解除(2026-07-26要望対応)。相手タブ側の参照も一緒に解除する。</summary>
    private void LinkUnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var tab = _document.CurrentTab;
        if (tab.LinkedTabId is { } linkedId)
        {
            var partner = _document.Project.Tabs.FirstOrDefault(t => t.TabId == linkedId);
            if (partner is not null) partner.LinkedTabId = null;
        }
        tab.LinkedTabId = null;
        _document.NotifyChanged();
        RefreshLinkPanel();
        InvalidateChartViews();
    }

    // =====================================================================
    // 右パネル: 分析(ITTNアナライザー/おにスター、2026-07-26、隠し機能、
    // docs/progress_and_tbd_2026-07-25.md §2-1/§2-2対応)
    // 2026-07-26要望対応: 実績進捗・解禁条件は右パネルに一切表示しない(統計情報ウィンドウ側で
    // 閲覧する)。右パネルは「未解禁の間はタブごと非表示、解禁したら普通に使えるだけ」のシンプルな
    // 二値表示にする(譜面編集に必要なものだけを表示する方針)。
    // =====================================================================

    private const int AnalyzerUnlockThreshold = 10000; // 配置オブジェクト累計数
    private const int OniStarUnlockThreshold = 10;      // 算出・再算出ボタン累計押下回数

    /// <summary>EditorDocument.StatRecordedの購読先(OpenDocument参照)。統計情報(AppSettings.Stat*)へ
    /// 加算・保存する。分析タブの解禁状態(配置数)が変わるタイミングだけ再描画する
    /// (頻繁な配置操作のたびに毎回フルリフレッシュすると重いため)。</summary>
    private void OnStatRecorded(EditorStatKind kind, int count)
    {
        bool refreshAnalysisTab = false;
        switch (kind)
        {
            case EditorStatKind.ObjectsPlaced:
                bool wasUnlocked = _appSettings.StatObjectsPlaced >= AnalyzerUnlockThreshold;
                _appSettings.StatObjectsPlaced += count;
                refreshAnalysisTab = (_appSettings.StatObjectsPlaced >= AnalyzerUnlockThreshold) != wasUnlocked;
                break;
            case EditorStatKind.ObjectsDeleted:
                _appSettings.StatObjectsDeleted += count;
                break;
            case EditorStatKind.Copy:
                _appSettings.StatObjectsCopied += count;
                break;
            case EditorStatKind.Cut:
                _appSettings.StatObjectsCut += count;
                break;
            case EditorStatKind.Paste:
                _appSettings.StatObjectsPasted += count;
                break;
        }
        _appSettings.Save(AppPaths.SettingsFilePath);
        if (refreshAnalysisTab) RefreshAnalysisPanel();
    }

    /// <summary>分析タブの表示状態を、AppSettingsの解禁カウンタに応じて更新する。アナライザーが
    /// 未解禁の間はタブ自体を非表示にする(進捗・解禁条件は右パネルに表示しない方針、2026-07-26)。
    /// タブ切替・ドキュメント読込のたびに呼ばれるため、算出結果自体は都度クリアする
    /// (タブが変われば対象の譜面が変わり、前回の結果は無意味になるため)。</summary>
    private void RefreshAnalysisPanel()
    {
        bool analyzerUnlocked = _appSettings.StatObjectsPlaced >= AnalyzerUnlockThreshold;
        if (analyzerUnlocked) AnalysisTabItem.Show(); else AnalysisTabItem.Hide();
        AnalyzerResultText.Text = "";
        OniStarResultText.Text = "？？？";
    }

    /// <summary>「分析を実行」ボタン(2026-07-26)。現在の難易度タブをIttnAnalyzer(analyze.js忠実移植、
    /// docs/progress_and_tbd_2026-07-25.md §1-2/1-3)で解析し、レーダー6軸・JACK/ALT/MOV・
    /// baseRating/totalRating/toolScaleRatingをそのまま表示する。</summary>
    private void AnalyzerRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var result = IttnAnalyzer.Analyze(_document.Project, _document.CurrentTab, _document.CurrentTemplate);
        if (result is null)
        {
            AnalyzerResultText.Text = "解析できませんでした(オブジェクトが無い等)。";
            return;
        }
        AnalyzerResultText.Text =
            $"STREAM  : {result.Stream:F2}\n" +
            $"VOLTAGE : {result.Voltage:F2}\n" +
            $"CHORD   : {result.Chord:F2}\n" +
            $"FREEZE  : {result.Freeze:F2}\n" +
            $"SOF-LAN : {result.Soflan:F2}\n" +
            $"ONIGIRI : {result.Onigiri:F2}\n" +
            $"JACK    : {result.Jack:F2}\n" +
            $"ALT     : {result.Alt:F2}\n" +
            $"MOV     : {result.Mov:F2}\n" +
            $"\n" +
            $"baseRating      : {result.BaseRating:F2}\n" +
            $"totalRating     : {result.TotalRating:F2}\n" +
            $"toolScaleRating : {result.ToolScaleRating:F2}";
    }

    /// <summary>「算出・再算出」ボタン(2026-07-26)。押すたびにAppSettings.StatOniStarRecalcPressesを
    /// 加算・保存し、解禁閾値(10回)に達していればIttnAnalyzer→OniStarEstimatorで統一スケールの
    /// 推定値(60%信頼区間つき)を表示する。未解禁の間は押しても結果は表示しない
    /// (進捗も表示しない、隠し機能、docs/progress_and_tbd_2026-07-25.md §2-2)。
    /// 2026-07-26要望対応: ☆/★表記への変換は行わない(統一スケールの数値をそのまま表示、
    /// 最終的な表記は今後の「おにスター」表記側で行う想定)。</summary>
    private void OniStarRecalcButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        _appSettings.StatOniStarRecalcPresses++;
        _appSettings.Save(AppPaths.SettingsFilePath);

        if (_appSettings.StatOniStarRecalcPresses < OniStarUnlockThreshold) return;

        var result = IttnAnalyzer.Analyze(_document.Project, _document.CurrentTab, _document.CurrentTemplate);
        if (result is null)
        {
            OniStarResultText.Text = "算出できませんでした(オブジェクトが無い等)。";
            return;
        }
        var est = OniStarEstimator.Estimate(result.TotalRating);
        OniStarResultText.Text = $"推定値: {est.Score:F1} (幅: {est.ScoreLow:F1} 〜 {est.ScoreHigh:F1}、60%目安)";
    }

    /// <summary>種類(単色/linear/radial/conic)・方向・色リストから、右パネルの色編集タブの
    /// リスト・表示を再構築する(2026-07-23。2026-07-24: 色名+透明度指定モードに対応)。
    /// 単色選択時は色を1件に固定し、追加/方向入力を隠す。</summary>
    private void RebuildNColorListPanel()
    {
        bool solid = NColorGradientTypeCombo.SelectedIndex <= 0;
        bool linear = NColorGradientTypeCombo.SelectedIndex == 1;
        if (solid && _nColorColors.Count > 1)
        {
            _nColorColors.RemoveRange(1, _nColorColors.Count - 1);
            _nColorUseOpacity.RemoveRange(1, _nColorUseOpacity.Count - 1);
            _nColorOpacity.RemoveRange(1, _nColorOpacity.Count - 1);
        }

        NColorListPanel.Children.Clear();
        for (int i = 0; i < _nColorColors.Count; i++)
        {
            int idx = i;
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var opacityCheck = new CheckBox { Content = "透明度を使用する(色名指定)", IsChecked = _nColorUseOpacity[idx], Margin = new Thickness(0, 0, 0, 2) };
            opacityCheck.Checked += (_, _) => ToggleNColorOpacityMode(idx, true);
            opacityCheck.Unchecked += (_, _) => ToggleNColorOpacityMode(idx, false);
            container.Children.Add(opacityCheck);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var swatch = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0),
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Background = NColorSwatchBrush(_nColorColors[idx]),
            };
            row.Children.Add(swatch);

            if (_nColorUseOpacity[idx])
            {
                var combo = new ComboBox
                {
                    Width = 110, IsEditable = true,
                    ItemsSource = CssColorNames.All.Select(c => c.Name).ToList(),
                    Text = _nColorColors[idx].Split(';')[0],
                };
                void CommitName()
                {
                    _nColorColors[idx] = ComposeNameOpacityToken(combo.Text, _nColorOpacity[idx]);
                    swatch.Background = NColorSwatchBrush(_nColorColors[idx]);
                    UpdateNColorPreview();
                    PushPaintColorToController();
                }
                combo.PreviewKeyDown += CommitOnEnter_PreviewKeyDown;
                combo.LostFocus += (_, _) => CommitName();
                combo.SelectionChanged += (_, _) => CommitName();

                var pickBtn = new Button { Content = "一覧", Width = 32, Margin = new Thickness(4, 0, 0, 0) };
                pickBtn.Click += (_, _) => CssColorPicker.Show(pickBtn, name => { combo.Text = name; CommitName(); });

                var opacityLabel = new TextBlock { Text = "不透明度(0-255):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), FontSize = 10 };
                var opacityBox = new TextBox { Width = 40, Text = _nColorOpacity[idx] };
                opacityBox.TextChanged += (_, _) =>
                {
                    _nColorOpacity[idx] = opacityBox.Text.Trim();
                    _nColorColors[idx] = ComposeNameOpacityToken(combo.Text, _nColorOpacity[idx]);
                    swatch.Background = NColorSwatchBrush(_nColorColors[idx]);
                    UpdateNColorPreview();
                    PushPaintColorToController();
                };

                row.Children.Add(combo);
                row.Children.Add(pickBtn);
                row.Children.Add(opacityLabel);
                row.Children.Add(opacityBox);
            }
            else
            {
                var box = new TextBox { Width = 96, Text = _nColorColors[idx] };
                box.TextChanged += (_, _) =>
                {
                    _nColorColors[idx] = box.Text;
                    swatch.Background = NColorSwatchBrush(box.Text);
                    UpdateNColorPreview();
                    PushPaintColorToController();
                };
                box.PreviewKeyDown += CommitOnEnter_PreviewKeyDown;
                box.LostFocus += (_, _) =>
                {
                    ColorHistoryPicker.Record(_appSettings, box.Text);
                    _appSettings.Save(AppPaths.SettingsFilePath);
                };
                // 2026-08-08: 「履歴」ボタンを統合カラーピッカー(ColorPickerPopup)呼び出しへ置き換え、
                // 隣に「☆登録」ボタンを新設(理由・OKフィードバックの経緯はAddColorFieldのコメント参照)。
                var pickerBtn = new Button { Content = "色", Width = 32, Margin = new Thickness(4, 0, 0, 0) };
                pickerBtn.Click += (_, _) => ColorPickerPopup.Show(_appSettings, pickerBtn, box.Text, hex => box.Text = hex);
                var favBtn = new Button { Content = "☆登録", Width = 44, Margin = new Thickness(4, 0, 0, 0) };
                var favOkText = new TextBlock
                {
                    Text = "OK", Foreground = Brushes.Green, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
                    Visibility = Visibility.Collapsed,
                };
                favBtn.Click += (_, _) =>
                {
                    if (FavoriteColorPicker.Register(_appSettings, box.Text))
                    {
                        _appSettings.Save(AppPaths.SettingsFilePath);
                        TransientOkFeedback.Show(favOkText);
                    }
                };

                row.Children.Add(box);
                row.Children.Add(pickerBtn);
                row.Children.Add(favBtn);
                row.Children.Add(favOkText);
            }

            if (!solid && _nColorColors.Count > 1)
            {
                var delBtn = new Button { Content = "×", Width = 24, Margin = new Thickness(4, 0, 0, 0) };
                delBtn.Click += (_, _) =>
                {
                    _nColorColors.RemoveAt(idx);
                    _nColorUseOpacity.RemoveAt(idx);
                    _nColorOpacity.RemoveAt(idx);
                    RebuildNColorListPanel();
                    UpdateNColorPreview();
                    PushPaintColorToController();
                };
                row.Children.Add(delBtn);
            }
            container.Children.Add(row);
            NColorListPanel.Children.Add(container);
        }

        NColorAddColorButton.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
        NColorDirectionLabel.Visibility = linear ? Visibility.Visible : Visibility.Collapsed;
        NColorDirectionBox.Visibility = linear ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>色名+透明度モードのON/OFF切替(2026-07-24)。切替時に色欄の値を妥当な既定値へ
    /// 詰め替える(hex⇔色名は自動変換できないため)。</summary>
    private void ToggleNColorOpacityMode(int idx, bool on)
    {
        if (idx >= _nColorUseOpacity.Count) return;
        _nColorUseOpacity[idx] = on;
        if (on)
        {
            var current = _nColorColors[idx];
            if (current.StartsWith('#') || current.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                _nColorColors[idx] = CssColorNames.All[0].Name; // 既定色名(aliceblue)
        }
        else
        {
            _nColorOpacity[idx] = "";
            if (!_nColorColors[idx].StartsWith('#'))
                _nColorColors[idx] = "#ffffff";
        }
        RebuildNColorListPanel();
        UpdateNColorPreview();
        PushPaintColorToController();
    }

    /// <summary>色名+透明度(0-255)をncolor_data ColorCode欄の書式(色名;透明度)へ組み立てる
    /// (2026-07-24、透明度未指定なら色名のみ)。</summary>
    private static string ComposeNameOpacityToken(string name, string opacity)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0) trimmedName = CssColorNames.All[0].Name;
        if (opacity.Length == 0) return trimmedName;
        return int.TryParse(opacity, out var v)
            ? $"{trimmedName};{Math.Clamp(v, 0, 255)}"
            : trimmedName;
    }

    /// <summary>色編集タブのスウォッチ表示用ブラシ解決(2026-07-24)。色名;透明度形式にも対応し、
    /// 透明度値をアルファ値として反映する。</summary>
    private static Brush NColorSwatchBrush(string token)
    {
        var namePart = token.Split(';')[0];
        var opacityPart = token.Contains(';') ? token.Split(';', 2)[1] : "";
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(namePart)!;
            if (int.TryParse(opacityPart, out var v))
                color.A = (byte)Math.Clamp(v, 0, 255);
            return new SolidColorBrush(color);
        }
        catch { return Brushes.LightGray; }
    }

    private void UpdateNColorPreview()
    {
        var code = ComposeNColorCode();
        NColorPreviewSwatch.Background = code is null
            ? Brushes.Transparent
            : new SolidColorBrush(ChartCanvas.ParseDisplayColor(code, Colors.Magenta));
    }

    private void PushPaintColorToController()
    {
        if (_controller is not null) _controller.PaintColorCode = ComposeNColorCode();
    }

    /// <summary>「即時適用(全体色変化)にする」チェックボックス(2026-07-24)。</summary>
    private void NColorAllFlagCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _paintAllFlag = NColorAllFlagCheck.IsChecked == true;
        if (_controller is not null) _controller.PaintAllFlag = _paintAllFlag;
    }

    // =====================================================================
    // 色編集モード サブモード(Normal/FrzHit/Shadow、2026-07-24)
    // =====================================================================

    /// <summary>右パネルの色編集タブに現在表示されている入力値から、SmartToolControllerが
    /// 実際に塗りに使う状態を一括で反映する。ColorEditModeEnabled/PaintColorCode/PaintAllFlagに加え、
    /// SubModeとFrzHit/Shadow系の入力値もまとめて反映する(以前は各フィールドを個別に反映していたが、
    /// サブモード追加に伴い呼び出し箇所が増えたため1箇所へ集約した)。</summary>
    private void PushColorEditStateToController()
    {
        if (_controller is null) return;
        _controller.ColorEditModeEnabled = _colorEditModeActive;
        _controller.PaintColorCode = ComposeNColorCode();
        _controller.PaintAllFlag = _paintAllFlag;
        _controller.SubMode = CurrentNColorSubMode();
        _controller.PaintArrowShadowColor = NColorArrowShadowColorBox.Text;
        _controller.PaintNormalShadowColor = NColorNormalShadowColorBox.Text;
        _controller.HitEnabled = NColorHitEnabledCheck.IsChecked == true;
        _controller.HitBarEnabled = NColorHitBarEnabledCheck.IsChecked == true;
        _controller.HitShadowEnabled = NColorHitShadowEnabledCheck.IsChecked == true;
        _controller.PaintHitColor = NColorHitColorBox.Text;
        _controller.PaintHitBarColor = NColorHitBarColorBox.Text;
        _controller.PaintHitShadowColor = NColorHitShadowColorBox.Text;
    }

    private ColorEditSubMode CurrentNColorSubMode() =>
        NColorSubModeFrzHitRadio.IsChecked == true ? ColorEditSubMode.FrzHit
        : NColorSubModeShadowRadio.IsChecked == true ? ColorEditSubMode.Shadow
        : ColorEditSubMode.Normal;

    /// <summary>編集対象(通常色/ヒット時色/塗りつぶし色)のラジオボタン切替。対応するパネルの
    /// 表示切替とコントローラへの反映に加え、FrzHitサブモード中は譜面ビューの表示色がヒット時色の
    /// プレビューに変わる(ChartCanvas.DrawNotesAndFreezes参照)ため再描画する。</summary>
    private void NColorSubMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        NColorNormalPanel.Visibility = NColorSubModeNormalRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NColorFrzHitPanel.Visibility = NColorSubModeFrzHitRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NColorShadowPanel.Visibility = NColorSubModeShadowRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PushColorEditStateToController();
        InvalidateChartViews();
    }

    /// <summary>Hit/HitBar/HitShadowの対象チェックボックス変更。3つとも未チェックになった場合、
    /// クリック/一括塗りつぶしをしても何も適用されない(SmartToolController側で無視される)ため、
    /// ユーザー確定仕様通り警告ダイアログを出す。</summary>
    private void NColorHitEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (NColorHitEnabledCheck.IsChecked != true && NColorHitBarEnabledCheck.IsChecked != true &&
            NColorHitShadowEnabledCheck.IsChecked != true)
        {
            MessageBox.Show(this,
                "ヒット時色(端点/帯/塗りつぶし)の対象が1つも選択されていません。このままではクリックや一括塗りつぶしをしても何も適用されません。",
                "ヒット時色編集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        PushColorEditStateToController();
    }

    private void NColorFrzHitField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        PushColorEditStateToController();
    }

    private void NColorShadowField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        PushColorEditStateToController();
    }

    private void NColorFrzHitBulkFillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _controller is null) return;
        if (_controller.BulkFillFrzHitSelection())
        {
            _document.NotifyChanged();
            InvalidateChartViews();
        }
    }

    private void NColorShadowBulkFillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _controller is null) return;
        if (_controller.BulkFillShadowSelection())
        {
            _document.NotifyChanged();
            InvalidateChartViews();
        }
    }

    // 2026-08-08: 「履歴」ボタンを統合カラーピッカー(ColorPickerPopup)呼び出しへ置き換え、
    // 各色欄に「☆登録」ボタン(現在値をお気に入りへ追加)を新設した(進捗まとめ5-2、お気に入りの色機能)。
    // 押しても見た目の変化が分かりづらいとの指摘を受け、登録成功時に「OK」を2秒間表示する
    // (TransientOkFeedback)。
    private void NColorHitPickerButton_Click(object sender, RoutedEventArgs e) =>
        ColorPickerPopup.Show(_appSettings, NColorHitPickerButton, NColorHitColorBox.Text, hex => { NColorHitColorBox.Text = hex; });

    private void NColorHitFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteColorPicker.Register(_appSettings, NColorHitColorBox.Text))
        {
            _appSettings.Save(AppPaths.SettingsFilePath);
            TransientOkFeedback.Show(NColorHitFavoriteOkText);
        }
    }

    private void NColorHitBarPickerButton_Click(object sender, RoutedEventArgs e) =>
        ColorPickerPopup.Show(_appSettings, NColorHitBarPickerButton, NColorHitBarColorBox.Text, hex => { NColorHitBarColorBox.Text = hex; });

    private void NColorHitBarFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteColorPicker.Register(_appSettings, NColorHitBarColorBox.Text))
        {
            _appSettings.Save(AppPaths.SettingsFilePath);
            TransientOkFeedback.Show(NColorHitBarFavoriteOkText);
        }
    }

    private void NColorHitShadowPickerButton_Click(object sender, RoutedEventArgs e) =>
        ColorPickerPopup.Show(_appSettings, NColorHitShadowPickerButton, NColorHitShadowColorBox.Text, hex => { NColorHitShadowColorBox.Text = hex; });

    private void NColorHitShadowFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteColorPicker.Register(_appSettings, NColorHitShadowColorBox.Text))
        {
            _appSettings.Save(AppPaths.SettingsFilePath);
            TransientOkFeedback.Show(NColorHitShadowFavoriteOkText);
        }
    }

    private void NColorArrowShadowPickerButton_Click(object sender, RoutedEventArgs e) =>
        ColorPickerPopup.Show(_appSettings, NColorArrowShadowPickerButton, NColorArrowShadowColorBox.Text, hex => { NColorArrowShadowColorBox.Text = hex; });

    private void NColorArrowShadowFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteColorPicker.Register(_appSettings, NColorArrowShadowColorBox.Text))
        {
            _appSettings.Save(AppPaths.SettingsFilePath);
            TransientOkFeedback.Show(NColorArrowShadowFavoriteOkText);
        }
    }

    private void NColorNormalShadowPickerButton_Click(object sender, RoutedEventArgs e) =>
        ColorPickerPopup.Show(_appSettings, NColorNormalShadowPickerButton, NColorNormalShadowColorBox.Text, hex => { NColorNormalShadowColorBox.Text = hex; });

    private void NColorNormalShadowFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteColorPicker.Register(_appSettings, NColorNormalShadowColorBox.Text))
        {
            _appSettings.Save(AppPaths.SettingsFilePath);
            TransientOkFeedback.Show(NColorNormalShadowFavoriteOkText);
        }
    }

    private void NColorHitColorBox_LostFocus(object sender, RoutedEventArgs e) => RecordNColorHistory(NColorHitColorBox.Text);
    private void NColorHitBarColorBox_LostFocus(object sender, RoutedEventArgs e) => RecordNColorHistory(NColorHitBarColorBox.Text);
    private void NColorHitShadowColorBox_LostFocus(object sender, RoutedEventArgs e) => RecordNColorHistory(NColorHitShadowColorBox.Text);
    private void NColorArrowShadowColorBox_LostFocus(object sender, RoutedEventArgs e) => RecordNColorHistory(NColorArrowShadowColorBox.Text);
    private void NColorNormalShadowColorBox_LostFocus(object sender, RoutedEventArgs e) => RecordNColorHistory(NColorNormalShadowColorBox.Text);

    private void RecordNColorHistory(string hex)
    {
        ColorHistoryPicker.Record(_appSettings, hex);
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>右パネルの種類・方向・色リストから、ncolor_data ColorCode欄の文字列を組み立てる
    /// (2026-07-23、仕様書dos-c0001-gradation)。有効な色が1つも無ければnull。
    /// 色が1つしか無い場合はグラデーション種類に関わらず単色として扱う(仕様通り)。</summary>
    private string? ComposeNColorCode()
    {
        var colors = _nColorColors.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (colors.Count == 0) return null;
        if (colors.Count == 1 || NColorGradientTypeCombo.SelectedIndex <= 0) return colors[0];

        string body = string.Join(":", colors);
        string suffix = NColorGradientTypeCombo.SelectedIndex switch
        {
            1 => "linear-gradient",
            2 => "radial-gradient",
            3 => "conic-gradient",
            _ => "linear-gradient",
        };
        if (NColorGradientTypeCombo.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(NColorDirectionBox.Text))
            body = $"{NColorDirectionBox.Text.Trim()}:{body}";
        return $"{body}@{suffix}";
    }

    private void KeyboardModeToggle_Click(object sender, RoutedEventArgs e) => ToggleKeyboardMode();

    private void ToggleKeyboardMode()
    {
        _keyboardModeActive = !_keyboardModeActive;
        KeyboardModeToggle.IsChecked = _keyboardModeActive;
        _keyboardSelectionAnchorTick = null; // 2026-07-26: モード切替時は範囲選択の状態を持ち越さない
        if (_keyboardModeActive) _keyboardMode?.EnterMode();
        Canvas.KeyboardModeActive = _keyboardModeActive; // 2026-07-22: レーンラベル2行目表示の切替
        _activePaneIsSecondary = false; // 2026-07-26: モード切替のたびアクティブペインは左にリセット
        UpdateActivePaneIndicator();
        InvalidateChartViews();
        StatusText.Text = _keyboardModeActive
            ? "キーボードモード: ON(↑=後退/↓=前進、Space=前進/B=後退、←=1小節戻る(小節頭なら1つ前へ)、→=1小節先へ、Ctrl+←/→=2小節、Shift+Ctrl+←/→=4小節、Shift+移動で範囲内の全レーンを選択、Enterで目視テスト、Backspaceでカーソル位置削除。Ctrl+,で解除)"
            : "キーボードモード: OFF";
    }

    /// <summary>キーボードモード中のキー入力処理。処理した(=既存のマウスモード単独キーハンドラへ
    /// 渡してはいけない)場合はtrueを返す。</summary>
    /// <summary>キーボードモード中、現在位置ライン(PlaybackStartFrame)が画面外に出た場合、
    /// tick0側(通常表示=画面上部、Reverse表示=画面下部)から1小節分進んだ位置へラインが来るよう
    /// スクロールする(2026-07-26確定仕様)。既に画面内に収まっている間は何もしない。
    /// 1小節分の高さはカーソル位置が属する小節の拍子(SignatureAt)を基準に算出する。</summary>
    private void ScrollKeyboardCursorIntoView()
    {
        if (_document is null || _keyboardMode is null) return;
        // 2026-07-26要望対応: 分割ビュー中はアクティブペインのScrollViewerだけを追従させる
        // (非アクティブ側は1アクションで一緒に動いてしまわないよう、常に現状維持のままにする)。
        var sv = ActiveChartScrollViewer;
        double vh = sv.ViewportHeight;
        if (vh <= 0) return; // 未レイアウト(初期化直後等)

        var engine = _document.Project.CreateTimingEngine();
        long tick = _keyboardMode.CursorTick;
        var layout = _document.CurrentLayout;
        double lineY = layout.TickToY(tick);

        double off = sv.VerticalOffset;
        if (lineY >= off && lineY <= off + vh) return; // 画面内なら何もしない

        double measurePx = engine.SignatureAt(tick).TicksPerMeasure * layout.PxPerTick;
        bool reverse = _appSettings.ChartViewReverse;
        double target = reverse ? lineY - vh + measurePx : lineY - measurePx;
        double max = Math.Max(0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
    }

    private bool HandleKeyboardModeKey(KeyEventArgs e)
    {
        if (_document is null || _keyboardMode is null) return false;

        // 2026-07-26: 進む・戻る系キーはすべて「画面上の見た目方向」基準に統一する(ユーザー確定仕様)。
        // 通常表示(tick0が上・末尾が下)では従来通り、Reverse表示中は時間方向を全キー反転して
        // 見た目方向を維持する。対象は↑/↓/Space/B(グリッド移動)、←/→(1小節移動)、
        // Ctrl+←/→系(2/4小節移動、MainWindow_PreviewKeyDown側)の全部。
        // - ↑=常に画面上へ、↓=常に画面下へ。
        // - Space/Bも見た目方向固定(Space=常に画面下へ、B=常に画面上へ。2026-07-25時点の
        //   通常表示での挙動を見た目基準として固定)。
        // - DAW風2段階の「戻る」挙動(小節途中→現在の小節頭、小節頭→1つ前の小節頭)は
        //   「時間的に戻る側のキー」に付随する(通常時=←、Reverse時=→)。
        bool rev = _appSettings.ChartViewReverse;
        // 2026-07-26: Space/Bキーの方向解釈方式(環境設定「表示」)。既定は従来通り見た目方向固定
        // ("visual")。"time"を選ぶと時間(tick)基準に固定され、Reverse中は画面上の方向が逆になる
        // (前進=常に画面上、後退=常に画面下)。↑/↓は対象外(常に見た目方向固定のまま)。
        bool spaceBTimeMode = _appSettings.KeyboardModeSpaceBMode == "time";
        // 2026-07-26: ←/→キーの方向解釈方式(環境設定「表示」、Space/Bと同じ考え方)。
        bool leftRightTimeMode = _appSettings.KeyboardModeLeftRightMode == "time";
        // 2026-07-26: Shift+前進後退キーで「移動元〜移動先」の範囲にある全レーンのノート・フリーズを
        // 選択する(要望対応)。Shiftを押したまま連続で移動すると、最初に押した瞬間の位置を
        // アンカーに固定したまま範囲を伸縮させる(通常のテキストエディタのShift+矢印と同じ挙動)。
        // Shiftを離して(=修飾無しで)移動した場合は、選択そのものは維持したまま「範囲選択モード」
        // (アンカー)だけを終了する(2026-07-26再要望対応: 選択済みオブジェクトを保ったまま
        // 前進後退できるように、という指示でClearSelection呼び出しを撤廃)。選択を明示的に解除したい
        // 場合はEscape(既存機能)を使う。
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        void MoveCursorTracked(Action move)
        {
            long before = _keyboardMode.CursorTick;
            move();
            if (shift)
            {
                _keyboardSelectionAnchorTick ??= before;
                _controller?.SelectRangeAllLanes(_keyboardSelectionAnchorTick.Value, _keyboardMode.CursorTick);
            }
            else if (_keyboardSelectionAnchorTick is not null)
            {
                _keyboardSelectionAnchorTick = null; // アンカーのみ解除、選択済みオブジェクトはそのまま維持
            }
            ScrollKeyboardCursorIntoView();
            InvalidateChartViews();
        }
        // 2026-07-29要望対応: キーボードモード専用のショートカットキー割り当てテーブルから解決する
        // (環境設定「ショートカットキー」→キーボードモード中のショートカットでカスタマイズ可能)。
        // マウスモードの_shortcutChordMapとは別テーブルのため、同じ物理キーが重複して割り当てられる
        // ことを許容する(MainWindow_PreviewKeyDown側で、キーボードモードON中はこちらを先に試す設計)。
        if (_keyboardShortcutChordMap.TryGetValue(e.Key, out var kbId))
        {
            switch (kbId)
            {
                case KeyboardModeShortcutId.CursorUp: // 画面上へ1グリッド(通常=戻る、Reverse=進む)
                    MoveCursorTracked(() => _keyboardMode.MoveCursor(forward: rev));
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.StepForward: // 既定(見た目固定): 画面下へ1グリッド(通常=進む、Reverse=戻る)。
                                                          // "time"モード時は常に前進(Reverse中は画面上へ)。
                    MoveCursorTracked(() => _keyboardMode.MoveCursor(forward: spaceBTimeMode || !rev));
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.CursorDown: // 画面下へ1グリッド(通常=進む、Reverse=戻る、Space/Bのモード設定の対象外)
                    MoveCursorTracked(() => _keyboardMode.MoveCursor(forward: !rev));
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.StepBackward: // 既定(見た目固定): 画面上へ1グリッド(通常=戻る、Reverse=進む)。
                                                           // "time"モード時は常に後退(Reverse中は画面下へ)。
                    MoveCursorTracked(() => _keyboardMode.MoveCursor(forward: !spaceBTimeMode && rev));
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.MeasureBack: // 既定(見た目固定): 画面上へ1小節移動(通常=戻る[2段階]、Reverse=進む)。
                                                          // "time"モード時は常に後退(2段階、Reverse中は画面下方向へ)。
                    MoveCursorTracked(() =>
                    {
                        bool timeBackward = leftRightTimeMode || !rev;
                        if (timeBackward) _keyboardMode.MoveCursorToPreviousMeasureOrCurrentStart();
                        else _keyboardMode.MoveCursorByMeasure(1);
                    });
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.MeasureForward: // 既定(見た目固定): 画面下へ1小節移動(通常=進む、Reverse=戻る[2段階])。
                                                             // "time"モード時は常に前進(Reverse中は画面上方向へ)。
                    MoveCursorTracked(() =>
                    {
                        bool timeBackward = !leftRightTimeMode && rev;
                        if (timeBackward) _keyboardMode.MoveCursorToPreviousMeasureOrCurrentStart();
                        else _keyboardMode.MoveCursorByMeasure(1);
                    });
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.DeleteAtCursor: // 2026-07-29要望対応: 従来の「カーソル位置のノート/フリーズを削除」動作
                    if (_keyboardMode.DeleteAtCursor()) InvalidateChartViews();
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.ToggleVisualTest: // 2026-07-26: キーボードモード中の目視テスト開始/終了ボタン
                    // (マウスモードのSpaceに相当。キーボードモード中はSpaceがカーソル前進に
                    // 割り当て済みのため、既定では代わりにEnterへ割り当てる)。
                    ToggleVisualTest();
                    e.Handled = true;
                    return true;
                case KeyboardModeShortcutId.TogglePane when _splitViewEnabled:
                    // 2026-07-26要望対応: 譜面ビュー分割中、割り当てキーでアクティブペイン(カーソル追従
                    // スクロールの対象)を切り替える。分割OFF中は素通し(既定のフォーカス移動やレーンの
                    // ノート入力キー割当があればそちらへフォールバックする、下のlaneMap判定を参照)。
                    TogglePaneActive();
                    e.Handled = true;
                    return true;
            }
        }

        // 2026-07-26要望対応: 目視テスト中の「ノート配置受付」(環境設定「テスト再生」、既定OFF)。
        // ONの間はノート入力キー配置をプレイテスト用(KeyAssign)に切り替え、キー押下時点の
        // 再生位置(スナップ後)へノートをトグル配置する(通常の編集キー配置=KeyboardInputKeysは使わない)。
        if (_visualTestActive && _appSettings.VisualTestAcceptNoteInput
            && HandleVisualTestNoteInput(e)) return true;

        // ノート入力キー(テンプレートのKeyboardInputKeysで定義されたレーンのみ反応。
        // 未設定(空配列)のレーン/テンプレートでは何も起きない。Shift併用でフリーズ開始/完了)。
        var template = _document.CurrentTemplate;
        var laneMap = KeyLabelMapper.BuildKeyMap(template.Lanes.Count, l => template.Lanes[l].KeyboardInputKeys);
        if (laneMap.TryGetValue(e.Key, out int lane))
        {
            bool changed = shift
                ? _keyboardMode.ToggleFreezeAtCursor(lane, DateTime.UtcNow)
                : _keyboardMode.ToggleNoteAtCursor(lane, DateTime.UtcNow);
            if (changed)
            {
                // 2026-07-26: ノート/フリーズ入力でカーソルが進んだ場合も画面外に出うるためスクロール判定
                ScrollKeyboardCursorIntoView();
                InvalidateChartViews();
            }
            e.Handled = true;
            return true;
        }

        return false;
    }

    /// <summary>目視テスト中の「ノート配置受付」(2026-07-26要望対応)。プレイテスト用キー配置
    /// (KeyAssign)でレーンを判定し、キー押下時点の再生位置(Canvas.PlaybackTick、スナップ後)へ
    /// ノートをトグル配置する。通常編集操作としてUndo履歴に積む(ユーザー確定仕様)。
    /// 対応キーでなければ、または再生位置が未確定(理論上起きないが保険)ならfalseを返し、
    /// 呼び出し元(HandleKeyboardModeKey)の通常キー処理へフォールバックさせる。</summary>
    private bool HandleVisualTestNoteInput(KeyEventArgs e)
    {
        if (_document is null || Canvas.PlaybackTick is not { } liveTick) return false;
        var template = _document.CurrentTemplate;
        var laneMap = KeyLabelMapper.BuildKeyMap(template.Lanes.Count, l => template.Lanes[l].KeyAssign);
        if (!laneMap.TryGetValue(e.Key, out int lane)) return false;

        long tick = _document.Snap.Snap(liveTick);
        bool existed = _document.CurrentTab.Lanes[lane].Notes.Contains(tick);
        _document.Execute(existed ? new DeleteNoteAction(lane, tick) : new PlaceNoteAction(lane, tick));
        InvalidateChartViews();
        e.Handled = true;
        return true;
    }

    // =====================================================================
    // 目視テスト(Space開始/終了、2026-07-17f)と位置表示ヘルパ
    // =====================================================================

    /// <summary>tick位置を「tick / frame / 秒」の複合表記にする(上部パネル表示用、2026-07-17f)</summary>
    private string FormatTickPos(double tick)
    {
        if (_document is null) return "-";
        var engine = _document.Project.CreateTimingEngine();
        long t = (long)Math.Round(tick);
        double frame = ToDisplayFrame(engine.TickToFrame(t));
        return $"{t}t / {frame:0.0}f / {frame / 60.0:0.00}s";
    }

    /// <summary>上部パネルの再生開始フレーム表示を更新する(2026-07-17f)</summary>
    private void UpdateStartFrameText()
    {
        if (_document?.CurrentTab.PlaybackStartFrame is { } f)
        {
            var engine = _document.Project.CreateTimingEngine();
            double displayFrame = ToDisplayFrame(f);
            StartFramePosText.Text = $"{(long)Math.Round(engine.FrameToTick(f))}t / {displayFrame:0.0}f / {displayFrame / 60.0:0.00}s";
        }
        else
        {
            StartFramePosText.Text = "-";
        }
    }

    /// <summary>Ctrl+End: 全レーン中の末尾ノート(通常ノート/フリーズ終端の最大tick)を画面中央へ(2026-07-17f)</summary>
    private void ScrollToLastNote()
    {
        if (_document is null) return;
        long maxTick = -1;
        foreach (var lane in _document.CurrentTab.Lanes)
        {
            foreach (var t in lane.Notes) maxTick = Math.Max(maxTick, t);
            foreach (var f in lane.Freezes) maxTick = Math.Max(maxTick, f.EndTick);
        }
        if (maxTick < 0) return; // ノートが1つも無ければ何もしない
        double y = _document.CurrentLayout.TickToY(maxTick);
        ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - ChartScrollViewer.ViewportHeight / 2));
    }

    // =====================================================================
    // 右パネル: マーカー一覧・ジャンプ(2026-07-26要望対応、第三者提案)
    // =====================================================================

    /// <summary>マーカー一覧の表示用ラッパー。「小節N / frame: コメント」形式で表示する。</summary>
    private sealed record MarkerListEntry(long Tick, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>マーカー一覧を最新化する(Document.Changed購読=RefreshSelectedObjectPanelから相乗りで呼ぶ)。
    /// tick昇順で並べ、小節番号(1始まり)・frame・コメントを表示する。ドキュメント未オープン時は空表示。</summary>
    private void RefreshMarkerList()
    {
        if (_document is null) { MarkerListBox.ItemsSource = null; return; }
        var engine = _document.Project.CreateTimingEngine();
        MarkerListBox.ItemsSource = _document.Project.Markers
            .OrderBy(m => m.Tick)
            .Select(m =>
            {
                var (measureIndex, _) = engine.TickToMeasurePosition(m.Tick);
                double frame = ToDisplayFrame(engine.TickToFrame(m.Tick));
                var comment = string.IsNullOrEmpty(m.Comment) ? "(コメント無し)" : m.Comment;
                return new MarkerListEntry(m.Tick, $"小節{measureIndex + 1} / {frame:0.0}f: {comment}");
            })
            .ToList();
    }

    /// <summary>マーカー一覧のダブルクリックで、そのマーカーのtick位置を画面中央へスクロールする
    /// (ScrollToLastNoteと同じ考え方)。</summary>
    private void MarkerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || MarkerListBox.SelectedItem is not MarkerListEntry entry) return;
        double y = _document.CurrentLayout.TickToY(entry.Tick);
        ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - ChartScrollViewer.ViewportHeight / 2));
    }

    /// <summary>目視テストの「その場中断」ショートカット(マウスモード:Ctrl+Space/キーボードモード:
    /// Ctrl+Enter)共通の処理。中断タイミングの現在の再生位置(Canvas.PlaybackTick、スナップ後)を
    /// 再生開始ラインへ設定し、次回の目視テスト・プレイテストが続きから始まるようにする
    /// (2026-07-26要望対応、2026-09-07要望対応でマウスモード側にも展開)。</summary>
    private void SnapPlaybackStartLineToLivePlaybackTick()
    {
        if (_document is null || Canvas.PlaybackTick is not { } liveTick) return;
        var engine = _document.Project.CreateTimingEngine();
        long snappedTick = _document.Snap.Snap(liveTick);
        _document.CurrentTab.PlaybackStartFrame = engine.TickToFrame(snappedTick);
    }

    private void ToggleVisualTest()
    {
        if (_visualTestActive) StopVisualTest(returnToStart: true);
        else StartVisualTest();
    }

    /// <summary>目視テスト開始: 再生開始フレーム(未設定なら曲頭)から音楽再生+再生位置ライン表示(2026-07-17f)</summary>
    private void StartVisualTest()
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値(InitialSpeed等)を確定してから開始する
        if (_document is null) return;
        if (!_audioLoaded)
        {
            MessageBox.Show(this, "音楽ファイルが読み込まれていません。目視テストには音楽の読み込みが必要です。", "目視テスト", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        double startFrame = _document.CurrentTab.PlaybackStartFrame ?? 0;

        // 2026-07-26: 再生開始ラインが音楽ファイルの実際の長さを超えて置かれていた場合、_audioPlayer.Position
        // がクランプされて曲の末尾に固定され、無音のまま再生位置ライン・スクロールが一切動かなくなる不具合
        // (音が流れない・スクロールが固定される・というバグ報告の原因)。ここで検知して警告し、開始しない。
        if (_audioPlayer.Duration is { } duration && startFrame / 60.0 >= duration.TotalSeconds)
        {
            MessageBox.Show(this,
                "再生開始ラインが音楽ファイルの長さを超えています。ラインをもっと手前へ置き直してください。",
                "目視テスト", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2026-07-26f: ハンドクラップ用にノート出現frame一覧を作り直し、_audioPlayer(NAudioBgmPlayer)へ
        // 登録する(発音判定・PCM重ね合わせはBGMのレンダースレッド内で直接行われる)。
        if (_appSettings.HandClapEnabled && HandClapPlayer.Available)
        {
            var engine = _document.Project.CreateTimingEngine();
            var allFrames = HandClapPlayer.ComputeNoteFrames(_document.CurrentTab, engine);
            var frames = allFrames.Where(f => f >= startFrame).ToList();
            _audioPlayer.SetClapSchedule(HandClapPlayer, frames, _appSettings.HandClapVolume);
        }
        else
        {
            _audioPlayer.SetClapSchedule(null, null, _appSettings.HandClapVolume);
        }

        _audioPlayer.Position = TimeSpan.FromSeconds(startFrame / 60.0);
        _audioPlayer.Play();
        _playbackTimer.Start();
        _visualTestActive = true;

        // 2026-09-13要望対応: WASAPI自動復旧のスタック検知状態をリセットする(PlaybackTimer_Tick参照)。
        _lastTickPositionSeconds = -1;
        _lastTickPositionChangedUtc = DateTime.UtcNow;

        // 2026-09-07要望対応: 「Spaceで目視テストを開始しても無音・再生位置ラインが動かない」不具合の
        // 切り分け用診断情報。開始時点のスナップショットを取り、以後はPlaybackTimer_Tick/StopVisualTestで
        // 追記していく(環境報告に含める、DiagnosticsReport参照)。
        _lastVisualTestDiag = new VisualTestDiagnostics
        {
            StartedAtUtc = DateTime.UtcNow,
            RequestedStartFrame = startFrame,
            AudioLoadedAtStart = _audioLoaded,
            DurationSecondsAtStart = _audioPlayer.Duration?.TotalSeconds,
            KeyboardModeActiveAtStart = _keyboardModeActive,
            SplitViewEnabledAtStart = _splitViewEnabled,
            HandClapEnabledAtStart = _appSettings.HandClapEnabled,
            OutputStateAtStart = _audioPlayer.DiagOutputState,
            PlayingFlagAtStart = _audioPlayer.DiagIsPlayingFlag,
        };
    }

    /// <summary>目視テスト終了。returnToStart=true(Space)なら「表示範囲の一番上が再生開始フレームの
    /// 1小節前」までスクロールを戻す(未解決事項§2-2の終了時挙動)。false(Ctrl+Space)なら
    /// 現在の再生位置表示ラインの位置に留まる(未解決事項§2-1派生の要望)。</summary>
    private void StopVisualTest(bool returnToStart, string reason = "手動")
    {
        if (_lastVisualTestDiag is { } diag) { diag.StoppedAtUtc = DateTime.UtcNow; diag.StopReason = reason; }

        _visualTestActive = false;
        _audioPlayer.Stop();
        _playbackTimer.Stop();
        Canvas.PlaybackTick = null;
        InvalidateChartViews();
        AudioTimeText.Text = "-";
        _audioPlayer.SetClapSchedule(null, null, _appSettings.HandClapVolume); // 2026-07-26f
        // 2026-07-29要望対応: 目視テスト終了後、プレビューは再生開始ライン時点の静止スナップショットへ戻す
        SyncPreviewStartFrame();

        if (!returnToStart || _document is null) return;
        ReturnScrollToStartFrame();
    }

    /// <summary>「表示範囲の一番上が再生開始フレームの1小節前」までスクロールを戻す
    /// (未解決事項§2-2の終了時挙動。目視テストSpace終了とプレイテスト終了で共用、2026-07-17g)</summary>
    private void ReturnScrollToStartFrame()
    {
        if (_document is null) return;
        var engine = _document.Project.CreateTimingEngine();
        double startFrame = _document.CurrentTab.PlaybackStartFrame ?? 0;
        long startTick = Math.Max(0, (long)Math.Round(engine.FrameToTick(startFrame)));
        var (measure, _) = engine.TickToMeasurePosition(startTick);
        long topTick = engine.MeasureStartTick(Math.Max(0, measure - 1));
        double y = _document.CurrentLayout.TickToY(topTick);
        // 2026-07-22: 通常は「表示範囲の一番上」、Reverse時は「表示範囲の一番下」がtopTickに来るようにする
        // (Reverseでは進行方向が上向きになるため、リード込みの基準を下端に置くのが対称な挙動)。
        double target = _appSettings.ChartViewReverse ? y - ChartScrollViewer.ViewportHeight : y;
        ChartScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
    }

    // =====================================================================
    // プレイテスト(Ctrl+P、仕様書12.2、2026-07-17g)
    // =====================================================================

    private static double PlaytestHiSpeedValues_Nearest(double v) =>
        Math.Clamp(Math.Round(v * 4) / 4, 0.25, 10.0); // 0.25刻み(2026-07-19)

    /// <summary>ウィンドウサイズ倍率の選択肢(x0.5〜3、2026-07-17h)</summary>
    private static readonly List<double> PlaytestScaleValues = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    /// <summary>プレイ画面プレビューの表示サイズ倍率選択肢(2026-07-29要望対応、25%〜200%)。</summary>
    private static readonly List<double> PreviewScaleValues = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>上部パネルのプレイテスト設定変更をAppSettingsへ保存する</summary>
    private void PlaytestSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.PlaytestReverse = PlaytestReverseCheck.IsChecked == true;
        _appSettings.PlaytestAutoPlay = PlaytestAutoPlayCheck.IsChecked == true;
        if (PlaytestHiSpeedCombo.SelectedItem is double hs) _appSettings.PlaytestHiSpeed = hs;
        if (PlaytestScaleCombo.SelectedItem is double sc) _appSettings.PlaytestWindowScale = sc;
        _appSettings.Save(AppPaths.SettingsFilePath);
        // 2026-07-29要望対応: Reverse/HiSpeed等、プレイテスト・プレビューに関わる設定が変わった
        // タイミングで即座にプレビューへ反映する。
        RefreshPreviewPanel();
    }

    // =====================================================================
    // ノート音
    // =====================================================================

    /// <summary>環境設定「テスト再生 > 全般」で選択中の音声ファイル(./sounds内)を一度だけ
    /// 読み込むプレイヤー(遅延初期化)。選択ファイルが環境設定で変更された場合は
    /// OpenPreferences側でこのキャッシュをnullへ戻し、次回アクセス時に再読込させる。</summary>
    private HandClapPlayer? _handClapPlayer;
    private HandClapPlayer HandClapPlayer
    {
        get
        {
            if (_handClapPlayer is not null) return _handClapPlayer;
            var soundsDir = AppPaths.FindAssetDir("sounds");
            var path = soundsDir is null ? "" : Path.Combine(soundsDir, _appSettings.NoteSoundFileName);
            _handClapPlayer = new HandClapPlayer(path);
            return _handClapPlayer;
        }
    }

    private void HandClap_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.HandClapEnabled = HandClapCheck.IsChecked == true;

        if (double.TryParse(HandClapVolumeBox.Text, out var pct))
        {
            pct = Math.Clamp(pct, 0, 100);
            _appSettings.HandClapVolume = pct / 100.0;
            HandClapVolumeBox.Text = Math.Round(pct).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            HandClapVolumeBox.Text = Math.Round(_appSettings.HandClapVolume * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        _audioPlayer.SetClapVolume(_appSettings.HandClapVolume); // 2026-07-26f: 即時反映

        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>再生速度(目視テスト・プレイテスト共通、2026-07-23)</summary>
    private static double PlaybackSpeedValues_Nearest(double v) => Math.Clamp(Math.Round(v * 10) / 10, 0.1, 2.0);

    /// <summary>PlaybackSpeedCombo.SelectedItemの変更をAppSettingsへ反映させたくない場合に立てるガード
    /// (2026-07-26: ピッチ指定ダイアログ確定後、コンボ表示だけを近似値へ合わせる際に使用)。</summary>
    private bool _suppressPlaybackSpeedComboEvent;

    private void PlaybackSpeedCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressPlaybackSpeedComboEvent) return;
        if (PlaybackSpeedCombo.SelectedItem is not double v) return;
        _appSettings.PlaybackSpeed = v;
        _appSettings.Save(AppPaths.SettingsFilePath);
        _audioPlayer.SpeedRatio = v; // 目視テスト側。プレイテスト側はStartPlaytest時に都度渡す
    }

    /// <summary>「ピッチで指定...」ボタン(2026-07-26要望対応)。半音移動量からspeedRatio = 2^(移動量/12)を
    /// 計算し、PlaybackSpeedCombo(0.1刻みのプリセット)と同じ`AppSettings.PlaybackSpeed`へ反映する。
    /// 計算結果はプリセットの0.1刻みに一致しないことが多いため、コンボ表示は近似値に合わせるだけに留め、
    /// 実際に適用される値(_appSettings.PlaybackSpeed/_audioPlayer.SpeedRatio)は計算値そのものを使う。</summary>
    private void PlaybackSpeedByPitch_Click(object sender, RoutedEventArgs e)
    {
        int currentSemitones = (int)Math.Round(12.0 * Math.Log2(Math.Max(0.0001, _appSettings.PlaybackSpeed)));
        currentSemitones = Math.Clamp(currentSemitones, PitchShiftDialog.MinSemitones, PitchShiftDialog.MaxSemitones);
        var semitones = PitchShiftDialog.Ask(this, currentSemitones);
        if (semitones is not { } s) return;

        double ratio = Math.Round(PitchShiftDialog.RatioFromSemitones(s), 4);
        _appSettings.PlaybackSpeed = ratio;
        _appSettings.Save(AppPaths.SettingsFilePath);
        _audioPlayer.SpeedRatio = ratio; // 目視テスト側。プレイテスト側はStartPlaytest時に都度渡す

        _suppressPlaybackSpeedComboEvent = true;
        PlaybackSpeedCombo.SelectedItem = PlaybackSpeedValues_Nearest(ratio); // コンボの表示だけ近似値に合わせる
        _suppressPlaybackSpeedComboEvent = false;
    }

    /// <summary>「プレイテストへ反映」トグル(2026-08-08要望対応)。既定OFF=プレイテストは常に等倍(1.0倍)で
    /// 再生し、目視テスト側の「再生速度」欄の設定に影響されない。ONの間だけ、プレイテスト開始時
    /// (StartPlaytest)に_appSettings.PlaybackSpeedの値をそのまま渡す。</summary>
    private void ReflectPlaybackSpeedInPlaytestToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateKeepScrollSpeedToggleEnabled(); // 2026-08-08d: 親トグルの状態が変わるたびに追従させる(_initializedガードより前に行う)
        if (!_initialized) return;
        _appSettings.ReflectPlaybackSpeedInPlaytest = ReflectPlaybackSpeedInPlaytestToggle.IsChecked == true;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>「スクロール速度を維持」トグル(2026-08-08c要望対応)。既定OFF。ONの間、プレイテストの
    /// スクロール速度計算に(1/再生速度)を追加で乗算し、再生速度を変えても見た目のスクロール速度が
    /// 変わらないようにする(PlaytestWindowコンストラクタ参照)。</summary>
    private void KeepScrollSpeedInPlaytestToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _appSettings.KeepScrollSpeedInPlaytest = KeepScrollSpeedInPlaytestToggle.IsChecked == true;
        _appSettings.Save(AppPaths.SettingsFilePath);
    }

    /// <summary>2026-08-08d要望対応: 「スクロール速度を維持」は「プレイテストへ反映」がONの間しか
    /// 意味を持たない(OFFの間はプレイテストの再生速度が常に1.0倍固定のため、(1/再生速度)=1倍で
    /// 何の効果も出ない)。効果の無いトグルを押せてしまうと紛らわしいため、UI上も「プレイテストへ反映」が
    /// ONの時だけ操作可能にする(OFFの間はグレーアウト。チェック状態自体は保持し、再度ONにした際に
    /// 元の設定へ戻るようにする)。</summary>
    private void UpdateKeepScrollSpeedToggleEnabled()
    {
        KeepScrollSpeedInPlaytestToggle.IsEnabled = ReflectPlaybackSpeedInPlaytestToggle.IsChecked == true;
    }

    /// <summary>音量(0〜100%)を確定させる共通処理(2026-07-26)。スライダー・数値入力欄どちらの
    /// 変更でも呼ばれ、もう片方への反映・MediaPlayer.Volumeへの適用・設定保存をまとめて行う。</summary>
    private void ApplyVolumePercent(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _suppressVolumeEvents = true;
        VolumeSlider.Value = percent;
        VolumeBox.Text = Math.Round(percent).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _suppressVolumeEvents = false;

        double volume = percent / 100.0;
        _audioPlayer.Volume = volume;
        _appSettings.PlaybackVolume = volume; // エディタ全体の既定値・新規プロジェクトのフォールバック用
        _appSettings.Save(AppPaths.SettingsFilePath);
        // 2026-08-02要望対応: 音源によって適正音量が異なるため、プロジェクトごとにも保存する。
        if (_document is not null) _document.Project.PlaybackVolume = volume;
    }

    /// <summary>スライダー操作: 動かすたびに数値入力欄・実際の音量へ即時反映する。</summary>
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppressVolumeEvents) return;
        ApplyVolumePercent(e.NewValue);
    }

    /// <summary>数値入力欄での直接入力を確定する(フォーカスを外した時点で反映)。
    /// 不正な値ならスライダーの現在値へ戻す。</summary>
    private void VolumeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressVolumeEvents) return;
        if (!double.TryParse(VolumeBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            VolumeBox.Text = Math.Round(VolumeSlider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        ApplyVolumePercent(v);
    }

    private void PlaytestOffset_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (double.TryParse(PlaytestOffsetBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ofs))
        {
            _appSettings.PlaytestOffsetFrames = ofs;
            _appSettings.Save(AppPaths.SettingsFilePath);
        }
        else
        {
            PlaytestOffsetBox.Text = _appSettings.PlaytestOffsetFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>タイトルバー表示: 「プロジェクト名 (*) - IDE (ITTN-DANONI-EDITOR)」(2026-07-19b、
    /// 未解決事項§2-6。2026-08-08: タイトルバー表記を「IDE (ITTN-DANONI-EDITOR)」へ変更)。</summary>
    private void UpdateWindowTitle()
    {
        var name = _document?.Project.ProjectName ?? "";
        var star = _document?.IsModified == true ? " *" : "";
        Title = string.IsNullOrEmpty(name) ? "IDE (ITTN-DANONI-EDITOR)" : $"{name}{star} - IDE (ITTN-DANONI-EDITOR)";
    }

    /// <summary>未保存の変更がある場合の終了確認(未解決事項§2-6、環境設定でON/OFF可、2026-07-19b)</summary>
    /// <summary>
    /// 終了時の未保存確認(2026-07-20: マルチプロジェクトタブ対応、開いている全プロジェクト分をまとめて確認)。
    /// アクティブなセッションぶんの実行時状態(コントローラ・パス)を先に書き戻してから、
    /// 全セッションのIsModifiedを走査する(IsModified自体はEditorDocumentが常時保持しているため
    /// アクティブ/非アクティブに関わらず正しい値が取れるが、パスの書き戻しは保存先解決に必要)。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_collab is not null)
        {
            _ = _collab.DisconnectAsync(); // 2026-09-20: ベストエフォート、終了処理はブロックしない
            _collab = null;
        }
        if (_rendezvousHelper is not null)
        {
            _ = _rendezvousHelper.DisposeAsync(); // 2026-09-20: ベストエフォート
            _rendezvousHelper = null;
        }
        if (!_appSettings.ConfirmUnsavedOnClose) return;

        SyncActiveSessionBeforeSwitch();
        var unsaved = _sessions.Where(s => s.Document.IsModified).ToList();
        if (unsaved.Count == 0) return;

        var names = string.Join("\n", unsaved.Select(s =>
            "・" + (string.IsNullOrWhiteSpace(s.Document.Project.ProjectName) ? "Untitled" : s.Document.Project.ProjectName)));
        var r = MessageBox.Show(this,
            $"以下のプロジェクトに未保存の変更があります:\n{names}\n\n" +
            "「はい」= 全て保存してから終了\n「いいえ」= 保存せず終了\n「キャンセル」= 終了を中止",
            "終了の確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r != MessageBoxResult.Yes) return;

        // 各セッションを一時的にアクティブ扱いにしてSaveProject_Clickの保存ダイアログ処理を使い回す
        // (未保存パスのセッションはここでSaveFileDialogが出る)。終了後は元のアクティブ状態へ戻す。
        var (activeDocBefore, activePathBefore) = (_document, _currentFilePath);
        foreach (var s in unsaved)
        {
            _document = s.Document;
            _currentFilePath = s.FilePath;
            SaveProject_Click(this, new RoutedEventArgs());
            s.FilePath = _currentFilePath;
            if (s.Document.IsModified) e.Cancel = true; // 保存ダイアログのキャンセル等
        }
        _document = activeDocBefore;
        _currentFilePath = activePathBefore;
        UpdateWindowTitle();
    }

    private void StartPlaytest()
    {
        CommitPendingEdits(); // 2026-08-06: 入力途中の値(InitialSpeed等)を確定してから開始する
        if (_document is null) return;
        if (!_audioLoaded)
        {
            MessageBox.Show(this, "音楽ファイルが読み込まれていません。プレイテストには音楽の読み込みが必要です。", "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_visualTestActive) StopVisualTest(returnToStart: false); // 目視テスト中なら止めてから

        _appSettings.StatPlaytestLaunchCount++; // 2026-07-26: 統計情報
        _appSettings.Save(AppPaths.SettingsFilePath);

        var win = new PlaytestWindow(
            _document,
            _appSettings.PlaytestReverse,
            _appSettings.PlaytestHiSpeed,
            _appSettings.PlaytestOffsetFrames,
            _document.CurrentTab.PlaybackStartFrame ?? 0,
            _appSettings.PlaytestWindowScale,
            _appSettings.PlaytestAutoPlay,
            _appSettings.PlaytestQuitKeyDelete,
            _appSettings.PlaytestQuitKeyEscape,
            // 2026-08-08要望対応: 「プレイテストへ反映」トグルがOFFの間は、目視テスト側の再生速度設定に
            // 関わらずプレイテストは常に等倍(1.0倍)で再生する。ONの時のみ実際の設定値を渡す。
            _appSettings.ReflectPlaybackSpeedInPlaytest ? _appSettings.PlaybackSpeed : 1.0,
            _appSettings.PlaybackVolume, // 2026-07-21: UIの音量設定をプレイテストにも反映
            _appSettings) // 2026-07-26: ウィンドウ幅設定(環境設定「プレイテスト」)の解決に使う
        { Owner = this };
        win.ShowDialog();

        // 2026-07-26: 統計情報(手動プレイ中に打鍵で消えたノート数の累計)
        if (win.NotesClearedByKeypress > 0)
        {
            _appSettings.StatPlaytestNotesCleared += win.NotesClearedByKeypress;
            _appSettings.Save(AppPaths.SettingsFilePath);
        }

        // 終了後はテスト開始位置に戻る(Space目視テスト終了と同じ挙動、ユーザー確定仕様)
        ReturnScrollToStartFrame();
    }
}
