using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// 解決する(2026-08-03)。dos.txtのplayingWidthヘッダーが明示されている場合はこの値より常に
    /// そちらが優先される(呼び出し元のHeaderDouble経由、仕様書12.2)。ヘッダー未指定時のみここへ来る。</summary>
    internal static double ResolveWindowWidthFallback(AppSettings settings, string currentTabKeyTypeId)
    {
        if (settings.PlaytestWindowWidthMode == "px")
            return Math.Max(1, settings.PlaytestWindowWidthPx);

        // "keyType"モード: 指定キー種が未設定なら、従来通り実際に開いているタブのキー種を使う
        var keyTypeId = string.IsNullOrEmpty(settings.PlaytestWindowWidthKeyType)
            ? currentTabKeyTypeId
            : settings.PlaytestWindowWidthKeyType;
        return AutoSpreadWidth(keyTypeId);
    }

    private readonly EditorDocument _doc;
    private readonly KeyTemplate _template;
    private readonly PlaytestEngine _engine;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool _reverse;
    private readonly double _hiSpeed;
    private readonly double _startFrame;
    private readonly double _playingWidth;
    private readonly double _playingHeight;
    private readonly bool _autoPlay;
    private readonly bool _quitKeyDelete;
    private readonly bool _quitKeyBackSpace;
    private readonly bool _quitKeyEscape;
    private readonly double _playbackSpeed;
    private readonly double _volume;
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

    private readonly PlaySurface _surface;

    public PlaytestWindow(EditorDocument doc, bool reverse, double hiSpeed, double offsetFrames, double startFrame, double windowScale = 1.0, bool autoPlay = false,
        bool quitKeyDelete = true, bool quitKeyBackSpace = true, bool quitKeyEscape = true, double playbackSpeed = 1.0, double volume = 1.0,
        AppSettings? appSettings = null)
    {
        _doc = doc;
        _template = doc.CurrentTemplate;
        _reverse = reverse;
        _hiSpeed = Math.Max(0.25, hiSpeed);
        _startFrame = startFrame;
        _autoPlay = autoPlay;
        _quitKeyDelete = quitKeyDelete;
        _quitKeyBackSpace = quitKeyBackSpace;
        _quitKeyEscape = quitKeyEscape;
        _playbackSpeed = Math.Clamp(playbackSpeed, 0.1, 2.0); // 2026-07-23: 再生速度スライダー
        _volume = Math.Clamp(volume, 0.0, 1.0); // 2026-07-21: UIの音量設定をプレイテストにも反映
        // 幅: playingWidthヘッダー指定 > 環境設定「プレイテスト」のウィンドウ幅設定(2026-08-03、
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
        _stepYTop = stepYHeader + ArrowSize / 2;
        _stepYBottom = _playingHeight + stepYRHeader - stepYHeader - ArrowSize / 2;

        double scale = Math.Clamp(windowScale, 0.5, 3.0);

        var timing = doc.Project.CreateTimingEngine();

        // 2026-07-21: speed_data/boost_data(tick単位)をフレーム基準のbreakpointリストに変換
        _speedBreaks = doc.CurrentTab.SpeedEvents
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + offsetFrames, e.Value))
            .OrderBy(b => b.Frame)
            .ToList();
        _boostBreaks = doc.CurrentTab.BoostEvents
            .Select(e => (Frame: timing.TickToFrame(e.Tick) + offsetFrames, e.Value))
            .OrderBy(b => b.Frame)
            .ToList();

        _engine = new PlaytestEngine(
            doc.CurrentTab,
            timing,
            doc.Project.FrzAttempt,
            offsetFrames);
        _engine.Judged += OnJudged;

        _pressedKeys = new HashSet<Key>[_template.Lanes.Count];
        for (int i = 0; i < _pressedKeys.Length; i++) _pressedKeys[i] = [];
        _autoPlayArrowCursor = new int[_template.Lanes.Count];
        _autoPlayFreezeCursor = new int[_template.Lanes.Count];
        BuildKeyMap();

        Title = $"プレイテスト - {doc.Project.ProjectName} [{doc.CurrentTab.DifficultyName}]";
        Background = Brushes.Black;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();
        var quitKeyNames = new List<string>();
        if (_quitKeyDelete) quitKeyNames.Add("Delete");
        if (_quitKeyBackSpace) quitKeyNames.Add("BackSpace");
        if (_quitKeyEscape) quitKeyNames.Add("Esc");
        var quitKeyText = quitKeyNames.Count > 0 ? $"{string.Join(" / ", quitKeyNames)} で終了" : "終了キー未設定";

        var speedBoostTags = new List<string>();
        if (_speedBreaks.Count > 0) speedBoostTags.Add("Speed");
        if (_boostBreaks.Count > 0) speedBoostTags.Add("Boost");

        var infoText = $"{quitKeyText}  |  HS x{_hiSpeed:0.##}"
            + (Math.Abs(_baseSpeed - 1.0) > 0.001 ? $" (Δv {_baseSpeed * 100:0}%)" : "")
            + (speedBoostTags.Count > 0 ? $" [{string.Join("+", speedBoostTags)}]" : "")
            + $"  |  Reverse {(_reverse ? "ON" : "OFF")}  |  AutoPlay {(_autoPlay ? "ON" : "OFF")}  |  {_playingWidth:0}x{_playingHeight:0} x{scale:0.##}"
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
        _timer.Tick += Timer_Tick;
        ContentRendered += (_, _) => StartPlayback();
        Closed += (_, _) => { _timer.Stop(); _player.Stop(); _player.Close(); };
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

    private void StartPlayback()
    {
        var path = _doc.Project.AudioFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "音楽ファイルが見つかりませんの。", "プレイテスト", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _player.Open(new Uri(path, UriKind.Absolute));
        _player.Position = TimeSpan.FromSeconds(_startFrame / 60.0);
        _player.SpeedRatio = _playbackSpeed; // 2026-07-23: 再生速度スライダー(ピッチ補正は行わない)
        _player.Volume = _volume; // 2026-07-21: UIの音量設定をプレイテストにも反映
        _player.Play();
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _currentFrame = _player.Position.TotalSeconds * 60.0;
        if (_autoPlay) AutoPlayAdvance();
        _engine.Advance(_currentFrame);
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
        // 2026-07-20: 中断キーは環境設定で選択したもの(Delete/BackSpace/Escape)のみ有効
        bool isQuitKey = (e.Key == Key.Delete && _quitKeyDelete)
            || (e.Key == Key.Back && _quitKeyBackSpace)
            || (e.Key == Key.Escape && _quitKeyEscape);
        if (isQuitKey)
        {
            Close(); // 終了(呼び出し元がスクロール復帰を行う)
            e.Handled = true;
            return;
        }
        if (_autoPlay) return; // オートプレイ中は手動入力を無視(2026-07-20、終了キーのみ上で処理済み)
        if (e.IsRepeat) { e.Handled = true; return; } // キーリピートは無視(ホールド継続)
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;

        bool laneWasPressed = _pressedKeys[lane].Count > 0;
        _pressedKeys[lane].Add(e.Key);
        if (!laneWasPressed) _engine.KeyDown(lane, _currentFrame); // 同一レーン複数キーの同時押しは1押下扱い
        e.Handled = true;
    }

    private void OnKeyUpInput(object sender, KeyEventArgs e)
    {
        if (_autoPlay) return; // 2026-07-20
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;
        _pressedKeys[lane].Remove(e.Key);
        if (_pressedKeys[lane].Count == 0) _engine.KeyUp(lane, _currentFrame);
        e.Handled = true;
    }

    // =====================================================================
    // 判定表示
    // =====================================================================

    /// <summary>統計情報(2026-08-05)向け: このプレイテストセッション中、オートプレイではなく
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

            var tab = o._doc.CurrentTab;
            var project = o._doc.Project;

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

                // ステップゾーン(2026-07-20: レーンの画像・回転角を使用。色は従来通りDimGrayのtint)
                DrawNote(dc, image, laneDef, cx, stepY, Colors.DimGray);

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

                    var bandBrush = new SolidColorBrush(frzBandColor) { Opacity = 0.5 };
                    bandBrush.Freeze();
                    dc.DrawRectangle(bandBrush, null,
                        new Rect(cx - ArrowSize / 4, Math.Min(y1, y2), ArrowSize / 2, Math.Abs(y2 - y1)));
                    DrawNote(dc, image, laneDef, cx, y1, frzNoteColor);
                    DrawNote(dc, image, laneDef, cx, y2, frzNoteColor);
                }

                // 矢印(判定済みは消去。ただし見逃しウワァン分はそのまま流れていく)
                foreach (var a in o._engine.ArrowsOf(i))
                {
                    if (a.Result is { } r && r != PlayJudge.Uwan) continue;
                    double y = YOf(a.Frame, o.GetBoostFactor(a.Frame));
                    if (!Visible(y)) continue;
                    DrawNote(dc, image, laneDef, cx, y, color);
                }
            }

            // 判定文字(画面中央)+コンボ(その下)。2026-07-25: 本家準拠でフリーズ用の判定文字・
            // コンボはさらにその下へ別枠として常時表示する(矢印側の表示を上書きしない)。
            DrawCenteredText(dc, o._judgeText, o._judgeBrush, 28, h / 2 - 30, w);
            DrawCenteredText(dc, o._comboText, Brushes.White, 20, h / 2 + 8, w);
            DrawCenteredText(dc, o._freezeJudgeText, o._freezeJudgeBrush, 22, h / 2 + 40, w);
            DrawCenteredText(dc, o._freezeComboText, Brushes.White, 16, h / 2 + 66, w);
        }

        private static void DrawNote(DrawingContext dc, System.Windows.Media.Imaging.BitmapImage? image, LaneDef laneDef, double cx, double y, Color color)
        {
            if (image is not null)
            {
                ChartCanvas.DrawNoteImage(dc, image, laneDef, cx, y, ArrowSize, color);
            }
            else
            {
                var b = new SolidColorBrush(color);
                b.Freeze();
                dc.DrawRectangle(b, new Pen(Brushes.Black, 0.5),
                    new Rect(cx - ArrowSize / 2, y - ArrowSize / 2, ArrowSize, ArrowSize));
            }
        }

        private static void DrawCenteredText(DrawingContext dc, string text, Brush brush, double size, double y, double width)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Meiryo UI"), size, brush, 1.25);
            dc.DrawText(ft, new Point((width - ft.Width) / 2, y));
        }
    }
}
