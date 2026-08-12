using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Playtest;
using DanoniEditor.Core.Settings;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// プレイテスト画面(仕様書12.2、2026-07-17g)。別ウィンドウ・コード構築(他ダイアログと同方式)。
/// - 判定はPlaytestEngine(danoniplus本家準拠、Core層・ユニットテスト済み)に委譲
/// - スクロール方向はテンプレートのScrollDirection("down"=標準/"up"=折返し逆行)に従い、
///   Reverse設定で全レーン反転(ダブルスクロールキー種対応)
/// - キー割当はkeyAssignラベルからの自動変換(キーコンフィグUIは将来課題)
/// - 終了はDelete/BackSpace/Escape(呼び出し元が終了後のスクロール復帰を行う)
/// - ライフゲージ・リザルト表示なし、判定文字は画面中央+コンボ表示(ユーザー確定仕様)
/// </summary>
internal sealed class PlaytestWindow : Window
{
    private const double ArrowSize = 50;   // C_ARW_WIDTH(本家準拠)
    private const double StepY = KeyTemplate.StepY; // 70

    // =====================================================================
    // 横幅の自動決定(本家autoSpread準拠、2026-07-17h調査)
    // 本家danoni_main.js: autoSpread(既定ON)時、g_sWidth = max(基準600, minWidth{key} ?? 600)。
    // playingWidthヘッダー未指定("default")ならこのg_sWidthがそのままプレイ領域幅になる。
    // minWidth定義はdanoni_constants.js(develop)より。未定義キーはminWidthDefault=600。
    // (minWidth5=500/minWidth7i=550は基準600未満のため実効600になる点も本家と同一)
    // =====================================================================
    private const double BaseWidth = 600;        // 本家g_sWidth既定値
    private const double MinWidthDefault = 600;  // 本家minWidthDefault
    private static readonly Dictionary<string, double> MinWidthByKeyType = new()
    {
        ["5"] = 500, ["7i"] = 550,
        ["11"] = 650, ["11i"] = 650, ["11j"] = 650, ["16i"] = 650,
        ["12i"] = 675, ["13"] = 750, ["17"] = 825, ["23"] = 900,
    };

    /// <summary>キー種に応じたプレイ領域幅(本家autoSpreadと同じ計算)</summary>
    internal static double AutoSpreadWidth(string keyTypeId) =>
        Math.Max(BaseWidth, MinWidthByKeyType.TryGetValue(keyTypeId, out var w) ? w : MinWidthDefault);

    /// <summary>環境設定「プレイテスト」のウィンドウ幅設定から、実際に使うフォールバック幅(px)を
    /// 解決する(2026-07-26、2026-07-26 "auto"モード追加)。dos.txtのplayingWidthヘッダーが
    /// 明示されている場合はこの値より常にそちらが優先される(呼び出し元のHeaderDouble経由、仕様書12.2)。
    /// ヘッダー未指定時のみここへ来る。</summary>
    internal static double ResolveWindowWidthFallback(AppSettings settings, string currentTabKeyTypeId)
    {
        if (settings.PlaytestWindowWidthMode == "px")
            return Math.Max(1, settings.PlaytestWindowWidthPx);

        // "auto"モード: 固定のキー種を指定せず、常に今プレイテストを開いているタブのキー種で決める
        if (settings.PlaytestWindowWidthMode == "auto")
            return AutoSpreadWidth(currentTabKeyTypeId);

        // "keyType"モード: 指定キー種が未設定なら、従来通り実際に開いているタブのキー種を使う
        var keyTypeId = string.IsNullOrEmpty(settings.PlaytestWindowWidthKeyType)
            ? currentTabKeyTypeId
            : settings.PlaytestWindowWidthKeyType;
        return AutoSpreadWidth(keyTypeId);
    }

    /// <summary>環境設定「プレイテスト」のキー種ごとの採用キーパターン設定から、実際に適用する
    /// パターン番号を解決する(2026-07-26e、0=既定パターン)。settings未指定・未設定キー種・
    /// 負値は0扱い(KeyTemplate.WithPattern側でも範囲外は自動的にthisを返すため二重に安全)。</summary>
    internal static int ResolvePlaytestPatternIndex(AppSettings? settings, string keyTypeId) =>
        settings is not null && settings.PlaytestPatternByKeyType.TryGetValue(keyTypeId, out var idx) && idx > 0
            ? idx : 0;

    private readonly EditorDocument _doc;
    private readonly KeyTemplate _template;
    private readonly PlaytestEngine _engine;
    private readonly AppSettings? _appSettings; // 2026-07-26要望対応: 表示位置の保存・復元に使う
    private readonly NAudioBgmPlayer _player = new(); // 2026-07-26f: WPF MediaPlayerから移行(ハンドクラップのサンプル精度スケジューリング対応)
    /// <summary>2026-07-26要望対応: 「クラップは合っているのにノートだけ数F遅れる」報告への対策として、
    /// 固定間隔のDispatcherTimer(16ms、UIスレッドのNormal優先度)から、WPFが実際に次のフレームを
    /// 合成する直前に同期して発火するCompositionTarget.Renderingへ切り替えた。DispatcherTimerだと
    /// 「位置を読む瞬間」と「それが画面に出る瞬間」の間に別のディスパッチャ処理が挟まりズレうるが、
    /// Renderingイベントはその2つがほぼ一致するため、UIスレッドが混み合う状況でもズレが生じにくい。
    /// 静的イベントのため、購読しっぱなしにするとウィンドウを閉じた後もハンドラが生き続けて
    /// リークするので、Closed時に必ず解除する(_renderingSubscribedで二重解除を防ぐ)。</summary>
    private bool _renderingSubscribed;
    private readonly bool _reverse;
    private readonly double _hiSpeed;
    private readonly double _startFrame;
    private readonly double _playingWidth;
    private readonly double _playingHeight;
    private readonly bool _autoPlay;
    private readonly bool _quitKeyDelete;
    private readonly bool _quitKeyEscape;
    private readonly double _playbackSpeed;
    private readonly double _volume;
    private readonly int _startupWaitMs;

