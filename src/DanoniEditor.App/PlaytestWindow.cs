using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Playtest;
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
    private double _currentFrame;

    private readonly Dictionary<Key, int> _keyToLane = [];
    private readonly HashSet<Key>[] _pressedKeys;
    private readonly List<string> _unmappedLabels = [];

    private string _judgeText = "";
    private Brush _judgeBrush = Brushes.White;
    private string _comboText = "";

    private readonly PlaySurface _surface;

    public PlaytestWindow(EditorDocument doc, bool reverse, double hiSpeed, double offsetFrames, double startFrame, double windowScale = 1.0)
    {
        _doc = doc;
        _template = doc.CurrentTemplate;
        _reverse = reverse;
        _hiSpeed = Math.Max(0.25, hiSpeed);
        _startFrame = startFrame;
        // 幅: playingWidthヘッダー指定 > 本家autoSpread準拠のキー種別自動決定(2026-07-17h)
        _playingWidth = HeaderDouble("playingWidth", AutoSpreadWidth(_template.KeyTypeId));
        _playingHeight = HeaderDouble("playingHeight", 500);
        double scale = Math.Clamp(windowScale, 0.5, 3.0);

        _engine = new PlaytestEngine(
            doc.CurrentTab,
            doc.Project.CreateTimingEngine(),
            doc.Project.FrzAttempt,
            offsetFrames);
        _engine.Judged += OnJudged;

        _pressedKeys = new HashSet<Key>[_template.Lanes.Count];
        for (int i = 0; i < _pressedKeys.Length; i++) _pressedKeys[i] = [];
        BuildKeyMap();

        Title = $"プレイテスト - {doc.Project.ProjectName} [{doc.CurrentTab.DifficultyName}]";
        Background = Brushes.Black;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel();
        var infoText = $"Delete / BackSpace / Esc で終了  |  HS x{_hiSpeed:0.##}  |  Reverse {(_reverse ? "ON" : "OFF")}  |  {_playingWidth:0}x{_playingHeight:0} x{scale:0.##}"
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
        _player.Play();
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _currentFrame = _player.Position.TotalSeconds * 60.0;
        _engine.Advance(_currentFrame);
        _surface.InvalidateVisual();
    }

    // =====================================================================
    // キー入力
    // =====================================================================

    /// <summary>keyAssignの表示ラベル("←"、"S"、["E","R"]等)を物理キーへ変換して逆引き表を作る</summary>
    private void BuildKeyMap()
    {
        for (int lane = 0; lane < _template.Lanes.Count; lane++)
        {
            foreach (var label in _template.Lanes[lane].KeyAssign)
            {
                var keys = KeysForLabel(label);
                if (keys.Count == 0)
                {
                    _unmappedLabels.Add($"{label}(レーン{lane + 1})");
                    continue;
                }
                foreach (var k in keys) _keyToLane[k] = lane;
            }
        }
    }

    private static IReadOnlyList<Key> KeysForLabel(string label) => label switch
    {
        "←" => [Key.Left],
        "↓" => [Key.Down],
        "↑" => [Key.Up],
        "→" => [Key.Right],
        "<" => [Key.OemComma],
        ">" => [Key.OemPeriod],
        ";" => [Key.OemSemicolon],
        ":" => [Key.OemQuotes],
        "@" => [Key.OemOpenBrackets, Key.Oem3], // JIS配列の@(本家keycode BracketLeft由来)
        "Space" or "SP" or "␣" or " " => [Key.Space],
        "Enter" => [Key.Enter],
        _ when label.Length == 1 && label[0] is >= 'A' and <= 'Z' => [(Key)((int)Key.A + (label[0] - 'A'))],
        _ when label.Length == 1 && label[0] is >= 'a' and <= 'z' => [(Key)((int)Key.A + (char.ToUpperInvariant(label[0]) - 'A'))],
        _ when label.Length == 1 && label[0] is >= '0' and <= '9' =>
            [(Key)((int)Key.D0 + (label[0] - '0')), (Key)((int)Key.NumPad0 + (label[0] - '0'))],
        _ => [],
    };

    private void OnKeyDownInput(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back or Key.Escape)
        {
            Close(); // 終了(呼び出し元がスクロール復帰を行う)
            e.Handled = true;
            return;
        }
        if (e.IsRepeat) { e.Handled = true; return; } // キーリピートは無視(ホールド継続)
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;

        bool laneWasPressed = _pressedKeys[lane].Count > 0;
        _pressedKeys[lane].Add(e.Key);
        if (!laneWasPressed) _engine.KeyDown(lane, _currentFrame); // 同一レーン複数キーの同時押しは1押下扱い
        e.Handled = true;
    }

    private void OnKeyUpInput(object sender, KeyEventArgs e)
    {
        if (!_keyToLane.TryGetValue(e.Key, out int lane)) return;
        _pressedKeys[lane].Remove(e.Key);
        if (_pressedKeys[lane].Count == 0) _engine.KeyUp(lane, _currentFrame);
        e.Handled = true;
    }

    // =====================================================================
    // 判定表示
    // =====================================================================

    private void OnJudged(JudgeResult r)
    {
        (_judgeText, _judgeBrush) = r.Judge switch
        {
            PlayJudge.Ii => ("(・∀・)ｲｲ!!", Brushes.Cyan),
            PlayJudge.Shakin => ("(`・ω・)ｼｬｷﾝ", Brushes.LightGreen),
            PlayJudge.Matari => ("( ´∀`)ﾏﾀｰﾘ", Brushes.Orange),
            PlayJudge.Shobon => ("(´・ω・`)ｼｮﾎﾞｰﾝ", Brushes.MediumPurple),
            PlayJudge.Uwan => ("ｳﾜｧﾝ!!(ﾉД`)", Brushes.Red),
            PlayJudge.Kita => ("(ﾟ∀ﾟ)ｷﾀ-!!", Brushes.Yellow),
            _ => ("ｲｸﾅｲ(・A・)", Brushes.Gray),
        };
        // 本家準拠: イイ/シャキンで更新、マターリ/ダメージ系でコンボ表示は消える(内部値はエンジン側規則)
        _comboText = r.Judge is PlayJudge.Ii or PlayJudge.Shakin ? $"{_engine.Combo} Combo!!" : "";
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
                double stepY = flipped ? h - StepY - ArrowSize / 2 : StepY + ArrowSize / 2;
                double dir = flipped ? -1 : 1; // ノートの並ぶ向き(標準=ステップゾーンの下に未来のノート)

                var image = ChartCanvas.GetNoteImage(laneDef.NoteGraphic);
                var brush = ChartCanvas.LaneBrush(tab, project, laneDef.ColorGroup);
                var color = ((SolidColorBrush)brush).Color;
                var (frzNoteColor, frzBandColor) = ChartCanvas.FrzColors(tab, project, laneDef.ColorGroup, brush);

                // ステップゾーン(枠のみ)
                dc.DrawRectangle(null, new Pen(Brushes.DimGray, 2),
                    new Rect(cx - ArrowSize / 2, stepY - ArrowSize / 2, ArrowSize, ArrowSize));

                double YOf(double frame) => stepY + (frame - o._currentFrame) * o._hiSpeed * dir;
                bool Visible(double y) => y > -ArrowSize && y < h + ArrowSize;

                // フリーズ(帯→端点の順に描画)
                foreach (var f in o._engine.FreezesOf(i))
                {
                    if (f.Result == PlayJudge.Kita) continue; // O.K.確定分は消去
                    double y1 = YOf(f.StartFrame), y2 = YOf(f.EndFrame);
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
                    double y = YOf(a.Frame);
                    if (!Visible(y)) continue;
                    DrawNote(dc, image, laneDef, cx, y, color);
                }
            }

            // 判定文字(画面中央)+コンボ(その下)
            DrawCenteredText(dc, o._judgeText, o._judgeBrush, 28, h / 2 - 30, w);
            DrawCenteredText(dc, o._comboText, Brushes.White, 20, h / 2 + 8, w);
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