    /// <summary>2026-07-26g要望対応: 起動時ウェイトの仕様変更。旧仕様は「再生開始ラインの位置で一時停止
    /// してからウェイト後に再生開始」だったが、新仕様は「再生開始ラインからウェイト分だけ手前の位置から
    /// 連続再生を始める(=ノーツ側は再生開始ラインから始まる譜面として扱い、ウェイト区間はリードイン)」。
    /// この値が実際に_player.Positionへ設定する開始フレーム(_startFrameからウェイト分だけ手前、0未満には
    /// ならない)。</summary>
    private double PlayStartFrame => Math.Max(0, _startFrame - _startupWaitMs * 60.0 / 1000.0);
    private readonly int _patternIndex; // 2026-07-26e: 採用中のキーパターン番号(0=既定)
    private bool _closed;
    private double _currentFrame;

    // --- スクロール速度・ステップゾーン位置(2026-07-20、danoniplus本体 js/danoni_main.js準拠) ---
    // 本家の実ピクセル移動量は HiSpeed(表示倍率) × baseSpeed × 2。baseSpeedはplayingHeight/stepY/stepYR
    // ヘッダーから算出される画面サイズ補正値(4422-4431行目)。stepY/stepYRヘッダーはステップゾーンの
    // 上側/下側位置も個別にずらす(14572行目のmainSpriteシフト+15399行目のreverseStepY計算より導出)。
    private readonly double _baseSpeed;
    private readonly double _baseScrollSpeed; // = HiSpeed × baseSpeed × 2 (speed_data倍率を含まない定数部)
    private readonly double _stepYTop;
    private readonly double _stepYBottom;

    // --- speed_data/boost_data(2026-07-21、danoniplus本体準拠) ---
    // speed_data: 今この瞬間(現在フレーム)に画面上の全ノートへ一斉に効くグローバル倍率。
    // boost_data: 各ノート自身の到達フレームで生成時に一度だけ決定され、以後変わらない固定倍率
    // (本家13431-13445/13535/15489行目)。本家はフレーム毎の加算で位置を求めるが、区分定数関数
    // (speed_data)の積分は区分線形になるため、ここでは解析的な累積距離関数として計算する。
    private readonly List<(double Frame, double Value)> _speedBreaks;
    private readonly List<(double Frame, double Value)> _boostBreaks;

    private readonly Dictionary<Key, int> _keyToLane = [];
    private readonly HashSet<Key>[] _pressedKeys;
    private readonly List<string> _unmappedLabels = [];

    // --- オートプレイ(2026-07-20)。各レーンの矢印/フリーズをFrame昇順に1件ずつ処理するカーソル ---
    private readonly int[] _autoPlayArrowCursor;
    private readonly int[] _autoPlayFreezeCursor;

    // 2026-07-25: 本家準拠(danoni_main.js: charaJ/comboJ ←→ charaFJ/comboFJ)で、通常ノート(矢印)と
    // フリーズの判定文字・コンボ表示を別系統で持つ。同時に判定が発生しても互いの表示を上書きしない。
    private string _judgeText = "";
    private Brush _judgeBrush = Brushes.White;
    private string _comboText = "";

    private string _freezeJudgeText = "";
    private Brush _freezeJudgeBrush = Brushes.White;
    private string _freezeComboText = "";

    // 2026-07-26: ステップゾーンの判定ヒットフラッシュ(danoniplus本体danoni_main.js judgeArrow内の
    // stepHitTargetArrowを参考に移植)。本体は通常ノート判定(イイ/シャキン/マターリ/ショボーン/ウワァン、
    // フリーズは対象外)の瞬間にステップゾーンへ「一回り大きい(±15px)ノート画像を判定色で不透明度0.75、
    // 4フレームだけ(フェード無しで即消灯)表示する」演出を入れており、これをそのまま再現する
    // (本家: C_ARW_WIDTH+30サイズ・opacity 0.75・C_FRM_HITMOTION=4フレーム)。
    private const int StepHitFrames = 4;
    private const double StepHitOpacity = 0.75;
    private const double StepHitSizeAdd = 30;
    private readonly PlayJudge?[] _stepHitJudge;
    private readonly int[] _stepHitFramesRemaining;
    /// <summary>2026-07-26要望対応: ヒットフラッシュを常にステップゾーン上ではなく、実際に消去された
    /// (判定された)座標に表示する(プレイヤーがその上下のズレでタイミング誤差を知覚できるように)。
    /// 判定確定時のノート位置(YOf相当)を保存し、フラッシュ表示中はその座標に固定する。</summary>
    private readonly double[] _stepHitY;

    // 2026-08-02要望対応: 「空押し」ステップゾーン点灯(danoniplus本体danoni_main.js、
    // stepDiv要素(main_stepKeyDownクラス)を参考に移植)。本家は判定対象ノートの有無に関わらず、
    // キーを押している間だけステップゾーンを点灯させ(離すと即消灯)、入力がきちんと拾われている
    // ことをプレイヤーへ視覚的にフィードバックする。ヒットフラッシュ(_stepHitJudge等、判定成功時のみ
    // 数フレームだけ判定色で光る)とは別系統の演出で、両方同時に表示され得る(本家も同様に
    // stepDiv/stepHitを別レイヤーとして重ねて描画している)。同一レーン複数キーの同時押しは
    // OnKeyDownInput/OnKeyUpInputの_pressedKeys遷移(0→1/1→0)に合わせて1回だけON/OFFする。
    private readonly bool[] _stepKeyDown;
    private const double StepKeyDownOpacity = 0.6;

    private readonly PlaySurface _surface;

    public PlaytestWindow(EditorDocument doc, bool reverse, double hiSpeed, double offsetFrames, double startFrame, double windowScale = 1.0, bool autoPlay = false,
        bool quitKeyDelete = true, bool quitKeyEscape = true, double playbackSpeed = 1.0, double volume = 1.0,
        AppSettings? appSettings = null)
    {
        _doc = doc;
        _appSettings = appSettings;
        // 2026-07-26: プレイテスト中は日本語入力ON状態だと判定キー等がIME変換確定操作に
        // 奪われてしまうため、ウィンドウ全体でIMEを無効化する(譜面ビューと同様の対応)。
        InputMethod.SetIsInputMethodEnabled(this, false);
        // 2026-07-26e: キー種ごとの採用キーパターン(環境設定「プレイテスト」)を反映する。
        // エディタ本体の譜面ビューはdoc.CurrentTemplate(パターン0)をそのまま使い続けており、
        // プレイテストのみここでKeyTemplate.WithPatternにより見た目・キー入力を差し替える。
        _patternIndex = ResolvePlaytestPatternIndex(appSettings, doc.CurrentTemplate.KeyTypeId);
        _template = doc.CurrentTemplate.WithPattern(_patternIndex);
        _reverse = reverse;
        _hiSpeed = Math.Max(0.25, hiSpeed);
        _startFrame = startFrame;
        _autoPlay = autoPlay;
        _quitKeyDelete = quitKeyDelete;
        _quitKeyEscape = quitKeyEscape;
        _playbackSpeed = Math.Clamp(playbackSpeed, 0.1, 2.0); // 2026-07-23: 再生速度スライダー
        _volume = Math.Clamp(volume, 0.0, 1.0); // 2026-07-21: UIの音量設定をプレイテストにも反映
        _startupWaitMs = Math.Max(0, appSettings?.PlaytestStartupWaitMs ?? 0); // 2026-07-26d: 起動時ウェイト
        // 幅: playingWidthヘッダー指定 > 環境設定「プレイテスト」のウィンドウ幅設定(2026-07-26、
        // 未設定時は従来通り本家autoSpread準拠のキー種別自動決定にフォールバック)
        double widthFallback = appSettings is not null
            ? ResolveWindowWidthFallback(appSettings, _template.KeyTypeId)
            : AutoSpreadWidth(_template.KeyTypeId);
        _playingWidth = HeaderDouble("playingWidth", widthFallback);
        _playingHeight = HeaderDouble("playingHeight", 500);

        // 2026-07-20: stepY/stepYRヘッダー(本家C_STEP_Y=70基準)からスクロール速度補正・
        // ステップゾーン位置を算出。distY/baseSpeedの式はjs/danoni_main.js 4422-4431行目に準拠。
        double stepYHeader = HeaderDoubleAny("stepY", StepY);
        double stepYRHeader = HeaderDoubleAny("stepYR", 0);
        double distY = _playingHeight - StepY + stepYRHeader;
        _baseSpeed = 1 + ((distY - (stepYHeader - StepY) * 2) / (500 - StepY) - 1) * 0.85;
        _baseScrollSpeed = _hiSpeed * _baseSpeed * 2;
        // 2026-08-08c要望対応: 「スクロール速度を維持」トグル(AppSettings.KeepScrollSpeedInPlaytest)がONの場合、
        // 再生速度(_playbackSpeed、音声のSpeedRatioにのみ影響する)に関わらずノートの見た目のスクロール速度を
        // 一定に保つため、(1/再生速度)を追加で乗算しておく。「プレイテストへ反映」がOFFの間は_playbackSpeedが
        // 常に1.0で渡ってくるため、このトグルの値自体は実質的に影響しない(MainWindow.StartPlaytest参照)。
        if (appSettings?.KeepScrollSpeedInPlaytest == true) _baseScrollSpeed /= _playbackSpeed;
        _stepYTop = stepYHeader + ArrowSize / 2;
        _stepYBottom = _playingHeight + stepYRHeader - stepYHeader - ArrowSize / 2;

        double scale = Math.Clamp(windowScale, 0.5, 3.0);

        var timing = doc.Project.CreateTimingEngine();

        // 2026-07-21: speed_data/boost_data(tick単位)をフレーム基準のbreakpointリストに変換
        // 2026-07-30追記: リンク(自動スムージング)区間の中間点もExpandLinkedEventsで展開してから変換する。
        _speedBreaks = DanoniEditor.Core.Timing.ValueEventSmoothing.ExpandLinkedEvents(doc.CurrentTab.SpeedEvents)
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + offsetFrames, e.Value))
            .OrderBy(b => b.Frame)
            .ToList();
        _boostBreaks = DanoniEditor.Core.Timing.ValueEventSmoothing.ExpandLinkedEvents(doc.CurrentTab.BoostEvents)
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + offsetFrames, e.Value))
            .OrderBy(b => b.Frame)
            .ToList();

        // 2026-07-26g要望対応: 「再生開始ラインから始まる譜面を遊ぶ」形式。再生開始ライン(_startFrame)
        // より手前のノート/フリーズは判定対象から除外する(存在しないものとして扱う)。
        _engine = new PlaytestEngine(
            doc.CurrentTab,
            timing,
            doc.Project.FrzAttempt,
            offsetFrames,
            minFrame: startFrame);
        _engine.Judged += OnJudged;

        // ノート音。環境設定でONの場合のみ選択中の音声ファイル(./sounds内)を読み込み、
        // ノート出現frame一覧をあらかじめ用意した上で、BGM再生エンジン(_player)自身のレンダー
        // スレッド内で発音判定・PCM重ね合わせを行うよう登録する(UIスレッドのポーリングを
        // 介さないサンプル精度スケジューリング)。
        if (appSettings?.HandClapEnabled == true)
        {
            var soundsDir = AppPaths.FindAssetDir("sounds");
            var clapPlayer = new HandClapPlayer(soundsDir is null ? "" : System.IO.Path.Combine(soundsDir, appSettings.NoteSoundFileName));
            if (clapPlayer.Available)
            {
                var allFrames = HandClapPlayer.ComputeNoteFrames(doc.CurrentTab, timing, offsetFrames);
                var frames = allFrames.Where(f => f >= startFrame).ToList();
                _player.SetClapSchedule(clapPlayer, frames, appSettings.HandClapVolume);
            }
        }

        _pressedKeys = new HashSet<Key>[_template.Lanes.Count];
        for (int i = 0; i < _pressedKeys.Length; i++) _pressedKeys[i] = [];
        _autoPlayArrowCursor = new int[_template.Lanes.Count];
        _autoPlayFreezeCursor = new int[_template.Lanes.Count];
        _stepHitJudge = new PlayJudge?[_template.Lanes.Count];
        _stepHitFramesRemaining = new int[_template.Lanes.Count];
        _stepHitY = new double[_template.Lanes.Count];
        _stepKeyDown = new bool[_template.Lanes.Count];
        BuildKeyMap();

        Title = $"プレイテスト - {doc.Project.ProjectName} [{doc.CurrentTab.DifficultyName}]";
        Background = Brushes.Black;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        // 2026-07-26要望対応(第三者提案): 前回閉じた時点の表示位置を引き継ぐ。保存が無い場合(初回起動等)
        // は従来通り親ウィンドウ中央に表示する。仮想スクリーン範囲外(モニタ構成変更等)の場合も
        // フォールバックする(MainWindowのウィンドウ位置復元と同じ考え方)。
        if (_appSettings is { PlaytestWindowLeft: { } left, PlaytestWindowTop: { } top }
            && left >= SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top >= SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var root = new DockPanel();
        var quitKeyNames = new List<string>();
        if (_quitKeyDelete) quitKeyNames.Add("Delete");
        if (_quitKeyEscape) quitKeyNames.Add("Esc");
        var quitKeyText = quitKeyNames.Count > 0 ? $"{string.Join(" / ", quitKeyNames)} で終了" : "終了キー未設定";
        quitKeyText += " / BackSpaceでやり直し"; // 2026-07-26d: BackSpaceは再生開始フレームからのやり直し専用

        var speedBoostTags = new List<string>();
        if (_speedBreaks.Count > 0) speedBoostTags.Add("Speed");
        if (_boostBreaks.Count > 0) speedBoostTags.Add("Boost");

        var infoText = $"{quitKeyText}  |  HS x{_hiSpeed:0.##}"
            + (Math.Abs(_baseSpeed - 1.0) > 0.001 ? $" (Δv {_baseSpeed * 100:0}%)" : "")
            + (speedBoostTags.Count > 0 ? $" [{string.Join("+", speedBoostTags)}]" : "")
            + $"  |  Reverse {(_reverse ? "ON" : "OFF")}  |  AutoPlay {(_autoPlay ? "ON" : "OFF")}  |  {_playingWidth:0}x{_playingHeight:0} x{scale:0.##}"
            + (_patternIndex > 0 ? $"  |  パターン{_patternIndex}" : "") // 2026-07-26e
            + (_unmappedLabels.Count > 0 ? $"  |  未割当キー: {string.Join(",", _unmappedLabels)}" : "");
        var info = new TextBlock
        {
            Text = infoText,
            Foreground = Brushes.Gray,
            Background = Brushes.Black,
            Padding = new Thickness(8, 4, 8, 4),
        };
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);

        _surface = new PlaySurface(this) { Width = _playingWidth, Height = _playingHeight };
        // ウィンドウサイズ倍率(2026-07-17h): 論理座標(判定・配置)はそのまま、表示のみ拡縮する
        if (Math.Abs(scale - 1.0) > 0.001)
            _surface.LayoutTransform = new ScaleTransform(scale, scale);
        root.Children.Add(_surface);
        Content = root;

        PreviewKeyDown += OnKeyDownInput;
        PreviewKeyUp += OnKeyUpInput;
        ContentRendered += (_, _) => StartPlayback();
        Closed += (_, _) =>
        {
            _closed = true;
            if (_renderingSubscribed) { CompositionTarget.Rendering -= Timer_Tick; _renderingSubscribed = false; }
            _player.Stop();
            _player.Dispose();
            // 2026-07-26要望対応(第三者提案): 閉じるボタン・中断キーどちらで終了した場合でも
            // Closedは共通で発火するため、ここで一括して表示位置を保存する(次回起動時に引き継ぐ)。
            if (_appSettings is not null)
            {
                _appSettings.PlaytestWindowLeft = Left;
                _appSettings.PlaytestWindowTop = Top;
                _appSettings.Save(AppPaths.SettingsFilePath);
            }
        };
    }

    private double HeaderDouble(string key, double fallback) =>
        _doc.Project.ExtraHeaders.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : fallback;

    /// <summary>0や負値も許容するヘッダー取得(2026-07-20、stepY/stepYR用。本家はstepYRに負値を許容する)</summary>
    private double HeaderDoubleAny(string key, double fallback) =>
        _doc.Project.ExtraHeaders.TryGetValue(key, out var v)
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    /// <summary>boost_data相当。指定フレーム(そのノート自身の到達フレーム)時点で有効な固定倍率を返す
    /// (本家getSpdByFrame準拠。直前のbreakpoint値を使用、該当が無ければ1倍)</summary>
    private double GetBoostFactor(double frame)
    {
        double result = 1.0;
        foreach (var b in _boostBreaks)
        {
            if (b.Frame > frame) break;
            result = b.Value;
        }
        return result;
    }

    /// <summary>speed_data相当。frame=0から指定フレームまでの累積移動距離(倍率1のノートが進む量)を返す。
    /// speed_dataは区分定数の倍率(未指定区間は1倍)なので、その積分は区分線形になり解析的に求まる
    /// (本家はフレーム毎の加算で同じ結果を得ている、13315-13331行目)。</summary>
    private double CumulativeSpeedDistance(double frame)
    {
        double dist = 0;
        double prevFrame = 0;
        double currentValue = 1.0;
        foreach (var b in _speedBreaks)
        {
            if (b.Frame >= frame) break;
            dist += currentValue * (b.Frame - prevFrame);
            prevFrame = b.Frame;
            currentValue = b.Value;
        }
        dist += currentValue * (frame - prevFrame);
        return dist;
    }

    private async void StartPlayback()
    {
        var path = _doc.Project.AudioFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "音楽ファイルが見つかりませんの。", "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        // 2026-07-26: 従来はOpen()(非同期・投げっぱなし)の直後にPositionを設定していたため、
        // デコード完了前は「読み込めたかどうか」を判定できなかった。ここではOpenAsyncを直接awaitし、
        // 完了を待ってから開始フレームの妥当性を検証する(目視テスト側で見つかった「再生開始ラインが
        // 音楽ファイルの長さを超えている場合、無音のまま再生位置・スクロールが固定される」不具合の
        // プレイテスト側対策)。
        await _player.OpenAsync(path);
        if (_closed) return;
        if (_player.Duration is not { } duration)
        {
            MessageBox.Show(this, "音楽ファイルの読み込みに失敗しましたわ。", "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        if (_startFrame / 60.0 >= duration.TotalSeconds)
        {
            MessageBox.Show(this,
                "再生開始ラインが音楽ファイルの長さを超えていますの。ラインをもっと手前へ置き直してくださいませ。",
                "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        // 2026-07-26g要望対応: 起動時ウェイトの仕様変更。旧来のTask.Delayによる一時停止ではなく、
        // 再生開始ラインからウェイト分だけ手前(PlayStartFrame)から連続再生を始める(リードイン方式)。
        // ノート側は_engine構築時にminFrame指定で再生開始ライン未満を除外済みのため、この区間には
        // 判定対象のノートが一切存在しない(存在しないものとして扱う、要望通り)。
        _player.Position = TimeSpan.FromSeconds(PlayStartFrame / 60.0);
        _player.SpeedRatio = _playbackSpeed; // 2026-07-23: 再生速度スライダー(ピッチ補正は行わない)
        _player.Volume = _volume; // 2026-07-21: UIの音量設定をプレイテストにも反映

        _player.Play();
        if (!_renderingSubscribed) { CompositionTarget.Rendering += Timer_Tick; _renderingSubscribed = true; }
    }

    /// <summary>やり直し中の多重実行防止(BackSpace連打・ウェイト待機中の再入対策、2026-07-26)。</summary>
    private bool _restarting;

    /// <summary>再生開始フレームからのやり直し(2026-07-26d要望対応、プレイテスト中のBackSpace)。
    /// 判定エンジン・オートプレイカーソル・ステップヒット演出・判定表示・押下中キー・ハンドクラップの
    /// カーソルを全てリセットし、音楽位置をPlayStartFrame(再生開始ラインからウェイト分だけ手前)へ戻す
    /// (2026-07-26g要望対応: 起動時ウェイトの仕様変更、StartPlayback参照)。</summary>
    private void RestartFromStartFrame()
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            _engine.Reset();
            Array.Clear(_autoPlayArrowCursor);
            Array.Clear(_autoPlayFreezeCursor);
            Array.Clear(_stepHitJudge);
            Array.Clear(_stepHitFramesRemaining);
            Array.Clear(_stepKeyDown);
            foreach (var s in _pressedKeys) s.Clear();
            _judgeText = "";
            _comboText = "";
            _freezeJudgeText = "";
            _freezeComboText = "";
            NotesClearedByKeypress = 0;

            _currentFrame = PlayStartFrame;
            // 2026-07-26f: Position設定時に_player内部でクラップの発音カーソルも自動的に巻き戻される
            // (NAudioBgmPlayer.RecomputeClapCursorLocked)ため、ここで別途リセットする必要は無い。
            _player.Position = TimeSpan.FromSeconds(PlayStartFrame / 60.0);
            _surface.InvalidateVisual();
            _player.Play(); // 既に再生中でも安全(再入可能)
        }
        finally
        {
            _restarting = false;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _currentFrame = _player.Position.TotalSeconds * 60.0;
        if (_autoPlay) AutoPlayAdvance();
        _engine.Advance(_currentFrame);

        // 2026-07-26f: ハンドクラップの発音判定・PCM重ね合わせは_player(NAudioBgmPlayer)自身の
        // レンダースレッド内で直接行われるため、ここでの処理は不要になった。
        // 2026-07-26: ステップゾーンヒットフラッシュのカウントダウン(本家のmovArrowループ内カウントダウンと
        // 同じく、フェード無しでcnt=0になった瞬間に非表示化する)。
        for (int i = 0; i < _stepHitFramesRemaining.Length; i++)
            if (_stepHitFramesRemaining[i] > 0) _stepHitFramesRemaining[i]--;
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// オートプレイ(2026-07-20): 全ノートを±0Fジャストで拾う。各レーンの矢印/フリーズをFrame昇順の
    /// カーソルで管理し、現在フレームに到達した未処理ノートへ「ノート自身のFrame」をそのまま
    /// PlaytestEngine.KeyDownへ渡す(diffが必ず0になるためイイ判定確定)。フリーズはKeyUpを呼ばず
    /// 握りっぱなしにする(Advance側の終点到達判定で自動的にキター確定する)。
    /// </summary>
    private void AutoPlayAdvance()
    {
        for (int lane = 0; lane < _template.Lanes.Count; lane++)
        {
            var arrows = _engine.ArrowsOf(lane);
            while (_autoPlayArrowCursor[lane] < arrows.Count && arrows[_autoPlayArrowCursor[lane]].Frame <= _currentFrame)
            {
                _engine.KeyDown(lane, arrows[_autoPlayArrowCursor[lane]].Frame);
                _autoPlayArrowCursor[lane]++;
            }

            var freezes = _engine.FreezesOf(lane);
            while (_autoPlayFreezeCursor[lane] < freezes.Count && freezes[_autoPlayFreezeCursor[lane]].StartFrame <= _currentFrame)
            {
                _engine.KeyDown(lane, freezes[_autoPlayFreezeCursor[lane]].StartFrame);
                _autoPlayFreezeCursor[lane]++;
            }
        }
    }

    // =====================================================================
    // キー入力
    // =====================================================================

    /// <summary>keyAssignの表示ラベル("←"、"S"、["E","R"]等)を物理キーへ変換して逆引き表を作る
    /// (2026-07-21: 実装はKeyLabelMapperへ抽出・共有化、キーボードモードと重複を排除)</summary>
    private void BuildKeyMap()
    {
        _keyToLane.Clear();
        foreach (var kv in KeyLabelMapper.BuildKeyMap(_template.Lanes.Count, lane => _template.Lanes[lane].KeyAssign, _unmappedLabels))
            _keyToLane[kv.Key] = kv.Value;
    }

    private void OnKeyDownInput(object sender, KeyEventArgs e)
    {
        // 2026-07-27要望対応: Ctrl+Pでその場中断。キーボードモード目視テスト中のCtrl+Enterと同様、
        // 中断したタイミングの最寄りグリッドへ再生開始ラインを設定してから終了する
        // (次回の目視テスト・プレイテストが続きから始まるように)。
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.P)
        {
            var engine = _doc.Project.CreateTimingEngine();
            long liveTick = (long)Math.Round(engine.FrameToTick(_currentFrame));
            long snappedTick = _doc.Snap.Snap(liveTick);
            _doc.CurrentTab.PlaybackStartFrame = engine.TickToFrame(snappedTick);
            Close();
            e.Handled = true;
            return;
        }

        // 2026-07-20: 中断キーは環境設定で選択したもの(Delete/Escape)のみ有効
        bool isQuitKey = (e.Key == Key.Delete && _quitKeyDelete)
            || (e.Key == Key.Escape && _quitKeyEscape);
        if (isQuitKey)
        {
            Close(); // 終了(呼び出し元がスクロール復帰を行う)
            e.Handled = true;
            return;
        }
        // 2026-07-26d: BackSpaceは中断キーから外し、「再生開始フレームからやり直し」専用にした
        if (e.Key == Key.Back)
        {
            if (!e.IsRepeat) RestartFromStartFrame();
            e.Handled = true;
            return;
        }
        if (_autoPlay) return; // オートプレイ中は手動入力を無視(2026-07-20、終了キーのみ上で処理済み)
        if (e.IsRepeat) { e.Handled = true; return; } // キーリピートは無視(ホールド継続)
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;

        bool laneWasPressed = _pressedKeys[lane].Count > 0;
        _pressedKeys[lane].Add(e.Key);
        if (!laneWasPressed)
        {
            _engine.KeyDown(lane, _currentFrame); // 同一レーン複数キーの同時押しは1押下扱い
            // 2026-08-02要望対応: 「空押し」ステップゾーン点灯。判定対象ノートの有無に関わらず、
            // キーが押されている間はステップゾーンを点灯させる(本家main_stepKeyDown相当)。
            _stepKeyDown[lane] = true;
        }
        e.Handled = true;
    }

    private void OnKeyUpInput(object sender, KeyEventArgs e)
    {
        if (_autoPlay) return; // 2026-07-20
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;
        _pressedKeys[lane].Remove(e.Key);
        if (_pressedKeys[lane].Count == 0)
        {
            _engine.KeyUp(lane, _currentFrame);
            _stepKeyDown[lane] = false; // 2026-08-02要望対応: 最後の1キーを離した瞬間に消灯
        }
        e.Handled = true;
    }

    // =====================================================================
    // 判定表示
    // =====================================================================

    /// <summary>統計情報(2026-07-26)向け: このプレイテストセッション中、オートプレイではなく
    /// 手動プレイ中に「打鍵によって」消えた(判定された)ノートの累計数。Uwan(通常ノートのタイムアウト
    /// ミス)・Iknai(フリーズのタイムアウトミス、キー未入力のまま判定枠を過ぎたケース)は
    /// 打鍵を伴わないため含めない(Iknaiは早すぎる誤押下でも起こり得る点は既知の簡略化)。
    /// App層(MainWindow.StartPlaytest)がShowDialog()後にこの値を読み、AppSettingsへ加算する。</summary>
    public int NotesClearedByKeypress { get; private set; }

    private void OnJudged(JudgeResult r)
    {
        if (!_autoPlay && r.Judge is not (PlayJudge.Uwan or PlayJudge.Iknai))
            NotesClearedByKeypress++;

        var (text, brush) = r.Judge switch
        {
            PlayJudge.Ii => ("(・∀・)ｲｲ!!", Brushes.Cyan),
            PlayJudge.Shakin => ("(`・ω・)ｼｬｷﾝ", Brushes.LightGreen),
            PlayJudge.Matari => ("( ´∀`)ﾏﾀｰﾘ", Brushes.Orange),
            PlayJudge.Shobon => ("(´・ω・`)ｼｮﾎﾞｰﾝ", Brushes.MediumPurple),
            PlayJudge.Uwan => ("ｳﾜｧﾝ!!(ﾉД`)", Brushes.Red),
            PlayJudge.Kita => ("(ﾟ∀ﾟ)ｷﾀ-!!", Brushes.Yellow),
            _ => ("ｲｸﾅｲ(・A・)", Brushes.Gray),
        };

        // 2026-07-26要望対応: ヒットフラッシュは「打鍵によって消去された」通常ノート判定(イイ〜ショボーン)
        // のみが対象。ウワァン(押さずに判定枠を通過したミス)は打鍵を伴わないため表示しない。
        // フリーズのキター/イクナイはこの演出を使わず、帯の消去/変色で判定を表す(従来通り)。
        if (r.Judge is not (PlayJudge.Kita or PlayJudge.Iknai or PlayJudge.Uwan))
        {
            // 2026-07-26要望対応: 常にステップゾーン上ではなく、実際に消去された座標(判定時点での
            // ノートの表示位置)へ表示する。プレイヤーがその上下のズレでタイミング誤差を知覚できるように
            // するための対応。ノートのフレームは「入力フレーム(=判定処理時点の_currentFrame) + DiffFrames」
            // (DiffFrames = ノート基準フレーム − 入力フレーム)で求まる。YOfと同じ計算式をここでも使う。
            var laneDef = _template.Lanes[r.Lane];
            bool flipped = (laneDef.ScrollDirection == "down") ^ _reverse;
            double dir = flipped ? -1 : 1;
            double stepY = flipped ? _stepYBottom : _stepYTop;
            double noteFrame = _currentFrame + r.DiffFrames;
            double boost = GetBoostFactor(noteFrame);
            double y = stepY + boost * (CumulativeSpeedDistance(noteFrame) - CumulativeSpeedDistance(_currentFrame)) * _baseScrollSpeed * dir;

            _stepHitJudge[r.Lane] = r.Judge;
            _stepHitY[r.Lane] = y;
            _stepHitFramesRemaining[r.Lane] = StepHitFrames;
        }

        // 2026-07-25: 本家準拠でフリーズ(キター/イクナイ)と通常ノート(イイ〜ウワァン)の判定文字・
        // コンボ表示を別系統に書き分ける(danoni_main.jsのcharaJ/comboJ ←→ charaFJ/comboFJ)。
        if (r.Judge is PlayJudge.Kita or PlayJudge.Iknai)
        {
            _freezeJudgeText = text;
            _freezeJudgeBrush = brush;
            _freezeComboText = r.Judge == PlayJudge.Kita ? $"{_engine.FreezeCombo} Combo!!" : "";
        }
        else
        {
            _judgeText = text;
            _judgeBrush = brush;
            // 本家準拠: イイ/シャキンで更新、マターリ/ダメージ系でコンボ表示は消える(内部値はエンジン側規則)
            _comboText = r.Judge is PlayJudge.Ii or PlayJudge.Shakin ? $"{_engine.Combo} Combo!!" : "";
        }
    }

    // =====================================================================
    // 描画サーフェス
    // =====================================================================

    /// <summary>プレイ画面本体の描画(OnRender直描き、ChartCanvasと同方式)</summary>
    private sealed class PlaySurface(PlaytestWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var o = owner;
            double w = o._playingWidth, h = o._playingHeight;
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

            // 2026-07-27要望対応: プレイテスト中の小節線表示(環境設定「プレイテスト」で切替、既定OFF)。
            // 最背面(ノート・判定文字より下)に薄く表示する。
            if (o._appSettings?.PlaytestShowMeasureLines == true)
                DrawMeasureLines(dc, o, w, h);

            // 2026-07-26: 判定文字(+コンボ)は最背面レイヤーへ変更(ユーザー要望)。矢印・フリーズより
            // 先に描く=それらの下に隠れる形になる。あわせて不透明度75%・文字サイズも縮小する。
            dc.PushOpacity(0.75);
            DrawCenteredText(dc, o._judgeText, o._judgeBrush, 18, h / 2 - 30, w);
            DrawCenteredText(dc, o._comboText, Brushes.White, 13, h / 2 + 8, w);
            DrawCenteredText(dc, o._freezeJudgeText, o._freezeJudgeBrush, 15, h / 2 + 40, w);
            DrawCenteredText(dc, o._freezeComboText, Brushes.White, 11, h / 2 + 66, w);
            dc.Pop();

            var tab = o._doc.CurrentTab;
            var project = o._doc.Project;
            var engine = o._doc.Project.CreateTimingEngine();

            for (int i = 0; i < o._template.Lanes.Count; i++)
            {
                var laneDef = o._template.Lanes[i];
                double cx = o._template.GetStepX(i, w) + ArrowSize / 2;
                // 2026-07-19: scrollDirectionの定義を「up=上方向スクロール(ステップゾーン上、ノーツは下から上へ)/
                // down=下方向スクロール(ステップゾーン下)」に統一(ユーザー確定)。従来は逆に解釈していた。
                bool flipped = (laneDef.ScrollDirection == "down") ^ o._reverse;
                // 2026-07-20: stepY/stepYRヘッダー反映済みの位置を使用(コンストラクタで算出)
                double stepY = flipped ? o._stepYBottom : o._stepYTop;
                double dir = flipped ? -1 : 1; // ノートの並ぶ向き(標準=ステップゾーンの下に未来のノート)

                var image = ChartCanvas.GetNoteImage(laneDef.NoteGraphic);
                var brush = ChartCanvas.LaneBrush(tab, project, laneDef.ColorGroup);
                var color = ((SolidColorBrush)brush).Color;
                var (frzNoteColor, frzBandColor) = ChartCanvas.FrzColors(tab, project, laneDef.ColorGroup, brush);
                // 2026-07-27要望対応(第三者報告): 色編集モード(ncolor_data)で個別指定された色が
                // プレイテストへ一切反映されていなかった不具合の修正。ChartCanvas.DrawNotesAndFreezes
                // と同じ「tickの厳密一致検索」方式で解決する(編集画面の見た目と一致させる)。
                var colorOverrides = tab.Lanes[i].ColorOverrides.ToDictionary(c => c.Tick);

                // ステップゾーン(2026-07-20: レーンの画像・回転角を使用。色は従来通りDimGrayのtint)
                DrawNote(dc, image, laneDef, cx, stepY, Colors.DimGray);

                // 2026-08-02要望対応: 「空押し」ステップゾーン点灯(本家stepDiv/main_stepKeyDown移植)。
                // 判定対象ノートの有無に関わらず、キーを押している間は白く点灯させる(離すと即消灯、
                // フェード無し)。ヒットフラッシュ(下記)とは別レイヤーで、両方同時に表示され得る。
                if (o._stepKeyDown[i])
                {
                    dc.PushOpacity(StepKeyDownOpacity);
                    DrawNote(dc, image, laneDef, cx, stepY, Colors.White);
                    dc.Pop();
                }

                // 2026-07-26: ステップゾーンヒットフラッシュ(本家stepHitTargetArrow移植、2026-07-26要望対応で
                // 表示座標を「実際に消去された座標」(OnJudged側で算出したo._stepHitY)へ変更)。
                if (o._stepHitFramesRemaining[i] > 0 && o._stepHitJudge[i] is { } hitJudge)
                {
                    dc.PushOpacity(StepHitOpacity);
                    DrawNote(dc, image, laneDef, cx, o._stepHitY[i], JudgeColor(hitJudge), ArrowSize + StepHitSizeAdd);
                    dc.Pop();
                }

                // 2026-07-21: speed_data(現在時刻に効くグローバル倍率、区分線形積分)×boost_data
                // (そのノート自身の到達フレームで決まる固定倍率)で実際の移動距離を求める
                double YOf(double frame, double boost) =>
                    stepY + boost * (o.CumulativeSpeedDistance(frame) - o.CumulativeSpeedDistance(o._currentFrame)) * o._baseScrollSpeed * dir;
                bool Visible(double y) => y > -ArrowSize && y < h + ArrowSize;

                // フリーズ(帯→端点の順に描画)
                foreach (var f in o._engine.FreezesOf(i))
                {
                    if (f.Result == PlayJudge.Kita) continue; // O.K.確定分は消去
                    double frzBoost = o.GetBoostFactor(f.StartFrame);
                    double y1 = YOf(f.StartFrame, frzBoost), y2 = YOf(f.EndFrame, frzBoost);
                    // ホールド中は始点がステップゾーンに吸着し、帯が短くなっていく
                    if (f.Started && f.Result is null) y1 = stepY;
                    if (!Visible(y1) && !Visible(y2) && Math.Sign(y1 - h / 2) == Math.Sign(y2 - h / 2)) continue;

                    colorOverrides.TryGetValue(f.StartTick, out var fOver);
                    string? edgeColorCode = fOver?.Color;
                    string? bandColorCode = fOver?.BandColor;
                    // 2026-07-30要望対応: 即時適用(AllFlag)の簡易ライブシミュレーション。自分より前の
                    // tickで既に発火済みの即時適用があれば、その色を優先する(近似ルール、詳細はヘルパー参照)。
                    edgeColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                        tab.Lanes[i].ColorOverrides, f.StartTick, o._currentFrame, e => e.Color, engine.TickToFrame, edgeColorCode);
                    bandColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                        tab.Lanes[i].ColorOverrides, f.StartTick, o._currentFrame, e => e.BandColor, engine.TickToFrame, bandColorCode);
                    var edgeColor = edgeColorCode is { } ec ? ChartCanvas.ParseDisplayColor(ec, frzNoteColor) : frzNoteColor;
                    var bandColor = bandColorCode is { } bc ? ChartCanvas.ParseDisplayColor(bc, frzBandColor) : frzBandColor;

                    var bandBrush = new SolidColorBrush(bandColor) { Opacity = 0.5 };
                    bandBrush.Freeze();
                    dc.DrawRectangle(bandBrush, null,
                        new Rect(cx - ArrowSize / 4, Math.Min(y1, y2), ArrowSize / 2, Math.Abs(y2 - y1)));
                    DrawNote(dc, image, laneDef, cx, y1, edgeColor);
                    DrawNote(dc, image, laneDef, cx, y2, edgeColor);
                }

                // 矢印(判定済みは消去。ただし見逃しウワァン分はそのまま流れていく)
                foreach (var a in o._engine.ArrowsOf(i))
                {
                    if (a.Result is { } r && r != PlayJudge.Uwan) continue;
                    double y = YOf(a.Frame, o.GetBoostFactor(a.Frame));
                    if (!Visible(y)) continue;
                    colorOverrides.TryGetValue(a.Tick, out var nOver);
                    string? noteColorCode = ChartCanvas.ResolveImmediateAppliedColor(
                        tab.Lanes[i].ColorOverrides, a.Tick, o._currentFrame, e => e.Color, engine.TickToFrame, nOver?.Color);
                    var noteColor = noteColorCode is { } nc ? ChartCanvas.ParseDisplayColor(nc, color) : color;
                    DrawNote(dc, image, laneDef, cx, y, noteColor);
                }
            }
        }

        private static void DrawNote(DrawingContext dc, System.Windows.Media.Imaging.BitmapImage? image, LaneDef laneDef, double cx, double y, Color color, double size = ArrowSize)
        {
            if (image is not null)
            {
                ChartCanvas.DrawNoteImage(dc, image, laneDef, cx, y, size, color);
            }
            else
            {
                var b = new SolidColorBrush(color);
                b.Freeze();
                dc.DrawRectangle(b, new Pen(Brushes.Black, 0.5),
                    new Rect(cx - size / 2, y - size / 2, size, size));
            }
        }

        /// <summary>ステップゾーンヒットフラッシュの判定色(2026-07-26、画面中央の判定文字と同系色に統一)。</summary>
        private static Color JudgeColor(PlayJudge judge) => judge switch
        {
            PlayJudge.Ii => Colors.Cyan,
            PlayJudge.Shakin => Colors.LightGreen,
            PlayJudge.Matari => Colors.Orange,
            PlayJudge.Shobon => Colors.MediumPurple,
            PlayJudge.Uwan => Colors.Red,
            _ => Colors.White,
        };

        private static void DrawCenteredText(DrawingContext dc, string text, Brush brush, double size, double y, double width)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Meiryo UI"), size, brush, 1.25);
            dc.DrawText(ft, new Point((width - ft.Width) / 2, y));
        }

        private static readonly Pen MeasureLinePen = CreateFrozenPen(Color.FromArgb(0x60, 0xCC, 0xCC, 0xCC), 1.0);
        private static readonly Brush MeasureLineTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xCC, 0xCC, 0xCC)));

        private static Pen CreateFrozenPen(Color color, double thickness)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, thickness);
            pen.Freeze();
            return pen;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        /// <summary>プレイテスト中の小節線・小節番号表示(2026-07-27要望対応)。譜面ビューの
        /// DrawTimeInfoLane/DrawGridAndMeasureLinesと同じ「拍子イベント列に沿って小節先頭tickを
        /// 順に辿る」ロジックを、frameベースの画面座標(YOf相当)へ適用したもの。
        /// 2026-08-01修正: 小節線はレーンに紐付かない全体基準の表示であるため、基準となる
        /// ScrollDirectionは「先頭レーンの値」ではなく「全レーン中で最も多いScrollDirection」を使う
        /// (旧実装は先頭レーンをそのまま代表にしていたため、上下でスクロール方向が混在する
        /// 折返しキー種等で小節線がReverseに正しく追随して見えないことがあった)。
        /// 現在フレームの1小節前から走査を始め、画面外(dirが向かう側)へ完全に
        /// 出た時点で打ち切る(安全弁としてmaxScan回で強制終了)。</summary>
        private static void DrawMeasureLines(DrawingContext dc, PlaytestWindow o, double w, double h)
        {
            if (o._template.Lanes.Count == 0) return;
            string majorityDirection = o._template.Lanes
                .GroupBy(l => l.ScrollDirection)
                .OrderByDescending(g => g.Count())
                .First().Key;
            bool flipped = (majorityDirection == "down") ^ o._reverse;
            double stepY = flipped ? o._stepYBottom : o._stepYTop;
            double dir = flipped ? -1 : 1;

            // 2026-08-01修正: ノート側のYOf(654行目付近)はboost_dataの倍率を反映しているのに対し、
            // こちらは反映していなかったため、boost_dataがある譜面で小節線がノートとずれるバグを修正。
            double YOf(double frame) =>
                stepY + o.GetBoostFactor(frame) * (o.CumulativeSpeedDistance(frame) - o.CumulativeSpeedDistance(o._currentFrame)) * o._baseScrollSpeed * dir;

            var engine = o._doc.Project.CreateTimingEngine();
            long currentTick = (long)Math.Round(engine.FrameToTick(o._currentFrame));
            var (currentMeasure, _) = engine.TickToMeasurePosition(currentTick);

            const double margin = 40;
            const int maxScan = 500;
            int startMeasure = Math.Max(0, currentMeasure - 1);
            for (int i = 0; i < maxScan; i++)
            {
                int measure = startMeasure + i;
                long tick = engine.MeasureStartTick(measure);
                double frame = engine.TickToFrame(tick);
                double y = YOf(frame);

                if (y >= -margin && y <= h + margin)
                {
                    dc.DrawLine(MeasureLinePen, new Point(0, y), new Point(w, y));
                    var ft = new FormattedText($"#{measure}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Meiryo UI"), 11, MeasureLineTextBrush, 1.25);
                    dc.DrawText(ft, new Point(4, y - ft.Height - 1));
                }

                if (dir > 0 && y > h + margin) break;
                if (dir < 0 && y < -margin) break;
            }
        }
    }
}
