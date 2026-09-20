using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Audio;
using DanoniEditor.Core.Export;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;
using DanoniEditor.App.Collab;
using DanoniEditor.App.Plugins;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App;

/// <summary>
/// 譜面編集エリアの描画本体(仕様書6.1/6.6、07-16手記§6の設計ロック分)。
/// ItemsControl/ObservableCollectionでノート1つ1つをUIElement化せず、OnRenderで直接DrawingContextに
/// 描く。可視範囲(ViewportRect、MainWindow側がScrollViewer.ScrollChangedで更新)の外は計算すらしない。
/// 画像アセット(./img)は現状リポジトリに存在しないため、ベクター描画のみ(仕様書のフォールバック経路)。
/// </summary>
public sealed class ChartCanvas : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Brush GridLineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush BeatLineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush MeasureLineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ColumnSepBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
    // 2026-07-17: 「どこまでがノートレーンで、どこからが情報レーン(マーカー/小節/speed/boost/BPM)か
    // わかりづらい」との要望対応。半透明にしているのは、将来ここに波形を表示する予定があるため
    // (波形を透けさせて重ねられるように)。
    private static readonly Brush NoteLaneBackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush InfoLaneBackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)));
    private static readonly Brush SpeedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)));
    private static readonly Brush BoostBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x40)));
    private static readonly Brush BpmBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x50, 0xC0, 0x60)));
    private static readonly Brush TimeSigBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xE0)));
    private static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)));
    // 2026-07-23(TBD 4): 歌詞レーン(WordLanes)のタグ色。制御行([fadein]等)は歌詞本文と区別しやすい紫系にする。
    private static readonly Brush WordLyricsBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x50)));
    private static readonly Brush WordControlBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0x70, 0xE0)));
    private static readonly Brush LaneLabelBackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x10))); // 2026-07-22: レーンラベル背景
    private static readonly Brush KeyboardInputKeyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7))); // 2026-07-22: キーボードモード入力キー(2行目)の色分け
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xEE, 0x00)));
    private static readonly Pen SelectionPen = Freeze(new Pen(SelectionBrush, 2));
    private static readonly Typeface Typeface = new("Segoe UI");

    private static readonly Dictionary<string, Brush> BrushCache = [];

    // --- ノート画像キャッシュ(仕様書2.1/4.3): ./img/{arrow,c,giko,iyo,monar,morara,onigiri}.png ---
    // 画像が無い場合(現状リポジトリに未同梱)はnullを返し、呼び出し側がベクター描画にフォールバックする。
    private static readonly string? ImgDir = AppPaths.FindAssetDir("img");
    private static readonly Dictionary<string, BitmapImage?> ImageCache = [];

    /// <summary>SVGラスタライズ時の出力解像度(px)。ノート画像は最大でもこの程度の表示サイズしか
    /// 使わないため、これより大きくしても画質向上の意味が薄い一方でメモリ・処理コストが増える。</summary>
    private const int SvgRasterSize = 256;

    internal static BitmapImage? GetNoteImage(string noteGraphic) // 2026-07-17g: PlaytestWindowと共用のためinternal化
    {
        if (ImageCache.TryGetValue(noteGraphic, out var cached)) return cached;
        BitmapImage? image = null;
        if (ImgDir is not null)
        {
            var pngPath = Path.Combine(ImgDir, $"{noteGraphic}.png");
            if (File.Exists(pngPath))
            {
                try
                {
                    image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(pngPath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                }
                catch { image = null; } // 壊れた画像等はベクターへフォールバック
            }
            else
            {
                // 2026-07-26: pngが無い場合、同名のsvgがあればラスタライズして使う(要望対応)。
                // pngが優先(既存素材との互換性維持)、svgはpng不在時のみのフォールバック。
                var svgPath = Path.Combine(ImgDir, $"{noteGraphic}.svg");
                if (File.Exists(svgPath)) image = TryRasterizeSvg(svgPath);
            }
        }
        ImageCache[noteGraphic] = image;
        return image;
    }

    /// <summary>SVGファイルをビットマップへラスタライズし、以降の着色処理(GetTintedNoteImage)や
    /// 描画処理(DrawNoteImage)が既存のpng読込と全く同じ扱いをできるよう、BitmapImageとして返す
    /// (Svgライブラリ(System.Drawing.Bitmap)でレンダリングしたのち、PNGエンコードしてメモリ上から
    /// 通常のBitmapImage読込と同じ経路に載せる)。透過背景を維持する。</summary>
    private static BitmapImage? TryRasterizeSvg(string svgPath)
    {
        try
        {
            var svgDoc = Svg.SvgDocument.Open(svgPath);
            using var bitmap = svgDoc.Draw(SvgRasterSize, SvgRasterSize);
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; } // 壊れたsvg等はベクターへフォールバック
    }

    // --- ノート画像の着色(2026-07-16k): setColor/frzColorをノート画像にも反映する要望対応。
    // 素材はグレースケール(輝度)ベースの線画のため、輝度を明るさとして使い、対象色を乗算合成する
    // (danoniplus本体がグレースケール素材+色乗算で着色するのと同じ考え方)。1回計算した結果は
    // (画像, 色)の組み合わせごとにキャッシュし、毎フレーム再計算しない。
    private static readonly Dictionary<(string ImgKey, uint ColorArgb), BitmapSource> TintCache = [];

    private static BitmapSource GetTintedNoteImage(BitmapImage source, Color tint)
    {
        string imgKey = source.UriSource?.ToString() ?? source.GetHashCode().ToString();
        uint colorKey = (uint)((tint.A << 24) | (tint.R << 16) | (tint.G << 8) | tint.B);
        var cacheKey = (imgKey, colorKey);
        if (TintCache.TryGetValue(cacheKey, out var cached)) return cached;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        // 2026-07-16l: 当初は元画像の輝度を乗算する方式にしていたが、実際の素材(黒地+アルファで
        // 輪郭を作るタイプ)では輝度がほぼ0のため色が乗らず「着色されない」不具合になっていた。
        // アルファチャンネルだけを形状マスクとして使い、RGBは対象色で塗りつぶす方式に変更する
        // (素材の下地色がどうであっても確実に対象色が反映される)。
        for (int p = 0; p < pixels.Length; p += 4)
        {
            byte a = pixels[p + 3];
            pixels[p] = tint.B;
            pixels[p + 1] = tint.G;
            pixels[p + 2] = tint.R;
            pixels[p + 3] = a;
        }

        var bmp = BitmapSource.Create(width, height, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        TintCache[cacheKey] = bmp;
        return bmp;
    }

    private static Brush BrushOf(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var b)) return b;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            b = Freeze(new SolidColorBrush(color));
        }
        catch
        {
            b = Brushes.Magenta; // 不正な色指定は目立つ色でフォールバック
        }
        BrushCache[hex] = b;
        return b;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // =====================================================================
    // 依存プロパティ
    // =====================================================================

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(EditorDocument), typeof(ChartCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnDocumentChanged));

    public EditorDocument? Document
    {
        get => (EditorDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (ChartCanvas)d;
        if (e.OldValue is EditorDocument old) old.Changed -= canvas.InvalidateAll;
        if (e.NewValue is EditorDocument neu) neu.Changed += canvas.InvalidateAll;
        canvas.InvalidateAll();
    }

    /// <summary>対になるレーンラベルヘッダー領域(2026-08-08、別領域化。MainWindow.xaml.cs側の
    /// InitializeComponent直後に設定される)。ズーム(Alt+ホイール、ChartLayout.ZoomScale)は
    /// ラベルバーの文字サイズ・高さにも影響するため、このCanvas自身の再計測・再描画に合わせて
    /// ヘッダー側も同時に無効化する(InvalidateAll参照)。</summary>
    public LaneHeaderBar? HeaderBar { get; set; }

    private void InvalidateAll()
    {
        InvalidateMeasure();
        InvalidateVisual();
        HeaderBar?.InvalidateMeasure();
        HeaderBar?.InvalidateVisual();
    }

    /// <summary>現在スクロールで見えている範囲(canvasローカル座標、px)。MainWindowがScrollChangedで更新する。</summary>
    public Rect ViewportRect { get; set; } = Rect.Empty;

    /// <summary>右パネル/プレイテストと同様、プラグインが独自のオーバーレイ描画を差し込むための
    /// 登録先(2026-07-26、プラグイン対応の土台)。MainWindowが起動時に読み込んだプラグイン一覧を
    /// ここへセットする。空リストなら通常通り何も描かれない。</summary>
    public IReadOnlyList<IChartOverlayPlugin> OverlayPlugins { get; set; } = [];

    /// <summary>音楽再生中のカレントフレーム位置(tick、MainWindowが再生タイマーで更新)。nullなら非表示</summary>
    public double? PlaybackTick { get; set; }

    /// <summary>
    /// ノートレーンにノート画像を表示するか(仕様書14章のアプリ設定、2026-07-16b追加)。
    /// falseの場合、ノートは画像の代わりに強調グリッド(横棒)で表示される。
    /// フリーズ(始点/終点/胴体)の見た目はこのフラグの影響を受けない(元々ベクター描画のため)。
    /// </summary>
    public bool ShowNoteImages { get; set; } = true;

    /// <summary>ノートレーンに強調グリッド(グリッド位置を示す横棒)を表示するか。ShowNoteImagesとは
    /// 独立にON/OFFできる(2026-07-16j: 従来は「ShowNoteImages=falseの時だけ強調表示」という排他仕様
    /// だったが、「ノート画像ON時にも強調表示を併用したい」との要望で分離。両方OFFにならないという
    /// 制約はMainWindow側のUIで担保する)。</summary>
    public bool ShowHighlightGrid { get; set; } = false;

    /// <summary>ノート強調グリッドの太さ(px)。</summary>
    public double HighlightLineWidth { get; set; } = 3.0;

    /// <summary>ノート強調グリッドの色。</summary>
    public Color HighlightLineColor { get; set; } = Color.FromRgb(0xFF, 0xD4, 0x00);

    /// <summary>強調グリッドの対象からフリーズアロー終点を除外するか(2026-07-26要望対応、既定OFF)。
    /// ONの場合、フリーズの終点位置には強調グリッド(横棒)を描かない(始点は従来通り描く)。</summary>
    public bool ExcludeFreezeEndFromHighlight { get; set; } = false;

    /// <summary>強調グリッドの色をHighlightLineColorの固定色ではなく、そのノート自身の色
    /// (setColor/ncolor_data由来、レーンのブラシと同じ色)にするか(2026-08-02要望対応、既定OFF)。
    /// ONの場合、DrawNotesAndFreezesが各ノート/フリーズの描画に使った色をそのまま強調グリッドにも
    /// 使う(HighlightLineColorは無視される)。</summary>
    public bool UseNoteColorForHighlight { get; set; } = false;

    /// <summary>
    /// 表示設定(AppSettings由来)をまとめて適用し、再描画する。MainWindowが起動時・設定変更時に呼ぶ。
    /// </summary>
    public void ApplyDisplaySettings(bool showNoteImages, bool showHighlightGrid, double highlightLineWidth, Color highlightLineColor,
        bool excludeFreezeEndFromHighlight = false, bool useNoteColorForHighlight = false)
    {
        ShowNoteImages = showNoteImages;
        ShowHighlightGrid = showHighlightGrid;
        HighlightLineWidth = highlightLineWidth;
        HighlightLineColor = highlightLineColor;
        ExcludeFreezeEndFromHighlight = excludeFreezeEndFromHighlight;
        UseNoteColorForHighlight = useNoteColorForHighlight;
        InvalidateVisual();
    }

    /// <summary>再生開始フレーム可視化ラインの太さ(px)(2026-07-17f、AppSettingsから適用)</summary>
    public double PlaybackStartLineWidth { get; set; } = 2.0;

    /// <summary>再生開始フレーム可視化ラインの色(同上)</summary>
    public Color PlaybackStartLineColor { get; set; } = Color.FromRgb(0x4F, 0xC3, 0xF7);

    /// <summary>再生開始フレームラインの表示設定を適用して再描画する(2026-07-17f)</summary>
    public void ApplyPlaybackStartLineSettings(double width, Color color)
    {
        PlaybackStartLineWidth = width;
        PlaybackStartLineColor = color;
        InvalidateVisual();
    }

    /// <summary>マクロ範囲マーカー・ハイライト帯の太さ・色(2026-07-26要望対応、AppSettingsから適用)</summary>
    public double MacroRangeMarkerWidth { get; set; } = 2.0;

    /// <summary>マクロ範囲マーカー・ハイライト帯の色(同上)</summary>
    public Color MacroRangeHighlightColor { get; set; } = Color.FromRgb(0xFF, 0xA5, 0x00);

    /// <summary>マクロ範囲マーカーの表示設定を適用して再描画する(2026-07-26)</summary>
    public void ApplyMacroRangeHighlightSettings(double width, Color color)
    {
        MacroRangeMarkerWidth = width;
        MacroRangeHighlightColor = color;
        InvalidateVisual();
    }

    // =====================================================================
    // タブリンク機能(2026-07-26要望対応、AppSettingsから適用)
    // =====================================================================

    public double LinkedNoteSizeRatio { get; set; } = 0.85;
    public Color LinkedNoteColor { get; set; } = Color.FromRgb(0x99, 0x99, 0x99);
    public double LinkedHighlightWidthRatio { get; set; } = 0.5;
    public double LinkedHighlightHeight { get; set; } = 2.0;
    public Color LinkedHighlightColor { get; set; } = Color.FromRgb(0x99, 0x99, 0x99);

    /// <summary>タブリンク機能の背景ノート表示設定を適用して再描画する(2026-07-26)</summary>
    public void ApplyLinkedBackgroundSettings(double noteSizeRatio, Color noteColor,
        double highlightWidthRatio, double highlightHeight, Color highlightColor)
    {
        LinkedNoteSizeRatio = noteSizeRatio;
        LinkedNoteColor = noteColor;
        LinkedHighlightWidthRatio = highlightWidthRatio;
        LinkedHighlightHeight = highlightHeight;
        LinkedHighlightColor = highlightColor;
        InvalidateVisual();
    }

    // =====================================================================
    // 波形表示+StartNumber編集モード(2026-07-18、要望メモ07-15項目7・8)
    // =====================================================================

    /// <summary>波形ピークキャッシュ(MainWindowがバックグラウンドデコード後に設定)。null=未読込</summary>
    public WaveformPeaks? Waveform { get; set; }

    /// <summary>読み込み済み音楽ファイルの全体長(フレーム、60fps基準、2026-07-26要望対応)。
    /// MainWindowが音楽読込完了(MediaOpened)のたびに設定する。null=未読込、または長さ不明。
    /// 「サビから制作」等、ノートを置く前でも曲の長さぶんスクロールできるようにするための値
    /// (MaxTickInProjectで既存の「ノートの最大tick」「空プロジェクトの最低8小節」と比較し、
    /// より大きい方を採用する)。</summary>
    public double? AudioTotalFrames { get; set; }

    /// <summary>波形表示ON/OFF(譜面ビュー背景の最下層に描画)</summary>
    public bool ShowWaveform { get; set; }

    /// <summary>StartNumber編集モード(2026-07-18)。ON中は通常編集を無効化し、
    /// ドラッグ=StartNumber調整(譜面側が波形に対して動く)、クリック=ガイド線設置となる。</summary>
    public bool StartNumberEditMode { get; set; }

    /// <summary>スナップ用ガイド線の位置(絶対フレーム=波形に固定)。最大1本、クリックで設置/移動</summary>
    public double? GuideFrame { get; set; }

    /// <summary>StartNumberドラッグでの変更後に発火(MainWindowが右パネルの数値表示更新に使う)</summary>
    public event Action? StartNumberChangedByDrag;

    // --- StartNumberドラッグ状態 ---
    private bool _snDragging;
    private bool _snMoved;
    private Point _snStartPointInScroll;
    private double _snStartNumber0;
    private double _snScrollOffset0;
    private double _snFramesPerPx;

    /// <summary>tick(小数可)→絶対フレーム。TimingEngineのlong APIを線形補間して小数tickに対応</summary>
    private static double FrameAtTick(TimingEngine engine, double tick)
    {
        long t0 = (long)Math.Floor(Math.Max(0, tick));
        double f0 = engine.TickToFrame(t0);
        double f1 = engine.TickToFrame(t0 + 1);
        return f0 + (Math.Max(0, tick) - t0) * (f1 - f0);
    }

    private void SnDown(MouseButtonEventArgs e)
    {
        if (Document is null) return;
        Focus();
        CaptureMouse();
        _snDragging = true;
        _snMoved = false;
        var sv = FindAncestorScrollViewer();
        _snStartPointInScroll = sv is not null ? e.GetPosition(sv) : e.GetPosition(this);
        _snStartNumber0 = Document.Project.StartNumber;
        _snScrollOffset0 = sv?.VerticalOffset ?? 0;

        // ドラッグ開始点のBPM区間からpx→frame換算係数を決める(BPM一定なら厳密、ソフラン時は開始点基準の近似)
        var engine = Document.Project.CreateTimingEngine();
        double tickAt = Math.Max(0, Document.CurrentLayout.YToTick(e.GetPosition(this).Y));
        double bpm = 120;
        foreach (var ev in engine.BpmEvents)
            if (ev.Tick <= tickAt) bpm = ev.Bpm; else break;
        double framesPerTick = TimingEngine.FramesPerMinute / bpm / TimingEngine.TicksPerBeat;
        _snFramesPerPx = framesPerTick / Document.CurrentLayout.PxPerTick;
        e.Handled = true;
    }

    private void SnMove(MouseEventArgs e)
    {
        if (!_snDragging || Document is null) return;
        var sv = FindAncestorScrollViewer();
        var cur = sv is not null ? e.GetPosition(sv) : e.GetPosition(this);
        double dpx = cur.Y - _snStartPointInScroll.Y;
        if (Math.Abs(dpx) > 2) _snMoved = true;

        // 下ドラッグ=譜面が曲に対して後ろへ=StartNumber増。波形が画面に固定されて見えるよう
        // スクロールオフセットを同量だけ補償する(上端近くでは補償しきれず波形側が動いて見える)
        // 2026-07-22: 譜面ビューReverse時はtickの進行方向(≒StartNumber変化の意味)が画面上下で
        // 入れ替わるため、符号を反転する。
        double snSign = Reverse ? -1 : 1;
        Document.Project.StartNumber = _snStartNumber0 + snSign * dpx * _snFramesPerPx;
        sv?.ScrollToVerticalOffset(Math.Max(0, _snScrollOffset0 - dpx));
        InvalidateVisual();
        e.Handled = true;
    }

    private void SnUp(MouseButtonEventArgs e)
    {
        if (!_snDragging || Document is null) return;
        _snDragging = false;
        ReleaseMouseCapture();
        var engine = Document.Project.CreateTimingEngine();

        if (!_snMoved)
        {
            // クリック=ガイド線の設置/移動(波形=絶対フレームに固定、最大1本)
            GuideFrame = FrameAtTick(engine, Document.CurrentLayout.YToTick(e.GetPosition(this).Y));
        }
        else if (GuideFrame is { } gf)
        {
            // ドラッグ終了時: ガイド線に最も近い小節線が10px以内なら吸着(StartNumberを厳密整合)
            double guideY = Document.CurrentLayout.TickToY(engine.FrameToTick(gf));
            double bestDist = double.MaxValue;
            long bestTick = -1;
            long tick = 0;
            int measure = 0;
            // 2026-07-22: 「guideYから画面下方向へ2000px」はReverse時に意味が逆転する(下方向=tick減少に
            // なりうる)ため、tick基準(guideのtick + 2000px相当のtick数)で探索上限を求める形に変更。
            long maxTick = (long)engine.FrameToTick(gf) + (long)(2000 / Document.CurrentLayout.PxPerTick) + 1;
            while (tick <= maxTick && measure < 10000)
            {
                double y = Document.CurrentLayout.TickToY(tick);
                double d = Math.Abs(y - guideY);
                if (d < bestDist) { bestDist = d; bestTick = tick; }
                tick += engine.SignatureAt(tick).TicksPerMeasure;
                measure++;
            }
            if (bestTick >= 0 && bestDist <= 10)
                Document.Project.StartNumber += gf - engine.TickToFrame(bestTick);
        }

        Document.NotifyChanged();
        StartNumberChangedByDrag?.Invoke();
        e.Handled = true;
    }

    // =====================================================================
    // 時間情報レーンのドラッグによる「時間範囲選択」(2026-07-27要望対応)
    // 元はレーン入替マクロ専用の「範囲選択モード」だったが、時間情報レーン上のドラッグ操作へ
    // 置き換えた。専用モードのON/OFF切替は廃止し、時間情報レーン上での左ドラッグそのものが
    // 常時この操作として機能する(通常のノート編集とは列が完全に分離されるため、モードの排他制御は
    // 不要になった)。オブジェクトは選択対象ではなく、あくまで開始tick・終了tickを指定する操作。
    // =====================================================================

    /// <summary>時間範囲選択の設置/ドラッグ移動があった後に発火。MainWindowが右パネルの
    /// 範囲表示の更新に使う。</summary>
    public event Action? TimeRangeSelectionChanged;

    // --- 範囲マーカードラッグ状態 ---
    private int _rsDraggingWhich; // 0=なし、1=始点、2=終点
    private const double RangeMarkerHitToleranceY = 10.0;

    /// <summary>新規範囲をドラッグ中か。ドラッグ開始位置を_rsCreateAnchorTickに固定し、ドラッグ中の
    /// Y座標との間で常にMin/Maxを取って始点/終点を更新する(上下どちらへドラッグしても正しい範囲になる)。</summary>
    private bool _rsCreatingNewRange;
    private long _rsCreateAnchorTick;

    /// <summary>新規範囲作成ドラッグ中、実際にアンカーとは異なるtickへ動いたか(2026-07-29要望対応)。
    /// falseのまま(=シングルクリックのみで実質的なドラッグが無かった)場合、RsUpで範囲の書き込み・
    /// 変更通知を一切行わない(「始点=終点」の意味を為さない範囲がクリックだけで確定してしまう
    /// 不具合への対処)。</summary>
    private bool _rsRangeDidMove;

    /// <summary>指定X座標が時間情報レーン(ColumnKind.TimeInfo)の列内かどうか(2026-07-27要望対応)。
    /// 時間範囲選択ドラッグの開始判定に使う(開始後はX座標を問わずY座標だけで追跡する)。</summary>
    private bool IsInTimeInfoLane(double x)
    {
        if (Document is null) return false;
        return Document.CurrentLayout.Column(ColumnKind.TimeInfo).Contains(x);
    }

    /// <summary>現在、時間範囲選択のドラッグ操作が進行中か(新規範囲作成・既存端点の個別移動のいずれか)。</summary>
    private bool IsTimeRangeDragActive => _rsCreatingNewRange || _rsDraggingWhich != 0;

    private void RsDown(MouseButtonEventArgs e)
    {
        if (Document is null) return;
        Focus();
        var tab = Document.CurrentTab;
        double y = e.GetPosition(this).Y;
        var layout = Document.CurrentLayout;

        // 既存マーカーへのヒット判定(端点個別ドラッグ)を優先する
        int hitWhich = 0;
        double bestDist = RangeMarkerHitToleranceY;
        if (tab.TimeRangeSelectionStartTick is { } st)
        {
            double d = Math.Abs(layout.TickToY(st) - y);
            if (d <= bestDist) { bestDist = d; hitWhich = 1; }
        }
        if (tab.TimeRangeSelectionEndTick is { } et)
        {
            double d = Math.Abs(layout.TickToY(et) - y);
            if (d <= bestDist) { bestDist = d; hitWhich = 2; }
        }

        if (hitWhich != 0)
        {
            CaptureMouse();
            _rsDraggingWhich = hitWhich;
            e.Handled = true;
            return;
        }

        // 時間情報レーン上、マーカー以外の場所からのドラッグは新規範囲作成
        // (ドラッグ1回で始点〜終点を一気に指定する)。
        // 2026-07-29要望対応: ここではまだ範囲を書き込まない(アンカーだけを保持する)。シングルクリック
        // (ドラッグ無し)のまま終わった場合に「始点=終点」の意味を為さない範囲が確定してしまうのを防ぐため、
        // 実際にアンカーとは異なるtickへ動いた時点(RsMove参照)で初めて範囲の書き込みを開始する。
        long anchorTick = Math.Max(0, Document.Snap.Snap(layout.YToTick(y)));
        CaptureMouse();
        _rsCreatingNewRange = true;
        _rsCreateAnchorTick = anchorTick;
        _rsRangeDidMove = false;
        e.Handled = true;
    }

    private void RsMove(MouseEventArgs e)
    {
        if (Document is null) return;
        var layout = Document.CurrentLayout;
        var tab = Document.CurrentTab;
        long tick = Math.Max(0, Document.Snap.Snap(layout.YToTick(e.GetPosition(this).Y)));

        if (_rsCreatingNewRange)
        {
            // 2026-07-29要望対応: アンカーと同じtickのままの間は「まだドラッグが成立していない」とみなし、
            // 範囲を書き込まない(ハイライトも表示しない)。異なるtickへ動いた時点で初めて範囲を確定させる。
            if (tick != _rsCreateAnchorTick) _rsRangeDidMove = true;
            if (_rsRangeDidMove)
            {
                tab.TimeRangeSelectionStartTick = Math.Min(_rsCreateAnchorTick, tick);
                tab.TimeRangeSelectionEndTick = Math.Max(_rsCreateAnchorTick, tick);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (_rsDraggingWhich == 0) return;
        if (_rsDraggingWhich == 1) tab.TimeRangeSelectionStartTick = tick;
        else tab.TimeRangeSelectionEndTick = tick;
        InvalidateVisual();
        e.Handled = true;
    }

    private void RsUp(MouseButtonEventArgs e)
    {
        if (_rsCreatingNewRange)
        {
            _rsCreatingNewRange = false;
            ReleaseMouseCapture();
            // 2026-07-29要望対応: 実際にドラッグが成立した場合のみ変更を確定させる(シングルクリックのみの
            // 場合は範囲・既存の選択状態に一切触れず、変更通知も出さない)。
            if (_rsRangeDidMove)
            {
                Document?.NotifyChanged();
                TimeRangeSelectionChanged?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (_rsDraggingWhich == 0) return;
        _rsDraggingWhich = 0;
        ReleaseMouseCapture();
        Document?.NotifyChanged();
        TimeRangeSelectionChanged?.Invoke();
        e.Handled = true;
    }

    /// <summary>時間範囲選択のクリア(2026-07-27要望対応: Escapeキーで呼ばれる)。</summary>
    public bool ClearTimeRangeSelection()
    {
        if (Document is null) return false;
        var tab = Document.CurrentTab;
        if (tab.TimeRangeSelectionStartTick is null && tab.TimeRangeSelectionEndTick is null) return false;
        tab.TimeRangeSelectionStartTick = null;
        tab.TimeRangeSelectionEndTick = null;
        Document.NotifyChanged();
        TimeRangeSelectionChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    public void UpdateViewport(Rect rect)
    {
        ViewportRect = rect;
        InvalidateVisual();
    }

    /// <summary>マウス操作の受け皿(仕様書6.3.1)。MainWindowがEditorDocumentと紐付けて生成する。</summary>
    public SmartToolController? Controller { get; set; }

    /// <summary>共同編集セッション(2026-09-20、ノート所有者アイコン用)。非nullの間、各ノート/フリーズ
    /// 始点の左上に置いた参加者の識別色で丸アイコンを重ね描きする(共同編集セッションが無い、または
    /// 未追跡のセルではnullが返るため何も描かれない)。MainWindow側で共同編集の開始/切断に合わせて
    /// 設定/クリアする(Document/Controllerと同じ、単純なCLRプロパティとしての受け渡し)。</summary>
    public CollabSessionController? CollabSession { get; set; }

    /// <summary>マウスホバー中の座標(2026-07-25、カーソルライン表示用)。キャンバス外に出るとnull。</summary>
    private Point? _hoverPos;

    public ChartCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        // 2026-07-26: 譜面ビューにフォーカスがある間はIMEを無効化する。日本語入力ON状態だと
        // Space等のショートカットキーが変換確定操作に奪われて効かなくなるため(ユーザー要望)。
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    // =====================================================================
    // マウス入力(SmartToolControllerへそのまま橋渡しするだけの薄い層)
    // =====================================================================

    private static PointerModifiers ModifiersOf(MouseEventArgs e)
    {
        var mods = PointerModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= PointerModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= PointerModifiers.Ctrl;
        return mods;
    }

    private static PointerPos PosOf(Point p) => new(p.X, p.Y);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 2026-09-07要望対応: マーカーへホバー中にポップアップが開いたままだと、ドラッグ開始直後の
        // マウスキャプチャや後続のクリック判定に干渉し「マーカー付近でドラッグ・右クリックが
        // 効かなくなる」不具合の原因になりうるため、新しい操作を始める前に必ず閉じておく。
        MarkerCommentPopup.HideImmediately();
        if (StartNumberEditMode) { SnDown(e); return; } // 2026-07-18: 専用モード(通常編集無効)
        if (Controller is null) return;
        Focus();

        // 2026-07-17f: マーカーレーンのダブルクリック=再生開始フレーム設定(未解決事項§2-2)。
        // WPFのClickCountをそのまま使う(自前ダブルクリック判定エンジンは不要)。1回目のクリックは
        // 押下即処理でカレントtickを設定済みだが冪等なので、取り消し処理も不要。
        if (e.ClickCount == 2 && Controller.DoubleLeft(PosOf(e.GetPosition(this))))
        {
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 2026-07-27要望対応: 時間情報レーン上の単発ドラッグは「時間範囲選択」専用
        // (ダブルクリックの再生開始位置指定より後、通常のBeginLeftより前に判定する)。
        if (IsInTimeInfoLane(e.GetPosition(this).X)) { RsDown(e); return; }

        CaptureMouse();
        Controller.BeginLeft(PosOf(e.GetPosition(this)), ModifiersOf(e));
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        // 2026-09-07要望対応: 左クリック側と同様、既存のマーカーホバーポップアップを閉じてから始める。
        MarkerCommentPopup.HideImmediately();
        if (StartNumberEditMode) { e.Handled = true; return; } // モード中は右クリック編集も無効(2026-07-18)
        if (Controller is null) return;
        Focus();
        CaptureMouse();
        Controller.BeginRight(PosOf(e.GetPosition(this)), ModifiersOf(e));
        InvalidateVisual();
    }

    /// <summary>ホイールクリック(中ボタン)でフリーズアロー配置(2026-07-17、FUJIエディタ同等機能)。
    /// WPFのFrameworkElementには中ボタン専用のOn*ButtonDownが無いため、OnMouseDownでChangedButtonを見る。</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle) MarkerCommentPopup.HideImmediately(); // 2026-09-07要望対応
        if (StartNumberEditMode) return; // モード中は中ボタン配置も無効(2026-07-18)
        if (e.ChangedButton != MouseButton.Middle || Controller is null) return;
        Focus();
        Controller.MiddleClick(PosOf(e.GetPosition(this)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // 2026-07-25: カーソルライン(最寄りスナップ位置の可視化)のため、ボタン押下の有無に関わらず
        // 常にホバー座標を更新して再描画する(以前はドラッグ中=IsMouseCaptured時のみ再描画していた)。
        // 2026-09-07修正: 座標はローカル変数posへ一度だけ取得し、以降はposを使い回す。
        // 従来は_hoverPos(フィールド)を都度読み直していたが、UpdateMarkerHoverPopupが新規に
        // ポップアップを開いた際、WPFがこのキャンバスへOnMouseLeaveを再入的に発火させ、そちらで
        // _hoverPosがnullへ戻される場合があった(ポップアップがカーソル直下付近に出るため)。
        // その結果、同じOnMouseMove内で後続の_hoverPos.Value読み出しが
        // 「Nullable object must have a value」で例外になっていた(マーカーレーンへの新規配置時に
        // 必ず再現する不具合)。
        var pos = e.GetPosition(this);
        _hoverPos = pos;
        // 2026-09-07要望対応: ドラッグ中(マウスボタン押下中、IsMouseCaptured)は新しくポップアップを
        // 開いたり内容を切り替えたりしない。ドラッグ経路がマーカーレーン付近を通っただけでポップアップが
        // 開き、そのままドラッグ操作や後続のクリックへ干渉する不具合があったため。
        if (!IsMouseCaptured) UpdateMarkerHoverPopup(pos); // 2026-08-08要望対応: マーカーコメントのホバーポップアップ
        if (IsTimeRangeDragActive) { RsMove(e); return; } // 2026-07-27: 時間範囲選択ドラッグ中
        if (StartNumberEditMode) { SnMove(e); return; }
        if (Controller is null) { InvalidateVisual(); return; }
        if (IsMouseCaptured) Controller.Move(PosOf(e.GetPosition(this)));
        UpdateCursor(pos); // 2026-08-08要望対応: 今何ができるか/しているかをカーソル形状で示す
        InvalidateVisual();
    }

    /// <summary>マウスカーソルの見た目を現在の状態に応じて切り替える(2026-08-08要望対応)。
    /// ・Ctrl+ドラッグ(移動先への複製、FinishMove参照)中: 通常の矢印カーソルに「+」の付いた
    ///   コピー系カーソル(WPFの標準カーソルセットに完全一致する「矢印+プラス」は存在しないため、
    ///   最も近い意味を持つCursors.Crossで代用している)。
    /// ・フリーズアローの端点(FreezeStart/FreezeEnd、掴むと長さ変更になる部位)のホバー中・
    ///   リサイズドラッグ確定中: 上下方向矢印(Cursors.SizeNS、リサイズが縦方向であることに対応)。
    /// ・それ以外: 既定の矢印カーソル。
    /// StartNumber編集モード・時間範囲選択ドラッグ中はそれぞれ専用の見た目を持つため、
    /// このメソッドが呼ばれる前に個別のreturnで弾かれている(OnMouseMove参照)。</summary>
    private void UpdateCursor(Point pos)
    {
        if (Controller is null || Document is null) { Cursor = Cursors.Arrow; return; }

        // ドラッグ確定中(閾値超過後): SmartToolController側のプレビューをそのまま見る
        if (Controller.ResizeFreezePreview is not null) { Cursor = Cursors.SizeNS; return; }
        if (Controller.MoveObjectsPreview is not null)
        {
            Cursor = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Cursors.Cross : Cursors.Arrow;
            return;
        }

        // 未ドラッグ時: フリーズ端点にホバーしていれば、掴んだ場合の挙動(長さ変更)を予告する
        if (!IsMouseCaptured && !Controller.ColorEditModeEnabled && !StartNumberEditMode)
        {
            var hit = Document.CurrentLayout.HitTest(Document.CurrentTab, Document.Project, pos.X, pos.Y);
            if (hit is { Kind: ObjectKind.FreezeStart or ObjectKind.FreezeEnd })
            {
                Cursor = Cursors.SizeNS;
                return;
            }
        }

        Cursor = Cursors.Arrow;
    }

    /// <summary>マウスがキャンバス外に出たらカーソルラインを消す(2026-07-25)。</summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPos = null;
        MarkerCommentPopup.ScheduleHide(); // 2026-08-08要望対応: キャンバス外に出たら猶予付きでポップアップを閉じる(ピン留め中を除く)
        Cursor = Cursors.Arrow; // 2026-08-08要望対応: キャンバス外に出たらカーソル形状も既定へ戻す
        InvalidateVisual();
    }

    /// <summary>マーカーレーン上のマーカーへホバーした際、コメント全文のポップアップを表示する
    /// (2026-08-08要望対応)。マーカーレーンの表示幅が狭くDrawEventTagのクリップ描画では
    /// 長いコメントが読み切れないため。当たり判定はChartLayout.HitTestをそのまま再利用する
    /// (x座標でマーカーレーンかどうかも含めて判定されるため、レーン外なら自然にnullが返る)。
    /// 2026-08-08b要望対応: ポップアップはマウス追従ではなくマーカーの実座標(anchorPoint、
    /// マーカーレーンの列位置とtickからのY座標)に固定表示する。ホバーが外れた場合も即座には
    /// 閉じずScheduleHide(猶予付き消去)を使う(ポップアップ内のボタンを押そうとカーソルを
    /// 動かした瞬間に消えてしまう問題への対応)。</summary>
    private void UpdateMarkerHoverPopup(Point pos)
    {
        if (Document is null) { MarkerCommentPopup.ScheduleHide(); return; }
        var hit = Document.CurrentLayout.HitTest(Document.CurrentTab, Document.Project, pos.X, pos.Y);
        if (hit is { Kind: ObjectKind.Marker } h)
        {
            var marker = Document.Project.Markers.FirstOrDefault(m => m.Tick == h.Tick);
            if (marker is not null)
            {
                var col = Document.CurrentLayout.Column(ColumnKind.Marker);
                var anchorPoint = new Point(col.X + col.Width, Document.CurrentLayout.TickToY(h.Tick));
                MarkerCommentPopup.Show(this, h.Tick, marker.Comment, anchorPoint);
                return;
            }
        }
        MarkerCommentPopup.ScheduleHide();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsTimeRangeDragActive) { RsUp(e); return; } // 2026-07-27: 時間範囲選択ドラッグ中
        if (StartNumberEditMode) { SnUp(e); return; }
        if (Controller is null) return;
        // 2026-07-26: Ctrl+ドラッグ=複製の判定はボタンを離した瞬間のCtrl状態で行うため、
        // ここで最新のModifiersOf(e)を渡す(押下時の状態のまま固定しない)。
        Controller.End(PosOf(e.GetPosition(this)), ModifiersOf(e));
        ReleaseMouseCapture();
        UpdateCursor(e.GetPosition(this)); // 2026-08-08要望対応: ドラッグ確定中の見た目を引きずらないよう解放時点の状態へ戻す
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Controller is null) return;
        Controller.End(PosOf(e.GetPosition(this)), ModifiersOf(e));
        ReleaseMouseCapture();
        UpdateCursor(e.GetPosition(this)); // 2026-08-08要望対応
        InvalidateVisual();
    }

    /// <summary>
    /// 2026-07-26再定義(ユーザー確定仕様、統一性重視): Shift+ホイール=縦方向ズーム(ハイスピ/pxPerTick)、
    /// Alt+ホイール=横方向ズーム(ChartLayout.ZoomScale。カラム幅・ノート表示サイズが連動、仕様書4.3。
    /// 旧Ctrl単独から移動)、Ctrl+ホイール=スクロール量2倍、Shift+Ctrl+ホイール=スクロール量4倍
    /// (旧・縦横同時ズームを廃止し、こちらへ用途変更)。修飾キー無しは既定のスクロール(ScrollViewerへ委譲)。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Document is null) return;
        var layout = Document.CurrentLayout;
        double step = e.Delta > 0 ? 1.1 : 1 / 1.1;

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (shift && ctrl && !alt)
        {
            ScrollByMultiplier(e.Delta, 4);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt)
        {
            ScrollByMultiplier(e.Delta, 2);
            e.Handled = true;
        }
        else if (alt && !shift && !ctrl)
        {
            // 横方向ズーム(ChartLayout.ZoomScale、旧Ctrl単独から移動)
            layout.SetZoom(layout.ZoomScale * step);
            e.Handled = true;
            InvalidateAll();
            // 2026-07-26: プロジェクトファイルへズーム率を保存(次回オープン時に復元、ユーザー要望)。
            Document.Project.EditorZoomScale = layout.ZoomScale;
        }
        else if (shift && !ctrl && !alt)
        {
            // 縦方向ズーム(pxPerTick)。2026-07-17: カーソル位置を中心に拡大縮小する要望対応。
            // PxPerTick変更前後でカーソル下のtickの画面上位置が変わらないよう、垂直オフセットを補正する。
            double mouseY = e.GetPosition(this).Y;
            double tickAtCursor = layout.YToTick(mouseY);
            var sv = FindAncestorScrollViewer();
            double oldOffset = sv?.VerticalOffset ?? 0;

            layout.PxPerTick = Math.Clamp(layout.PxPerTick * step, ChartLayout.MinPxPerTick, ChartLayout.MaxPxPerTick); // 2026-07-19g: 分解能スケールに追従
            e.Handled = true;
            InvalidateAll();
            // 2026-07-26: プロジェクトファイルへズーム率を保存(次回オープン時に復元、ユーザー要望)。
            Document.Project.EditorZoomPxPerTick = layout.PxPerTick;
            // 2026-08-08: InvalidateAll()は再描画を予約するだけで、この時点ではMeasureOverride/OnRenderが
            // まだ走っていない。そのため反転(Reverse)基準のキャッシュ(_reverseContentHeight)がPxPerTick変更前の
            // 値のまま残っており、直後のTickToY呼び出し(下記)がReverse表示時のみ古い基準で計算されてズレる
            // 不具合があった。MeasureOverride/OnRenderと同じ手順でここでも明示的に基準を更新し、非反転表示と
            // 挙動を統一する。
            layout.Reverse = Reverse;
            layout.RefreshContentHeight(MaxTickInProject(Document, AudioTotalFrames));

            if (sv is not null)
            {
                double newY = layout.TickToY(tickAtCursor);
                double desiredOffset = newY - (mouseY - oldOffset);
                sv.ScrollToVerticalOffset(Math.Max(0, desiredOffset));
            }
        }
    }

    /// <summary>2026-07-26: Ctrl/Shift+Ctrl+ホイール用の倍速スクロール。OS既定のホイール1ノッチあたりの
    /// 行数(SystemParameters.WheelScrollLines)を基準に、その整数倍だけScrollViewerのLineUp/Downを
    /// 呼ぶ(既定スクロールの内部実装を再利用しつつ、環境ごとの体感速度差もそのまま維持できる)。</summary>
    private void ScrollByMultiplier(int delta, int multiplier)
    {
        var sv = FindAncestorScrollViewer();
        if (sv is null) return;
        int lines = Math.Max(1, SystemParameters.WheelScrollLines) * multiplier;
        for (int i = 0; i < lines; i++)
        {
            if (delta > 0) sv.LineUp(); else sv.LineDown();
        }
    }

    private ScrollViewer? _ancestorScrollViewerCache;

    /// <summary>このCanvasを収めるScrollViewerを探す(Shift+ホイールのカーソル中心ズーム補正用、2026-07-17)。</summary>
    private ScrollViewer? FindAncestorScrollViewer()
    {
        if (_ancestorScrollViewerCache is not null) return _ancestorScrollViewerCache;
        DependencyObject? cur = this;
        while (cur is not null)
        {
            cur = VisualTreeHelper.GetParent(cur);
            if (cur is ScrollViewer sv) { _ancestorScrollViewerCache = sv; break; }
        }
        return _ancestorScrollViewerCache;
    }

    // =====================================================================
    // サイズ(全体の論理サイズを返す。実際に描くのは可視範囲のみ)
    // =====================================================================

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Document is null) return new Size(0, 0);
        var layout = Document.CurrentLayout;
        long maxTick = MaxTickInProject(Document, AudioTotalFrames);
        double h = layout.ContentHeight(maxTick); // Reverseの影響を受けない素の高さ(スクロール範囲自体は不変)
        // 2026-07-22: マウス操作(YToTick)がOnRenderの前に発生するケースに備え、ここでも反転基準を更新しておく。
        layout.Reverse = Reverse;
        layout.RefreshContentHeight(maxTick);
        return new Size(layout.TotalWidth, h);
    }

    /// <summary>internal化(2026-07-26): ChartMinimapが全体スクロール範囲の算出に再利用するため。
    /// audioTotalFrames(2026-07-26追加): 読込済み音楽の全長(フレーム)。指定があれば、ノートを
    /// まだ置いていない範囲でも曲の長さぶんスクロールできるよう、最大tickの下限に加味する
    /// (「サビから制作」等、末尾から作り始めるスタイルへの対応)。</summary>
    internal static long MaxTickInProject(EditorDocument doc, double? audioTotalFrames = null)
    {
        long max = 192 * 8; // 空プロジェクトでも最低8小節ぶんは表示領域を確保
        var tab = doc.CurrentTab;
        foreach (var lane in tab.Lanes)
        {
            foreach (var t in lane.Notes) max = Math.Max(max, t);
            foreach (var f in lane.Freezes) max = Math.Max(max, f.EndTick);
        }
        foreach (var e in tab.SpeedEvents) max = Math.Max(max, e.Tick);
        foreach (var e in tab.BoostEvents) max = Math.Max(max, e.Tick);
        foreach (var e in doc.Project.BpmEvents) max = Math.Max(max, e.Tick);
        foreach (var m in doc.Project.Markers) max = Math.Max(max, m.Tick);
        if (audioTotalFrames is { } totalFrames && totalFrames > 0)
        {
            var engine = doc.Project.CreateTimingEngine();
            long audioTick = (long)Math.Ceiling(engine.FrameToTick(totalFrames));
            max = Math.Max(max, audioTick);
        }
        return max;
    }

    // =====================================================================
    // 描画本体
    // =====================================================================

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (Document is null) return;

        var layout = Document.CurrentLayout;
        var project = Document.Project;
        var tab = Document.CurrentTab;
        var engine = project.CreateTimingEngine();

        // 2026-07-22: 譜面ビューReverse(環境設定のみで切替)。RefreshContentHeightは反転基準の
        // コンテンツ高さを最新化する(Reverse=false時は参照されないが常に呼んでおいて問題ない)。
        layout.Reverse = Reverse;
        layout.RefreshContentHeight(MaxTickInProject(Document, AudioTotalFrames));

        var viewport = ViewportRect.IsEmpty ? new Rect(0, 0, layout.TotalWidth, Math.Max(RenderSize.Height, 600)) : ViewportRect;
        double yTop = Math.Max(0, viewport.Top - 32);   // 少し余裕を持ってカリング
        double yBottom = viewport.Bottom + 32;
        // 2026-07-22: Reverse時はYが大きいほどtickが小さくなるため、YToTick後にMin/Maxを取り直す
        // (yTop<yBottomは常に成立するが、対応するtickの大小は反転しうる)。
        double tA = layout.YToTick(yTop), tB = layout.YToTick(yBottom);
        long tickMin = Math.Max(0, (long)Math.Min(tA, tB) - 1);
        long tickMax = (long)Math.Max(tA, tB) + 1;
        // 2026-08-02要望対応(2026-08-03実装): 全体のカリング用tickMinは0でクランプされているため、
        // 0小節目より手前(tick<0)の再生開始ライン・再生位置ラインが常に描画対象外になり、目視テストで
        // 実際には(無音を)再生しているのに何も表示されず「始まっていないように見える」不具合があった。
        // 座標変換自体はTopMargin(400px)の余白内であれば負のtickでも問題ないため、この2つの線だけは
        // 0でクランプしていない生のtickMinを使う(グリッド線・ノート等、他の描画には影響させない)。
        long tickMinRaw = (long)Math.Min(tA, tB) - 1;
        // 2026-08-08不具合修正: マイナスフレーム許容(ChartProject.AllowNegativeFramePlacement)ON時、
        // SmartToolController側は既にtick<0への配置を許可しているにもかかわらず、本体の描画カリングは
        // ここまで一律tickMin(0クランプ済み)を使っていたため、配置したノート/フリーズ/speed・boost/
        // マーカー等がカリング範囲外扱いとなり表示されない不具合があった(グリッド線側は
        // 2026-08-08時点で既にtickMinRaw対応済みだったが、オブジェクト描画側の対応が漏れていた)。
        // グリッド線(DrawGridAndMeasureLines)と同じ考え方で、フラグON時のみ0クランプ無しの
        // tickMinRawをオブジェクト描画のカリング下限として使う。
        bool allowNegativeFrame = Document.Project.AllowNegativeFramePlacement;
        long cullTickMin = allowNegativeFrame ? tickMinRaw : tickMin;

        DrawWaveform(dc, layout, engine, yTop, yBottom); // 最下層(2026-07-18)。カラム背景は半透明のため透ける
        DrawColumnBackgrounds(dc, layout, yTop, yBottom);
        DrawColumnSeparators(dc, layout, yTop, yBottom);
        DrawGridAndMeasureLines(dc, layout, engine, Document.Snap, tickMin, tickMax, tickMinRaw);
        // 2026-08-02要望対応: 再生開始ラインをノート(画像)・強調表示の裏へ回す(グリッド > 再生開始
        // ライン > ノート画像 > 強調表示、の順)。従来はノート・強調表示より後(最前面寄り)に描画しており、
        // 密集した譜面で再生開始ラインがノートを覆い隠して見えづらいとの指摘対応。DrawNotesAndFreezes内で
        // ノート画像→強調表示の順に描く(ヒットフラッシュと同様、同一ノートの中で画像→強調表示の重ね順は
        // 元々維持されている)ため、ここではDrawPlaybackStartLine自体をDrawNotesAndFreezesより前へ移すだけでよい。
        DrawPlaybackStartLine(dc, layout, engine, tickMinRaw, tickMax);
        DrawLinkedBackgroundNotes(dc, layout, tab, cullTickMin, tickMax); // 2026-07-26: タブリンクの背景ノート(本体より奥)
        DrawNotesAndFreezes(dc, layout, tab, project, cullTickMin, tickMax);
        DrawLinkedBackgroundValueEvents(dc, layout, tab, cullTickMin, tickMax); // 2026-08-08: タブリンクの背景speed/boost(本体より奥)
        DrawValueEvents(dc, layout, tab, project, cullTickMin, tickMax);
        DrawMarkers(dc, layout, project, cullTickMin, tickMax);
        DrawTimeSignatures(dc, layout, project, engine, tickMin, tickMax); // 拍子は物理小節頭固定のため0クランプのままでよい
        DrawTimeInfoLane(dc, layout, tab, engine, cullTickMin, tickMax); // 2026-07-23: 時間情報表示レーン(TBD 1-1)
        DrawWordEntries(dc, layout, tab, cullTickMin, tickMax); // 2026-07-23: 歌詞レーン(TBD 4)
        DrawSelectionHighlights(dc, layout, Document, cullTickMin, tickMax);
        DrawDragPreview(dc, layout, tab, project);
        DrawPlaybackLine(dc, layout, tickMinRaw, tickMax);
        DrawTimeRangeSelectionHighlight(dc, layout, tab, cullTickMin, tickMax); // 2026-07-27: 時間情報レーンの時間範囲選択
        DrawGuideLine(dc, layout, engine, yTop, yBottom); // StartNumber編集モードのガイド線(2026-07-18)
        DrawCursorLine(dc, layout); // 2026-07-25: マウスホバー位置の最寄りスナップ可視化(最前面寄り)
        // 2026-08-08: レーンラベルは、譜面本体の描画と同じキャンバス上へのオーバーレイ描画をやめ、
        // ScrollViewer外の専用領域(LaneHeaderBar、MainWindow.xaml参照)へ分離した。スクロールで
        // その位置まで来たオブジェクトがラベルの下に隠れて操作できなくなる不具合の対応(要望対応)。
        // 描画本体はPaintLaneLabelBar(旧DrawLaneLabels)に残し、LaneHeaderBar.OnRenderから呼ぶ。
        DrawPluginOverlays(dc, layout, viewport); // 2026-07-26: プラグインのオーバーレイ描画(最前面)
    }

    /// <summary>登録済みの<see cref="IChartOverlayPlugin"/>を、本体の描画が全て終わった後に
    /// 最前面へ呼び出す(2026-07-26、プラグイン対応の土台)。1つのプラグインの描画中に例外が
    /// 発生しても他のプラグイン・本体描画自体は継続させる(不良プラグインで譜面ビュー全体が
    /// 真っ黒になる事故を防ぐ)。</summary>
    private void DrawPluginOverlays(DrawingContext dc, ChartLayout layout, Rect viewport)
    {
        if (OverlayPlugins.Count == 0) return;
        var transform = new PluginChartViewTransform
        {
            TickToScreenY = tick => layout.TickToY(tick),
            LaneToScreenX = laneIndex => layout.NoteColumn(laneIndex).CenterX,
            NoteLaneWidth = layout.NoteLaneWidth,
            ViewportWidth = viewport.Width,
            ViewportHeight = viewport.Height,
            IsReverse = layout.Reverse,
        };
        var chart = PluginChartContextBuilder.Build(Document);
        foreach (var plugin in OverlayPlugins)
        {
            try { plugin.RenderOverlay(dc, transform, chart); }
            catch (Exception ex) { PluginLog.Write($"{plugin.Id}: RenderOverlayで例外が発生しました({ex.Message})"); }
        }
    }

    /// <summary>譜面ビューReverse表示(2026-07-22、環境設定のみで切替。プレイテストには非適用)。</summary>
    public bool Reverse { get; set; }

    /// <summary>SKB操作モード(キーボード操作)が有効中か(2026-07-22、レーンラベルの2行目表示に使用)。
    /// MainWindow.ToggleKeyboardModeから反映される。</summary>
    public bool KeyboardModeActive { get; set; }

    /// <summary>レーンラベル欄へのノート数リアルタイム表示(2026-07-26、要望対応、既定OFF)。
    /// AppSettings.ShowLaneNoteCountから反映される(MainWindow.ApplyDisplaySettingsToCanvas参照)。</summary>
    public bool ShowLaneNoteCount { get; set; }

    /// <summary>2026-08-08要望対応: 時間情報レーンのフレーム表示にBlankFrameを加算するか
    /// (環境設定「表示」から適用)。dos.txt出力値と一致させ、勘違いを予防する目的。</summary>
    public bool ShowFrameWithBlankFrame { get; set; } = true;

    /// <summary>ShowFrameWithBlankFrameがONの場合、内部フレーム値へBlankFrameを加算して返す
    /// (MainWindow.ToDisplayFrameと対称のロジック)。</summary>
    private double ToDisplayFrame(double internalFrame)
        => ShowFrameWithBlankFrame && Document is not null
            ? internalFrame + Document.Project.BlankFrame
            : internalFrame;

    /// <summary>レーンラベル欄の1行目表示切替(2026-08-02要望対応、既定false=キー表示)。
    /// false: 従来通りKeyAssignLabel(実キー、例"S"、"E/R")を表示。
    /// true: LaneDef.LaneId(レーン名、例"left"、"sleft")を表示。同じレーンに複数キーを
    /// アサインした際にKeyAssignLabelが長くなり読みづらいとの要望対応。
    /// AppSettings.ShowLaneNameLabelから反映される(MainWindow.ApplyDisplaySettingsToCanvas参照)。</summary>
    public bool ShowLaneNameLabel { get; set; }

    private static readonly Brush WaveformBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0x55, 0x4F, 0xC3, 0xF7)));
    private static readonly Pen GuidePen = MakeFrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 2) { DashStyle = new DashStyle([6, 3], 0) });

    // --- カーソルライン(2026-07-25): マウスホバー中、最寄りのスナップ位置を可視化する。
    // 全レーン共通の細い線(今どのtickへスナップするか)+ホバー中のレーンだけ太い帯で強調
    // (今クリックするとどこに何が置かれるかを事前に明示する要望対応)。太さ・色は表示設定
    // (DisplaySettingsDialog)から変更可能(2026-07-25b、HighlightLineWidth/Colorと同じ方式)。
    public double CursorLineWidth { get; set; } = 1.0;
    public Color CursorLineColor { get; set; } = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public double CursorHighlightWidth { get; set; } = 8.0;
    public Color CursorHighlightColor { get; set; } = Color.FromRgb(0x00, 0xE5, 0xFF);

    public void ApplyCursorLineSettings(double lineWidth, Color lineColor, double highlightWidth, Color highlightColor)
    {
        CursorLineWidth = lineWidth;
        CursorLineColor = lineColor;
        CursorHighlightWidth = highlightWidth;
        CursorHighlightColor = highlightColor;
    }

    private static Brush MakeFrozen(Brush b) { b.Freeze(); return b; }
    private static Pen MakeFrozenPen(Pen p) { p.Freeze(); return p; }

    /// <summary>波形描画(2026-07-18、要望メモ07-15項目7)。譜面と同一の変換経路
    /// (音声時刻→frame→tick→Y)で描くため、Shift+スクロール伸縮・BPM変更に常に1:1で追従する。
    /// 1pxごとの行に分割し、各行が覆うフレーム範囲のピークを引く(visible-range cullingのみ描画)。</summary>
    private void DrawWaveform(DrawingContext dc, ChartLayout layout, TimingEngine engine, double yTop, double yBottom)
    {
        if (!ShowWaveform || Waveform is not { } peaks) return;
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        double cx = (left + right) / 2;
        double halfW = (right - left) / 2 * 0.95;
        const double rowPx = 1; // 2026-07-18b: 2pxでは波形が潰れるとの指摘で1px(=表示解像度の上限)へ

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int rows = (int)((yBottom - yTop) / rowPx) + 1;
            var maxPts = new List<Point>(rows);
            var minPts = new List<Point>(rows);
            double prevFrame = FrameAtTick(engine, Math.Max(0, layout.YToTick(yTop)));
            for (double y = yTop; y <= yBottom; y += rowPx)
            {
                double nextFrame = FrameAtTick(engine, Math.Max(0, layout.YToTick(y + rowPx)));
                var (lo, hi) = peaks.QueryPeak(prevFrame, nextFrame);
                maxPts.Add(new Point(cx + hi * halfW, y));
                minPts.Add(new Point(cx + lo * halfW, y));
                prevFrame = nextFrame;
            }
            if (maxPts.Count > 1)
            {
                ctx.BeginFigure(maxPts[0], isFilled: true, isClosed: true);
                for (int i = 1; i < maxPts.Count; i++) ctx.LineTo(maxPts[i], false, false);
                for (int i = minPts.Count - 1; i >= 0; i--) ctx.LineTo(minPts[i], false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(WaveformBrush, null, geo);
    }

    /// <summary>マウスカーソルの直下ではなく「今クリックしたら実際にどこへスナップされるか」を
    /// 可視化するカーソルライン(2026-07-25)。SmartToolController.SnappedTickAtをそのまま使うため、
    /// 実際の配置ロジックと表示が食い違うことは無い。StartNumber編集モード中は専用のガイド線
    /// (DrawGuideLine)と役割が重複し紛らわしいため非表示にする。</summary>
    private void DrawCursorLine(DrawingContext dc, ChartLayout layout)
    {
        if (StartNumberEditMode) return;
        if (_hoverPos is not { } pos || Controller is null) return;

        long tick = Controller.SnappedTickAt(new PointerPos(pos.X, pos.Y));
        double y = layout.TickToY(tick);
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;

        // 2026-07-25b: 太さ・色はAppSettings経由で可変のため、DrawPlaybackStartLineと同様に
        // 毎回組み立てる(Freeze可能な値なので描画コストは軽微)。全レーンに薄く重ねるため、
        // 設定色へ固定の半透明度を追加で適用する(アルファ自体は表示設定の対象外)。
        var lineColor = Color.FromArgb(0x90, CursorLineColor.R, CursorLineColor.G, CursorLineColor.B);
        var linePen = new Pen(Freeze(new SolidColorBrush(lineColor)), CursorLineWidth) { DashStyle = new DashStyle([4, 3], 0) };
        linePen.Freeze();
        dc.DrawLine(linePen, new Point(left, y), new Point(right, y));

        // カーソルが乗っているレーン(列)だけ、太い帯で強調する
        // (今クリックすると「ここ」に配置される、という対象レーンの明示)。
        var hoverCol = layout.ColumnAt(pos.X);
        if (hoverCol is not null)
        {
            var fillColor = Color.FromArgb(0x80, CursorHighlightColor.R, CursorHighlightColor.G, CursorHighlightColor.B);
            var borderPen = new Pen(Freeze(new SolidColorBrush(CursorHighlightColor)), 1.5);
            borderPen.Freeze();
            var rect = new Rect(hoverCol.X, y - CursorHighlightWidth / 2, hoverCol.Width, CursorHighlightWidth);
            dc.DrawRectangle(Freeze(new SolidColorBrush(fillColor)), borderPen, rect);
        }
    }

    /// <summary>StartNumber編集モードのスナップ用ガイド線(最大1本、波形=絶対フレームに固定)。
    /// ドラッグ終了時、最寄りの小節線が10px以内ならこの線に吸着する(2026-07-18)。</summary>
    private void DrawGuideLine(DrawingContext dc, ChartLayout layout, TimingEngine engine, double yTop, double yBottom)
    {
        if (!StartNumberEditMode || GuideFrame is not { } gf) return;
        double y = layout.TickToY(engine.FrameToTick(gf));
        if (y < yTop || y > yBottom) return;
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        dc.DrawLine(GuidePen, new Point(left, y), new Point(right, y));
    }

    /// <summary>ノートレーンと情報レーン(マーカー/小節/speed/boost/BPM)を背景色で区別する(2026-07-17)。
    /// 半透明なのは、将来ここに波形を重ねて表示する予定があるため。</summary>
    private static void DrawColumnBackgrounds(DrawingContext dc, ChartLayout layout, double yTop, double yBottom)
    {
        foreach (var col in layout.Columns)
        {
            var brush = col.Kind == ColumnKind.Note ? NoteLaneBackgroundBrush : InfoLaneBackgroundBrush;
            dc.DrawRectangle(brush, null, new Rect(col.X, yTop, col.Width, yBottom - yTop));
        }
    }

    private static void DrawColumnSeparators(DrawingContext dc, ChartLayout layout, double yTop, double yBottom)
    {
        foreach (var col in layout.Columns)
        {
            dc.DrawLine(new Pen(ColumnSepBrush, 1), new Point(col.X, yTop), new Point(col.X, yBottom));
        }
        var last = layout.Columns[^1];
        dc.DrawLine(new Pen(ColumnSepBrush, 1),
            new Point(last.X + last.Width, yTop), new Point(last.X + last.Width, yBottom));
    }

    /// <summary>フレーム基準グリッド(フレーム情報モード用、2026-07-17i)。表示密度に応じて
    /// 1/2/5/10/15/30/60/300/600フレーム刻みから、線間隔が約12px以上になる最小の刻みを選ぶ。
    /// 60の倍数(=秒単位)の線はやや強調する。</summary>
    private void DrawFrameGrid(DrawingContext dc, ChartLayout layout, TimingEngine engine, long tickMin, long tickMax, double left, double right)
    {
        double fMin = engine.TickToFrame(tickMin);
        double fMax = engine.TickToFrame(Math.Max(tickMax, tickMin + 1));
        if (fMax <= fMin) return;
        // 2026-07-22: Reverse時はtickMax側のYがtickMin側より小さくなるため絶対値を取る
        // (この値はグリッド間隔[px]の大きさのみに使うスカラー量で、各線のY自体は個別にTickToYで求める)。
        double pxPerFrame = Math.Abs((layout.TickToY(tickMax) - layout.TickToY(tickMin)) / (fMax - fMin));
        if (pxPerFrame <= 0) return;

        double[] steps = [1, 2, 5, 10, 15, 30, 60, 300, 600];
        double step = steps.FirstOrDefault(st => st * pxPerFrame >= 12, 600);

        for (double f = Math.Ceiling(fMin / step) * step; f <= fMax; f += step)
        {
            if (f < 0) continue;
            double y = layout.TickToY(engine.FrameToTick(f));
            bool major = Math.Abs(f % 60) < 0.001; // 秒単位
            dc.DrawLine(new Pen(major ? BeatLineBrush : GridLineBrush, major ? 1.25 : 1),
                new Point(left, y), new Point(right, y));
        }
    }

    private void DrawGridAndMeasureLines(DrawingContext dc, ChartLayout layout, TimingEngine engine, SnapService snap, long tickMin, long tickMax, long tickMinRaw)
    {
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        // 2026-08-08要望対応(再設計版): 「マイナスフレームを許容する」がONの間だけ、サブグリッド線・
        // 拍線をtick<0領域(tickMinRaw、0でクランプしていない生の下限)まで伸ばす。既定(OFF)では
        // 従来通りtickMin(0クランプ済み)で打ち切り、見た目を変えない。小節線はtick0起点で前方向へ
        // 数えるため元々小節番号の概念が無いtick<0には伸ばさない(measureGuardループはそのまま)。
        bool allowNegative = Document?.Project.AllowNegativeFramePlacement == true;
        long gridTickMin = allowNegative ? tickMinRaw : tickMin;

        // フレーム情報モード中(仕様書7.6、2026-07-17i)は拍・スナップグリッドの代わりに
        // フレーム基準のグリッド線を描く(小節線・拍子はそのまま=「動く側」の確認用)。
        if (Document?.IsFrameEditMode == true)
        {
            DrawFrameGrid(dc, layout, engine, tickMin, tickMax, left, right);
        }
        // スナップ細分線(subtle、仕様書6.5)。拍(48tick)と重なる位置は拍線で上書きされるのでそのままでよい。
        else if (snap.Enabled && snap.GridTicks > 0 && snap.GridTicks < TimingEngine.TicksPerBeat)
        {
            long g = snap.GridTicks;
            long gridStart = gridTickMin - (gridTickMin % g);
            for (long t = gridStart; t <= tickMax; t += g)
            {
                if ((t < 0 && !allowNegative) || t % TimingEngine.TicksPerBeat == 0) continue; // 拍線と重複させない
                double y = layout.TickToY(t);
                dc.DrawLine(new Pen(GridLineBrush, 1), new Point(left, y), new Point(right, y));
            }
        }

        // 拍線(medium、4分=48tick間隔)。フレーム情報モード中はフレームグリッドに譲る
        if (Document?.IsFrameEditMode != true)
        {
            long beatStart = gridTickMin - (gridTickMin % TimingEngine.TicksPerBeat);
            for (long t = beatStart; t <= tickMax; t += TimingEngine.TicksPerBeat)
            {
                if (t < 0 && !allowNegative) continue;
                double y = layout.TickToY(t);
                dc.DrawLine(new Pen(BeatLineBrush, 1), new Point(left, y), new Point(right, y));
            }
        }

        // 小節線(strong、仕様書7.5)。小節番号のテキスト表示は時間情報表示レーンへ移動済み
        // (2026-07-23、TBD 1-1)。DrawTimeInfoLaneが同じ小節境界の走査で描画する。
        int measureGuard = 0;
        long tick = 0;
        while (tick <= tickMax)
        {
            if (tick >= tickMin - 4L * TimingEngine.TicksPerBeat * 4)
            {
                double y = layout.TickToY(tick);
                dc.DrawLine(new Pen(MeasureLineBrush, 1.5), new Point(left, y), new Point(right, y));
            }
            var sig = engine.SignatureAt(tick);
            tick += sig.TicksPerMeasure;
            measureGuard++;
            if (measureGuard > 100000) break; // 安全弁(拍子破損時の無限ループ防止)
        }
    }

    /// <summary>Windows標準の警告アイコン(黄色三角+!)のWPF用ImageSource(2026-07-26、警告フラグ付き
    /// オブジェクトのオーバーレイ表示用)。SystemIcons.WarningをHIcon経由で変換し、初回のみ生成して使い回す。</summary>
    private static ImageSource? _warningIconCache;
    private static ImageSource WarningIcon
    {
        get
        {
            if (_warningIconCache is null)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    System.Drawing.SystemIcons.Warning.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _warningIconCache = src;
            }
            return _warningIconCache;
        }
    }

    /// <summary>警告フラグONのオブジェクトへ、通常描写の上にWindows標準警告アイコンを重ね描きする
    /// (2026-07-26、ユーザー確定仕様)。ノート中心(cx,y)の右上に重なるよう配置する。</summary>
    private static void DrawWarningOverlay(DrawingContext dc, double cx, double y, double noteSize)
    {
        double size = Math.Max(10, noteSize * 0.55);
        dc.DrawImage(WarningIcon, new Rect(cx, y - size, size, size));
    }

    /// <summary>色編集モードの「即時適用(ncolor_dataのAllFlag)」が指定されたオブジェクトを示す
    /// Windows標準の情報アイコンのWPF用ImageSource(2026-07-30要望対応)。ユーザーが直接ON/OFFする
    /// ものではなく、色編集モードの「即時適用にする」チェックボックスON中に塗った/一括塗りつぶした
    /// ncolor_dataエントリの内部フラグ(NColorEntry.AllFlag)をそのまま可視化するための表示専用アイコン。
    /// SystemIcons.InformationをHIcon経由で変換し、初回のみ生成して使い回す。</summary>
    private static ImageSource? _immediateApplyIconCache;
    private static ImageSource ImmediateApplyIcon
    {
        get
        {
            if (_immediateApplyIconCache is null)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    System.Drawing.SystemIcons.Information.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _immediateApplyIconCache = src;
            }
            return _immediateApplyIconCache;
        }
    }

    /// <summary>即時適用(AllFlag)ONのオブジェクトへ情報アイコンを重ね描きする(2026-07-30要望対応)。
    /// 警告アイコンと同時に表示されても重ならないよう、ノート中心の左上へ配置する。</summary>
    private static void DrawImmediateApplyOverlay(DrawingContext dc, double cx, double y, double noteSize)
    {
        double size = Math.Max(10, noteSize * 0.55);
        dc.DrawImage(ImmediateApplyIcon, new Rect(cx - size, y - size, size, size));
    }

    /// <summary>コメント記載お知らせ用(NoteAnnotation.ShowIcon)のWindows標準アイコンの
    /// WPF用ImageSource(2026-07-30要望対応)。Warning(SystemIcons.Warning)とは独立した、
    /// ユーザーがプロパティパネルのチェックボックスで任意にON/OFFできるお知らせアイコン。
    /// SystemIcons.ApplicationをHIcon経由で変換し、初回のみ生成して使い回す。</summary>
    private static ImageSource? _commentIconCache;
    private static ImageSource CommentIcon
    {
        get
        {
            if (_commentIconCache is null)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _commentIconCache = src;
            }
            return _commentIconCache;
        }
    }

    /// <summary>コメントお知らせフラグ(ShowIcon)ONのオブジェクトへアイコンを重ね描きする
    /// (2026-07-30要望対応)。警告アイコン・即時適用アイコンと同時に表示されても重ならないよう、
    /// ノート中心の右側(警告アイコンのさらに右)へ配置する。</summary>
    private static void DrawCommentIconOverlay(DrawingContext dc, double cx, double y, double noteSize)
    {
        double size = Math.Max(10, noteSize * 0.55);
        dc.DrawImage(CommentIcon, new Rect(cx + size, y - size, size, size));
    }

    /// <summary>共同編集: ノート所有者アイコン(設計メモ6.4節、2026-09-20実装)。指定色で塗った小さな丸を
    /// ノート(またはフリーズ始点)の左上コーナーへ重ね描きし、「誰がこのセルを置いたか」を一目で
    /// わかるようにする。警告/即時適用/コメントの各アイコン(ノート中心から1アイコン分離れた位置に
    /// 表示)とは違い、これはノート本体の左上コーナーぎりぎりに小さく添える(常時表示されうる情報のため、
    /// 他のノートやアイコンと重なりにくい最小限の主張にとどめている)。白い縁取りを1pxつけて、
    /// 背景やノート画像の色と被っても視認できるようにする。</summary>
    private static void DrawCollabOwnerOverlay(DrawingContext dc, double cx, double y, double noteSize, string ownerColorHex)
    {
        var color = TryParseColor(ownerColorHex, Colors.White);
        double radius = Math.Max(3, noteSize * 0.16);
        var center = new Point(cx - noteSize / 2 + radius, y - noteSize / 2 + radius);
        dc.DrawEllipse(Freeze(new SolidColorBrush(color)), new Pen(Brushes.White, 1), center, radius, radius);
    }

    /// <summary>タブリンク機能(2026-07-26要望対応)。リンク中の相手タブ(非アクティブタブ)のノート・
    /// フリーズを、本体のノート描画より奥に、固定色・縮小サイズで簡易表示する。2026-07-26b要望対応:
    /// ノートは(ベクター丸ではなく)各レーンのノート画像をLinkedNoteColorで着色して表示し
    /// (画像素材が無いレーンは従来通りベクターフォールバック)、フリーズは端点の画像表示に加えて
    /// 帯(胴体)も同じ色で半透明表示する。非アクティブタブの色設定(ncolor_data等)は一切反映せず、
    /// AppSettings由来の固定色で統一する(ユーザー確定仕様)。この描画はDrawingContextへの直接描画のみで、
    /// HitTest/Controller側のデータ構造には一切登録しないため、クリック・ドラッグ等の編集操作の
    /// 対象には絶対にならない。</summary>
    private void DrawLinkedBackgroundNotes(DrawingContext dc, ChartLayout layout, DifficultyTab tab, long tickMin, long tickMax)
    {
        if (Document is null || tab.LinkedTabId is not { } linkedId) return;
        var partner = Document.Project.Tabs.FirstOrDefault(t => t.TabId == linkedId);
        if (partner is null) return;

        double noteSize = layout.NoteSize * LinkedNoteSizeRatio;
        var noteBrush = Freeze(new SolidColorBrush(LinkedNoteColor));
        var highlightBrush = Freeze(new SolidColorBrush(LinkedHighlightColor));
        var bandBrush = Freeze(new SolidColorBrush(LinkedNoteColor) { Opacity = 0.5 });

        int laneCount = Math.Min(tab.Lanes.Count, partner.Lanes.Count);
        for (int i = 0; i < laneCount; i++)
        {
            var col = layout.NoteColumn(i);
            var laneDef = layout.Template.Lanes[i];
            var image = GetNoteImage(laneDef.NoteGraphic); // ./img/{noteGraphic}.png、無ければベクターフォールバック
            double cx = col.CenterX;
            double hw = col.Width * LinkedHighlightWidthRatio;

            void DrawMark(long tick)
            {
                if (tick < tickMin || tick > tickMax) return;
                double y = layout.TickToY(tick);
                dc.DrawRectangle(highlightBrush, null, new Rect(cx - hw / 2, y - LinkedHighlightHeight / 2, hw, LinkedHighlightHeight));
                if (image is not null)
                    DrawNoteImage(dc, image, laneDef, cx, y, noteSize, LinkedNoteColor);
                else
                    dc.DrawEllipse(noteBrush, null, new Point(cx, y), noteSize / 2, noteSize / 2);
            }

            foreach (var t in partner.Lanes[i].Notes) DrawMark(t);

            foreach (var f in partner.Lanes[i].Freezes)
            {
                if (f.EndTick < tickMin || f.StartTick > tickMax) continue;
                double y1 = layout.TickToY(f.StartTick), y2 = layout.TickToY(f.EndTick);
                // 2026-07-26d要望対応: 帯の横幅は「強調表示の幅比率」ではなく「ノートのサイズ比率」
                // (LinkedNoteSizeRatio、noteSizeに反映済み)に従う。本体側の帯幅(half/2、halfはノートサイズの半分)
                // と同じ比率関係を保つ(noteSize/2 = 本体のhalfに相当、その半分が帯幅)。
                double bandWidth = noteSize / 2;
                dc.DrawRectangle(bandBrush, null, new Rect(cx - bandWidth / 2, Math.Min(y1, y2), bandWidth, Math.Abs(y2 - y1)));
                DrawMark(f.StartTick);
                DrawMark(f.EndTick);
            }
        }
    }

    /// <summary>タブリンク機能のspeed/boost版(2026-08-08要望対応)。ノートのゴースト表示
    /// (DrawLinkedBackgroundNotes)と同様に、リンク中の相手タブのspeed_data/boost_data変化点を、
    /// 本体のタグ描画(DrawValueEvents)より奥に、LinkedNoteColor由来の固定色で簡易表示する。
    /// タグ形状自体は本体と同じDrawEventTagをそのまま再利用し(色のみ差し替え)、始点終点の
    /// オートスムージング線(DrawValueEventLinks)はここでは描かない(あくまで参照表示であり、
    /// 編集補助であるリンク線までは不要と判断)。ノート同様、DrawingContextへの直接描画のみで
    /// HitTest/Controller側のデータ構造には一切登録しないため、クリック等の操作対象にはならない。
    /// 2026-08-08b要望対応: 本体側のタグと見分けづらいとの指摘を受け、(1)マーカー(三角形)を50%半透明にし、
    /// (2)数値ラベルは同一tickの本体側ラベルと重ならないよう1行上へずらし(shiftLabelUp)、
    /// (3)半角丸括弧で囲って(相手タブの値であることを一目でわかるように)表示する。</summary>
    private void DrawLinkedBackgroundValueEvents(DrawingContext dc, ChartLayout layout, DifficultyTab tab, long tickMin, long tickMax)
    {
        if (Document is null || tab.LinkedTabId is not { } linkedId) return;
        var partner = Document.Project.Tabs.FirstOrDefault(t => t.TabId == linkedId);
        if (partner is null) return;

        var ghostBrush = Freeze(new SolidColorBrush(LinkedNoteColor) { Opacity = 0.5 });

        var speedCol = layout.Column(ColumnKind.Speed);
        foreach (var e in partner.SpeedEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, speedCol, layout.TickToY(e.Tick), ghostBrush, $"({e.Value:0.00})", pointLeft: true, layout.ZoomScale, shiftLabelUp: true);

        var boostCol = layout.Column(ColumnKind.Boost);
        foreach (var e in partner.BoostEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, boostCol, layout.TickToY(e.Tick), ghostBrush, $"({e.Value:0.00})", pointLeft: true, layout.ZoomScale, shiftLabelUp: true);
    }

    private void DrawNotesAndFreezes(DrawingContext dc, ChartLayout layout, DifficultyTab tab, ChartProject project, long tickMin, long tickMax)
    {
        // 強調グリッド用ブラシ(ShowNoteImages=false時のみ使用、レンダー1回につき1個を使い回す)
        var highlightBrush = Freeze(new SolidColorBrush(HighlightLineColor));
        // 2026-09-20: 共同編集セッション中のみ、ノート所有者アイコン(設計メモ6.4節)の問い合わせに使う
        // タブ番号を1回だけ求めておく(CollabSessionController側のAPIがtabIndexを要求するため)。
        int collabTabIndex = CollabSession is not null ? project.Tabs.IndexOf(tab) : -1;

        for (int i = 0; i < tab.Lanes.Count; i++)
        {
            var col = layout.NoteColumn(i);
            var laneDef = layout.Template.Lanes[i];
            var brush = LaneBrush(tab, project, laneDef.ColorGroup);
            double cx = col.CenterX;
            double half = layout.NoteSize / 2;
            var image = GetNoteImage(laneDef.NoteGraphic); // ./img/{noteGraphic}.png、無ければnull(ベクターへフォールバック)
            // 2026-07-25: 塗りつぶし色(ShadowColor編集モード)用の画像。本体側の素材命名に合わせ、
            // arrow.png使用レーンはarrowShadow.png、それ以外(onigiri/giko/iyo/c/monar/morara)は
            // 共通のaaShadow.pngを使う。素材が無ければ従来通り塗りつぶし色は表示に反映されない
            // (画像そのものを毎フレーム塗り潰す方式は負荷が高いため採用しない、2026-07-25ユーザー確定仕様)。
            var shadowImage = GetNoteImage(laneDef.NoteGraphic == "arrow" ? "arrowShadow" : "aaShadow");

            var (frzNoteColor, frzBandColor) = FrzColors(tab, project, laneDef.ColorGroup, brush);
            // 2026-07-23: 色編集モード(ncolor_data)で個別指定された色をtickで引けるようにする。
            var colorOverrides = tab.Lanes[i].ColorOverrides.ToDictionary(c => c.Tick);
            // 2026-07-26: 警告フラグONのオブジェクト(インポート時丸め処理等)のtick集合(警告アイコン重ね描き用)
            var warningTicks = tab.Lanes[i].Annotations.Count == 0
                ? null
                : tab.Lanes[i].Annotations.Where(a => a.Warning).Select(a => a.Tick).ToHashSet();
            // 2026-07-30要望対応: コメントお知らせアイコン(ShowIcon)ONのtick集合(警告とは別系統)
            var commentIconTicks = tab.Lanes[i].Annotations.Count == 0
                ? null
                : tab.Lanes[i].Annotations.Where(a => a.ShowIcon).Select(a => a.Tick).ToHashSet();

            // 2026-07-24: frzHitColor編集モード中は、判定中(ヒット時)の色をプレビュー表示する
            // (仕様: 対象色パラメータが変わり、譜面ビュー上のフリーズアローの表示色がヒット時設定の
            // ものになるようにしたい)。通常表示への影響を避けるため、この間だけ既定色・個別上書きの
            // 参照元をHit/HitBar系に差し替える(端点/帯の描画ロジック自体は変えない)。
            bool hitPreview = Controller is { ColorEditModeEnabled: true, SubMode: ColorEditSubMode.FrzHit };
            Color frzHitNoteColor = frzNoteColor, frzHitBandColor = frzBandColor;
            if (hitPreview)
            {
                var (hitHex, hitBarHex) = ColorDefaults.ResolveFrzHitColorsHex(tab, project,
                    ColorToHex(frzNoteColor), ColorToHex(frzBandColor));
                frzHitNoteColor = TryParseColor(hitHex, frzNoteColor);
                frzHitBandColor = TryParseColor(hitBarHex, frzBandColor);
            }

            // 2026-07-25: 塗りつぶし色(ArrowShadow/NormalShadow、frzHitColorプレビュー中はHitShadow)の
            // 既定色。HitShadowは専用ヘッダーが無いためNormalShadowの解決値へフォールバックする
            // (DosImporter/DosExporterと同じ簡略化、2026-07-24開示済み)。
            Color arrowShadowDefault = TryParseColor(ColorDefaults.ResolveShadowHex(project, laneDef.ColorGroup, "setShadowColor"), Colors.Black);
            Color normalShadowDefault = TryParseColor(ColorDefaults.ResolveShadowHex(project, laneDef.ColorGroup, "frzShadowColor"), Colors.Black);
            // 2026-07-26確定仕様: shadow画像はデフォルトOFF(setShadowColor/frzShadowColor等、色関係の
            // 指定が実際にある場合のみ利用する)。従来はshadow画像素材(arrowShadow.png等)が存在する限り
            // 常時描画しており、指定が無いのに既定の黒塗りが表示されてしまっていた。
            // ここでは「その他ヘッダー」でsetShadowColor/frzShadowColorが明示的に使用設定されているか
            // (=このレーン・タブ全体の既定として指定あり)を判定し、個別オブジェクトのncolor_data
            // ShadowColor/HitShadowColor指定(nOver/fOver側で判定)と合わせてOR条件で表示可否を決める。
            bool arrowShadowHeaderSpecified = HasNonEmptyHeader(project, "setShadowColor");
            bool normalShadowHeaderSpecified = HasNonEmptyHeader(project, "frzShadowColor");

            foreach (var f in tab.Lanes[i].Freezes)
            {
                if (f.EndTick < tickMin || f.StartTick > tickMax) continue;
                double y1 = layout.TickToY(f.StartTick);
                double y2 = layout.TickToY(f.EndTick);

                colorOverrides.TryGetValue(f.StartTick, out var fOver);
                Color edgeColor, bandColor, shadowColor;
                bool showFreezeShadow;
                if (hitPreview)
                {
                    edgeColor = fOver?.HitColor is { } hc ? ParseDisplayColor(hc, frzHitNoteColor) : frzHitNoteColor;
                    bandColor = fOver?.HitBarColor is { } hbc ? ParseDisplayColor(hbc, frzHitBandColor) : frzHitBandColor;
                    shadowColor = fOver?.HitShadowColor is { } hsc ? ParseDisplayColor(hsc, normalShadowDefault) : normalShadowDefault;
                    // 2026-07-26: HitShadowは専用ヘッダーが無いためfrzShadowColorの指定有無で判定する
                    showFreezeShadow = normalShadowHeaderSpecified || fOver?.HitShadowColor is not null;
                }
                else
                {
                    edgeColor = fOver?.Color is { } ec ? ParseDisplayColor(ec, frzNoteColor) : frzNoteColor;
                    bandColor = fOver?.BandColor is { } bc ? ParseDisplayColor(bc, frzBandColor) : frzBandColor;
                    shadowColor = fOver?.ShadowColor is { } sc ? ParseDisplayColor(sc, normalShadowDefault) : normalShadowDefault;
                    showFreezeShadow = normalShadowHeaderSpecified || fOver?.ShadowColor is not null;
                }

                // 帯(フリーズ胴体)はfrzColorのスロット[1]("帯(通常)")、またはncolor_data帯指定を使う(仕様書6.4.2)
                // 2026-07-22: Reverse時はy2<y1になりうるため、Rect構築はMin/Abs基準にする(順序非依存)。
                var bandBrush = Freeze(new SolidColorBrush(bandColor) { Opacity = 0.5 });
                dc.DrawRectangle(bandBrush, null, new Rect(cx - half / 2, Math.Min(y1, y2), half, Math.Abs(y2 - y1)));

                // 2026-07-16j: ShowNoteImages/ShowHighlightGridは独立トグルになったため、
                // 「画像 or ベクターフォールバック」を描いた上で、強調グリッドは条件を問わず追加で重ねて描く。
                if (ShowNoteImages && image is not null)
                {
                    // 2026-07-25: 塗りつぶし色の画像(あれば)を本体画像より先に描き、下地として重ねる
                    // (本体側の見た目に合わせ、塗りつぶしを背面レイヤーとして扱う)。
                    // 2026-07-26: 色関係の指定(frzShadowColor/ncolor_data)が実際にある場合のみ描画する。
                    if (shadowImage is not null && showFreezeShadow)
                    {
                        DrawNoteImage(dc, shadowImage, laneDef, cx, y1, layout.NoteSize, shadowColor);
                        DrawNoteImage(dc, shadowImage, laneDef, cx, y2, layout.NoteSize, shadowColor);
                    }
                    // 始点・終点ともレーンのノート画像を使う(2026-07-16h)。frzColor(またはncolor_data端点指定)の
                    // 色を乗算着色する(2026-07-16k: 画像表示時もfrzColorが反映されない不具合の対応)。
                    DrawNoteImage(dc, image, laneDef, cx, y1, layout.NoteSize, edgeColor);
                    DrawNoteImage(dc, image, laneDef, cx, y2, layout.NoteSize, edgeColor);
                }
                else if (ShowNoteImages)
                {
                    // 画像が無い場合のベクターフォールバック。色はfrzColorのスロット[0](またはncolor_data端点指定)
                    var noteBrush = Freeze(new SolidColorBrush(edgeColor));
                    dc.DrawEllipse(noteBrush, null, new Point(cx, y1), half / 2, half / 2);
                    dc.DrawEllipse(noteBrush, new Pen(Brushes.White, 1), new Point(cx, y2), half / 2, half / 2);
                }

                if (ShowHighlightGrid)
                {
                    // 強調グリッド: 始点・終点それぞれの位置に横棒を描く(2026-07-16h、2026-07-16jで独立トグル化)。
                    // 2026-07-26: 終点は密集時に非常に見づらいとの指摘対応で、設定でON/OFFできるようにした
                    // (既定は従来通り描画する=OFF)。
                    // 2026-08-02要望対応: UseNoteColorForHighlightがONの間、固定色(highlightBrush)の代わりに
                    // このフリーズの表示色(edgeColor、frzColor/ncolor_data由来)をそのまま使う。
                    var freezeHighlightBrush = UseNoteColorForHighlight ? Freeze(new SolidColorBrush(edgeColor)) : highlightBrush;
                    dc.DrawRectangle(freezeHighlightBrush, null, new Rect(col.X, y1 - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
                    if (!ExcludeFreezeEndFromHighlight)
                        dc.DrawRectangle(freezeHighlightBrush, null, new Rect(col.X, y2 - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
                }

                // 2026-07-26: 警告フラグON(StartTickで同定)のフリーズは始点側へ警告アイコンを重ねる
                if (warningTicks is not null && warningTicks.Contains(f.StartTick))
                    DrawWarningOverlay(dc, cx, y1, layout.NoteSize);
                // 2026-07-30要望対応: 即時適用(AllFlag)ONのncolor_dataエントリを持つフリーズは
                // 始点側へ情報アイコンを重ねる
                if (fOver?.AllFlag == true)
                    DrawImmediateApplyOverlay(dc, cx, y1, layout.NoteSize);
                // 2026-07-30要望対応: コメントお知らせフラグ(ShowIcon)ONのフリーズは始点側へアイコンを重ねる
                if (commentIconTicks is not null && commentIconTicks.Contains(f.StartTick))
                    DrawCommentIconOverlay(dc, cx, y1, layout.NoteSize);
                // 2026-09-20: 共同編集中、このフリーズを置いた参加者の識別色で所有者アイコンを重ねる
                if (CollabSession is not null)
                {
                    var freezeOwnerColor = CollabSession.GetFreezeOwnerColor(collabTabIndex, i, f.StartTick);
                    if (freezeOwnerColor is not null)
                        DrawCollabOwnerOverlay(dc, cx, y1, layout.NoteSize, freezeOwnerColor);
                }
            }

            foreach (var t in tab.Lanes[i].Notes)
            {
                if (t < tickMin || t > tickMax) continue;
                double y = layout.TickToY(t);
                colorOverrides.TryGetValue(t, out var nOver);
                var noteColor = nOver?.Color is { } nc ? ParseDisplayColor(nc, ((SolidColorBrush)brush).Color) : ((SolidColorBrush)brush).Color;
                var noteShadowColor = nOver?.ShadowColor is { } nsc ? ParseDisplayColor(nsc, arrowShadowDefault) : arrowShadowDefault;
                // 2026-07-26: setShadowColor/ncolor_dataの指定が実際にある場合のみshadow画像を表示する(既定OFF)。
                bool showNoteShadow = arrowShadowHeaderSpecified || nOver?.ShadowColor is not null;
                // 2026-07-16j: 独立トグル化。ノート画像(or ベクターフォールバック)と強調グリッドは
                // 排他ではなく、それぞれのフラグに応じて重ねて描く。
                if (ShowNoteImages)
                {
                    if (image is not null)
                    {
                        // 2026-07-25: 塗りつぶし色の画像(あれば)を本体画像より先に描く(フリーズと同じ扱い)。
                        if (shadowImage is not null && showNoteShadow)
                            DrawNoteImage(dc, shadowImage, laneDef, cx, y, layout.NoteSize, noteShadowColor);
                        // setColor(またはncolor_data指定)の色を乗算着色する(2026-07-16k: 画像表示時もsetColorが反映されない不具合の対応)。
                        DrawNoteImage(dc, image, laneDef, cx, y, layout.NoteSize, noteColor);
                    }
                    else
                        dc.DrawRectangle(Freeze(new SolidColorBrush(noteColor)), new Pen(Brushes.Black, 0.5), new Rect(cx - half, y - half, half * 2, half * 2));
                }

                if (ShowHighlightGrid)
                {
                    // ノート画像の有無に関わらず、レーン列の幅いっぱいに強調グリッド(横棒)を描く
                    // (2026-07-16b: 「ノート画像だけだとレーン上の位置がわかりづらい」対応
                    //  2026-07-16j: ノート画像ONでも併用できるよう独立トグル化)。
                    // HitTest/選択/EditActionsは既存のNote判定(座標ベース)をそのまま使うため無変更。
                    // 2026-08-02要望対応: UseNoteColorForHighlightがONの間、固定色(highlightBrush)の代わりに
                    // このノートの表示色(noteColor、setColor/ncolor_data由来)をそのまま使う。
                    var noteHighlightBrush = UseNoteColorForHighlight ? Freeze(new SolidColorBrush(noteColor)) : highlightBrush;
                    dc.DrawRectangle(noteHighlightBrush, null,
                        new Rect(col.X, y - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
                }

                // 2026-07-26: 警告フラグONのノートは通常描写の上に警告アイコンを重ねる
                if (warningTicks is not null && warningTicks.Contains(t))
                    DrawWarningOverlay(dc, cx, y, layout.NoteSize);
                // 2026-07-30要望対応: 即時適用(AllFlag)ONのncolor_dataエントリを持つノートは情報アイコンを重ねる
                if (nOver?.AllFlag == true)
                    DrawImmediateApplyOverlay(dc, cx, y, layout.NoteSize);
                // 2026-07-30要望対応: コメントお知らせフラグ(ShowIcon)ONのノートはアイコンを重ねる
                if (commentIconTicks is not null && commentIconTicks.Contains(t))
                    DrawCommentIconOverlay(dc, cx, y, layout.NoteSize);
                // 2026-09-20: 共同編集中、このノートを置いた参加者の識別色で所有者アイコンを重ねる
                if (CollabSession is not null)
                {
                    var noteOwnerColor = CollabSession.GetNoteOwnerColor(collabTabIndex, i, t);
                    if (noteOwnerColor is not null)
                        DrawCollabOwnerOverlay(dc, cx, y, layout.NoteSize, noteOwnerColor);
                }
            }
        }
    }

    /// <summary>
    /// 画像1枚でのノート描画(仕様書2.1)。arrow.pngのみレーンのrotationAngleで回転させ、
    /// それ以外(onigiri/giko/iyo/c/monar/morara)は固定向きの専用画像なので回転しない
    /// (danoniplus本体のstepRtnが数値角度か固定キャラ名かで分岐するのと同じ扱い)。
    /// </summary>
    internal static void DrawNoteImage(DrawingContext dc, BitmapImage image, LaneDef laneDef, double cx, double y, double noteSize, Color? tint = null) // 2026-07-17g: internal化(同上)
    {
        // 2026-07-16k: tint指定時はsetColor/frzColor由来の色を乗算合成した画像を描く(素材そのままではなく)。
        ImageSource src = tint is { } t ? GetTintedNoteImage(image, t) : image;
        var rect = new Rect(cx - noteSize / 2, y - noteSize / 2, noteSize, noteSize);
        bool rotate = laneDef.NoteGraphic == "arrow" && laneDef.RotationAngle != 0;
        if (!rotate)
        {
            dc.DrawImage(src, rect);
            return;
        }
        dc.PushTransform(new RotateTransform(laneDef.RotationAngle, cx, y));
        dc.DrawImage(src, rect);
        dc.Pop();
    }

    // =====================================================================
    // テンプレート編集/マクロ編集ウィンドウ共通のレーンプレビュー描画(2026-07-26)。
    // 実際の譜面ビュー描画(DrawNoteImage、rotationAngle反映込み)をそのまま再利用し、
    // 色はcolorGroupに応じて要望の見本色(setColor=#9999ff,#ccffff,#ffffff,#ffff99,#ff9966)を
    // 循環で割り当てる(実際のsetColor設定とは無関係な、編集時の見分け用サンプル色)。
    // =====================================================================

    private static readonly Color[] PreviewSampleColors =
    [
        (Color)ColorConverter.ConvertFromString("#9999ff")!,
        (Color)ColorConverter.ConvertFromString("#ccffff")!,
        (Color)ColorConverter.ConvertFromString("#ffffff")!,
        (Color)ColorConverter.ConvertFromString("#ffff99")!,
        (Color)ColorConverter.ConvertFromString("#ff9966")!,
    ];

    internal static Color PreviewSampleColorForGroup(int colorGroup) =>
        PreviewSampleColors[((colorGroup % PreviewSampleColors.Length) + PreviewSampleColors.Length) % PreviewSampleColors.Length];

    /// <summary>1レーン分のプレビューアイコンを描く(画像が無ければ塗り矩形にフォールバック)。</summary>
    internal static void DrawLaneIcon(DrawingContext dc, LaneDef laneDef, double cx, double cy, double size, Color tint)
    {
        var image = GetNoteImage(laneDef.NoteGraphic);
        if (image is not null)
            DrawNoteImage(dc, image, laneDef, cx, cy, size, tint);
        else
            dc.DrawRectangle(Freeze(new SolidColorBrush(tint)), new Pen(Brushes.Gray, 0.5), // 2026-07-26: 背景が黒のため視認性を優先
                new Rect(cx - size / 2, cy - size / 2, size, size));
    }

    /// <summary>
    /// レーン色。tab自身のSetColorOverrideが無ければ、1タブ目(=共通値の実体、仕様書6.4.2)の値に
    /// フォールバックする(「全ての難易度で共通」チェックON時、②タブのプレビューと表示を一致させるため、
    /// 2026-07-16f修正。以前はここでいきなり本クラス既定色に落ちてしまい、共通値が反映されなかった)。
    /// </summary>
    internal static Brush LaneBrush(DifficultyTab tab, ChartProject project, int colorGroup) // 2026-07-17g: internal化(同上)
    {
        // 2026-07-24: 既定色解決ロジックはCore側のColorDefaultsへ集約(DosExporter/DosImporterの
        // ncolor_data永続化対応と表示を一致させるため、算出方法を1箇所に統一した)。
        return BrushOf(ColorDefaults.ResolveSetColorHex(tab, project, colorGroup));
    }

    /// <summary>
    /// フリーズの表示色(仕様書6.4.2 frzColor)。2026-07-26確定仕様: 色グループ数に関わらず常に4スロット
    /// [0]始点終点(通常) [1]帯(通常) [2]始点終点(判定中) [3]帯(判定中) の1セットのみ(danoniplus本体の
    /// 仕様通り。従来の「色グループごとに4スロット」実装は誤りだった)。tab自身のFrzColorOverrideが
    /// 無ければ1タブ目(共通値の実体)へ、さらに個々のスロットが空欄ならこのレーンのsetColorの値へ
    /// 自動補完する(仕様書6.4.2の自動補完ルール)。colorGroupはこのレーンのsetColorフォールバック値を
    /// 求めるためだけに使う(frzColor自体のスロット選択には使わない)。
    /// </summary>
    internal static (Color NoteColor, Color BandColor) FrzColors(DifficultyTab tab, ChartProject project, int colorGroup, Brush setColorBrush) // 2026-07-17g: internal化(同上)
    {
        // 2026-07-24: 既定色解決ロジックはCore側のColorDefaultsへ集約(LaneBrushと同じ理由)。
        var fallback = ((SolidColorBrush)setColorBrush).Color;
        var (normalHex, barHex) = ColorDefaults.ResolveFrzColorsHex(tab, project,
            ColorDefaults.ResolveSetColorHex(tab, project, colorGroup));
        return (TryParseColor(normalHex, fallback), TryParseColor(barHex, fallback));
    }

    private static Color TryParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    /// <summary>「その他ヘッダー」でheaderKey(setShadowColor/frzShadowColor等)が実際に使用設定されている
    /// (=空でない値が入っている)かどうか(2026-07-26、shadow画像デフォルトOFF対応)。</summary>
    private static bool HasNonEmptyHeader(ChartProject project, string headerKey) =>
        project.ExtraHeaders.TryGetValue(headerKey, out var v) && !string.IsNullOrWhiteSpace(v);

    /// <summary>frzHitColorプレビュー用にResolveFrzHitColorsHex(hex文字列ベース)へ渡すためのColor→hex変換
    /// (2026-07-24)。DrawNotesAndFreezesが既に解決済みのColorしか持たないため、Core側のColorDefaultsを
    /// 再利用するにはここで一度hexへ戻す必要がある。</summary>
    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>ncolor_data用ColorCode(単色/グラデーション記法)の譜面ビュー表示用近似色を求める
    /// (2026-07-23)。譜面ビューは編集用プレビューでありグラデーション自体は描画しないため、
    /// 先頭の色区間(@種類指定・先頭のグラデーション方向指定を除いた最初の色)だけを単色として使う。
    /// "0xRRGGBB"表記(dos.txt由来)にも対応する。</summary>
    internal static Color ParseDisplayColor(string code, Color fallback)
    {
        var head = code.Split('@', 2)[0].Split(':', StringSplitOptions.TrimEntries)[0];
        // linear-gradientの方向指定("45deg"/"to right"等)が先頭に来ている場合はスキップして次の区間を見る
        if (head.Length > 0 && !head.StartsWith('#') && !head.StartsWith("0x") && !head.StartsWith("0X")
            && !char.IsLetter(head, 0))
        {
            var parts = code.Split('@', 2)[0].Split(':', StringSplitOptions.TrimEntries);
            head = parts.Length > 1 ? parts[1] : head;
        }
        if (head.Length > 2 && head[0] == '0' && (head[1] is 'x' or 'X')) head = "#" + head[2..];
        return TryParseColor(head, fallback);
    }

    /// <summary>ncolor_dataの即時適用(AllFlag)を、プレイテスト/プレビューのライブ再生描画へ
    /// 簡易的に反映するための近似解決(2026-07-30要望対応)。本家は「指定フレーム時点で既に
    /// 出現済みの矢印/フリーズも含めて即座に塗り替える」挙動だが、本エディタは出現(スポーン)
    /// フレームの厳密な計算(スクロール速度からの逆算)までは行わない近似実装とする(将来、
    /// ユーザーから違和感の指摘があれば改めて精緻化する方針)。
    /// 近似ルール: 対象オブジェクト自身のtickにまだ到達していない(=まだ画面上に存在しうる)間、
    /// 自分より前のtickに置かれた即時適用エントリのうち、既に発火済み(そのtickに対応するframeが
    /// currentFrame以下)で最もtickが新しいものの値を優先して採用する。該当が無ければ、
    /// 通常通りbaseColor(自分自身のColorOverride、無ければレーン既定色)をそのまま使う。</summary>
    internal static string? ResolveImmediateAppliedColor(
        IEnumerable<NColorEntry> laneOverrides, long ownTick, double currentFrame,
        Func<NColorEntry, string?> fieldPicker, Func<long, double> tickToFrame, string? baseColor)
    {
        NColorEntry? latest = null;
        foreach (var e in laneOverrides)
        {
            if (e.Tick >= ownTick || !e.AllFlag) continue;
            if (fieldPicker(e) is null) continue;
            if (tickToFrame(e.Tick) > currentFrame) continue;
            if (latest is null || e.Tick > latest.Tick) latest = e;
        }
        return latest is not null && currentFrame < tickToFrame(ownTick) ? fieldPicker(latest) : baseColor;
    }

    private static void DrawValueEvents(DrawingContext dc, ChartLayout layout, DifficultyTab tab, ChartProject project, long tickMin, long tickMax)
    {
        var speedCol = layout.Column(ColumnKind.Speed);
        DrawValueEventLinks(dc, layout, speedCol, tab.SpeedEvents, SpeedBrush, tickMin, tickMax);
        foreach (var e in tab.SpeedEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, speedCol, layout.TickToY(e.Tick), SpeedBrush, e.Value.ToString("0.00"), pointLeft: true, layout.ZoomScale);

        var boostCol = layout.Column(ColumnKind.Boost);
        DrawValueEventLinks(dc, layout, boostCol, tab.BoostEvents, BoostBrush, tickMin, tickMax);
        foreach (var e in tab.BoostEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, boostCol, layout.TickToY(e.Tick), BoostBrush, e.Value.ToString("0.00"), pointLeft: true, layout.ZoomScale);

        var bpmCol = layout.Column(ColumnKind.Bpm);
        DrawBpmEventLinks(dc, layout, bpmCol, project.BpmEvents, BpmBrush, tickMin, tickMax);
        foreach (var e in project.BpmEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, bpmCol, layout.TickToY(e.Tick), BpmBrush, e.Bpm.ToString("0.##"), pointLeft: true, layout.ZoomScale);
    }

    /// <summary>2026-07-30要望対応: speed/boostの「始点終点オートスムージング出力」の可視化。
    /// LinkGridDivisionが設定されたイベントと、その直後の同種イベントとの間に、値を表す直線を描く。
    /// 値→X座標は「レーン左端+5px=0.0、中央=1.0、右端-5px=2.0」の線形マッピングで、2.0を超える値・
    /// 0.0を下回る値はいずれも振り切れ表示をせず、その端(2.0相当/0.0相当)の座標でキャップする
    /// (ユーザー確定仕様)。TickToYはtickに対して線形のため、tick順で線形補間した値もこの直線一本で
    /// 正しく表現できる(中間点を個別に描く必要は無い)。</summary>
    private static void DrawValueEventLinks(DrawingContext dc, ChartLayout layout, ColumnInfo col,
        List<ValueEvent> events, Brush brush, long tickMin, long tickMax)
    {
        if (events.Count < 2) return;
        var sorted = events.OrderBy(e => e.Tick).ToList();
        var linkPen = new Pen(brush, 2.0);

        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            if (sorted[i].LinkGridDivision is null) continue;
            var a = sorted[i];
            var b = sorted[i + 1];
            if (b.Tick < tickMin || a.Tick > tickMax) continue;

            var p1 = new Point(ValueToLinkX(col, a.Value), layout.TickToY(a.Tick));
            var p2 = new Point(ValueToLinkX(col, b.Value), layout.TickToY(b.Tick));
            dc.DrawLine(linkPen, p1, p2);
        }
    }

    private static double ValueToLinkX(ColumnInfo col, double value)
    {
        double v = Math.Clamp(value, 0.0, 2.0);
        double left = col.X + 5;
        double right = col.X + col.Width - 5;
        return left + (right - left) * (v / 2.0);
    }

    /// <summary>2026-08-23要望対応: BPMの「始点終点リンク(直線ランプ)」の可視化。
    /// speed/boostのDrawValueEventLinksと異なり、BPM列は値(BPM)をX座標へマッピングする軸を
    /// 持たない(BpmEventのタグは常に列中央に表示される、値の範囲がspeed/boostの0.0〜2.0のような
    /// 決まった幅を持たないため)。そのため、リンク区間は列中央を通る縦の直線で表現する
    /// (「この2点はランプでつながっている」ことだけを示す、値の大小は直線の傾きでは表現しない)。</summary>
    private static void DrawBpmEventLinks(DrawingContext dc, ChartLayout layout, ColumnInfo col,
        List<BpmEvent> events, Brush brush, long tickMin, long tickMax)
    {
        if (events.Count < 2) return;
        var sorted = events.OrderBy(e => e.Tick).ToList();
        var linkPen = new Pen(brush, 2.0);
        double x = col.X + col.Width / 2;

        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            if (sorted[i].LinkGridDivision is null) continue;
            var a = sorted[i];
            var b = sorted[i + 1];
            if (b.Tick < tickMin || a.Tick > tickMax) continue;

            var p1 = new Point(x, layout.TickToY(a.Tick));
            var p2 = new Point(x, layout.TickToY(b.Tick));
            dc.DrawLine(linkPen, p1, p2);
        }
    }

    /// <summary>マーカーコメントの表示方式(仕様書7.4: 全文/先頭数文字、環境設定から適用、2026-07-19b)</summary>
    public bool MarkerCommentFull { get; set; } = true;
    public int MarkerCommentHeadChars { get; set; } = 4;

    /// <summary>時間情報レーン/マーカーレーンの基準フォントサイズ(2026-07-26、環境設定から適用)。
    /// ZoomScale=1.0時のptサイズで、実描画時はZoomScaleを掛けて最終サイズを求める(既定値は
    /// 変更前の固定値8/9を踏襲)。</summary>
    public double TimeInfoFontSize { get; set; } = 8.0;
    public double MarkerFontSize { get; set; } = 9.0;

    /// <summary>時間情報表示レーン(2026-07-23、TBD 1-1)。マーカーレーンのさらに左側に置く表示専用レーン。
    /// 小節の頭には「小節番号・frame・time」の3行、ノート配置frame(小節頭を除く)には「frame」の1行を表示する。
    /// クリック等の編集操作は持たない(ダブルクリックの再生開始位置指定のみSmartToolController側で対応済み)。</summary>
    private void DrawTimeInfoLane(DrawingContext dc, ChartLayout layout, DifficultyTab tab, TimingEngine engine, long tickMin, long tickMax)
    {
        var col = layout.Column(ColumnKind.TimeInfo);
        double x = col.X + 2;
        double fontSize = Math.Max(6, TimeInfoFontSize * layout.ZoomScale);

        // 小節の頭: 小節番号・frame・time(既存の小節線描画と同じ走査ロジック、2026-07-23に小節線側から移設)。
        // 2026-07-23追記: ズームアウト等で隣の小節との間隔が3行ぶんの高さを下回る(＝行が重なる)場合は、
        // 自動的に小節番号のみの1行表示へ切り替える(ユーザー確定仕様: 「小節番号くらいなら重なっても良い」)。
        double lineH = fontSize + 1;
        double requiredHeight = lineH * 3;
        var measureHeadTicks = new HashSet<long>();
        int measure = 0;
        long mTick = 0;
        int measureGuard = 0;
        double? prevY = null;
        while (mTick <= tickMax)
        {
            measureHeadTicks.Add(mTick);
            double y = layout.TickToY(mTick);
            if (mTick >= tickMin - 4L * TimingEngine.TicksPerBeat * 4)
            {
                double frame = ToDisplayFrame(engine.TickToFrame(mTick));
                bool crowded = prevY is double py && Math.Abs(y - py) < requiredHeight;
                if (crowded)
                    DrawTimeInfoText(dc, x, y, fontSize, Brushes.White, $"#{measure}");
                else
                    DrawTimeInfoText(dc, x, y, fontSize, Brushes.White,
                        $"{frame / 60.0:0.00}s", $"{frame:0.#}f", $"#{measure}");
            }
            prevY = y;
            var sig = engine.SignatureAt(mTick);
            mTick += sig.TicksPerMeasure;
            measure++;
            measureGuard++;
            if (measureGuard > 100000) break; // 安全弁(拍子破損時の無限ループ防止)
        }

        // ノートが置かれているframe(小節頭と重複するものは上で表示済みのため除外)
        var noteTicks = new HashSet<long>();
        foreach (var lane in tab.Lanes)
        {
            foreach (var t in lane.Notes)
                if (t >= tickMin && t <= tickMax) noteTicks.Add(t);
            foreach (var f in lane.Freezes)
            {
                if (f.StartTick >= tickMin && f.StartTick <= tickMax) noteTicks.Add(f.StartTick);
                if (f.EndTick >= tickMin && f.EndTick <= tickMax) noteTicks.Add(f.EndTick);
            }
        }
        foreach (var t in noteTicks)
        {
            if (measureHeadTicks.Contains(t)) continue;
            double y = layout.TickToY(t);
            double frame = ToDisplayFrame(engine.TickToFrame(t));
            DrawTimeInfoText(dc, x, y, fontSize, Brushes.LightGray, $"{frame:0.#}f");
        }
    }

    /// <summary>時間情報表示レーンのテキストを、基準線(y)のすぐ上を起点に下から積み上げて描画する
    /// (linesBottomToTop[0]がyに最も近い行)。</summary>
    private void DrawTimeInfoText(DrawingContext dc, double x, double y, double fontSize, Brush brush, params string[] linesBottomToTop)
    {
        double lineH = fontSize + 1;
        double lineY = y - lineH - 1;
        foreach (var line in linesBottomToTop)
        {
            var ft = new FormattedText(line, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface, fontSize, brush, 1.0);
            dc.DrawText(ft, new Point(x, lineY));
            lineY -= lineH;
        }
    }

    private static string WordLaneLabel(WordLane lane) => lane.IsReverse ? $"{lane.Name}(Rev)" : lane.Name;

    /// <summary>歌詞レーン(WordLanes、2026-07-23、TBD 4)のエントリをタグとして描画する。
    /// 制御行([fadein]等)はそのままのキーワードを、通常歌詞は本文(長ければ省略)を表示する。</summary>
    private void DrawWordEntries(DrawingContext dc, ChartLayout layout, DifficultyTab tab, long tickMin, long tickMax)
    {
        for (int i = 0; i < tab.WordLanes.Count; i++)
        {
            var col = layout.WordColumn(i);
            foreach (var w in tab.WordLanes[i].Entries)
            {
                if (w.Tick < tickMin || w.Tick > tickMax) continue;
                string label = w.Kind switch
                {
                    WordEntryKind.Control => w.Text,
                    WordEntryKind.Comment => $"//{w.Text}",
                    _ => w.Text.Length <= 6 ? w.Text : w.Text[..6] + "…",
                };
                var brush = w.Kind == WordEntryKind.Control ? WordControlBrush : WordLyricsBrush;
                DrawEventTag(dc, col, layout.TickToY(w.Tick), brush, label, pointLeft: false, layout.ZoomScale);
            }
        }
    }

    private void DrawMarkers(DrawingContext dc, ChartLayout layout, ChartProject project, long tickMin, long tickMax)
    {
        var col = layout.Column(ColumnKind.Marker);
        foreach (var m in project.Markers)
        {
            if (m.Tick < tickMin || m.Tick > tickMax) continue;
            var label = MarkerCommentFull || m.Comment.Length <= MarkerCommentHeadChars
                ? m.Comment
                : m.Comment[..MarkerCommentHeadChars] + "…";
            DrawEventTag(dc, col, layout.TickToY(m.Tick), MarkerBrush, label, pointLeft: false, layout.ZoomScale, MarkerFontSize);
        }
    }

    private static void DrawTimeSignatures(DrawingContext dc, ChartLayout layout, ChartProject project, TimingEngine engine, long tickMin, long tickMax)
    {
        var col = layout.Column(ColumnKind.Measure);
        foreach (var s in project.TimeSignatures)
        {
            long tick = engine.MeasureStartTick(s.MeasureIndex);
            if (tick < tickMin || tick > tickMax) continue;
            DrawEventTag(dc, col, layout.TickToY(tick), TimeSigBrush, $"{s.Numerator}/{s.Denominator}", pointLeft: true, layout.ZoomScale, mirrorShape: true);
        }
    }

    /// <summary>
    /// 譜面ビュー上部(Reverse時は下部)に常時固定表示するレーンラベル(2026-07-22)。
    /// 情報レーン(マーカー/拍子/speed/boost/BPM)は単語1つ、ノートレーンは実キー(1行目、KeyAssignLabel)+
    /// キーボードモード中のみ入力キー(2行目、KeyboardInputKeysLabel、水色で区別)を表示する。
    /// 固定ヘッダーのXAML要素は作らず、viewport(スクロール位置)に追従してOnRenderのたびに
    /// その位置へ描き直す方式(ChartCanvas全体が1枚のCanvasで、ScrollViewerが外側にあるため)。
    /// </summary>
    /// <summary>2026-07-26: レーンラベル欄のノート数表示(要望対応)。マウスモード中はラベルの次の行に、
    /// キーボードモード中(既に2行使用中)はレーンラベル(1行目)をノート数表示に置き換える。
    /// 2026-08-01要望対応: 「通常ノート数/フリーズ数」の形で表示し、通常ノートをオレンジ、
    /// フリーズを青、区切りのスラッシュを白で色分けする(DrawNoteCountTextを参照)。</summary>
    private static readonly Brush NormalNoteCountBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)));
    private static readonly Brush FreezeNoteCountBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5)));

    /// <summary>レーンラベルバー(speed/boost/BPM等の見出し)の高さ(px)を計算する。
    /// 2026-08-08: バーをScrollViewer外の専用領域(LaneHeaderBar)へ分離したことに伴い、
    /// LaneHeaderBar.MeasureOverrideが自身の必要高さを求めるために呼ぶ(公開化)。</summary>
    public double MeasureLaneLabelBarHeight()
    {
        if (Document is null) return 0;
        double fontSize = Math.Max(7, 9 * Document.CurrentLayout.ZoomScale);
        double lineH = fontSize + 3;
        bool twoLines = KeyboardModeActive || ShowLaneNoteCount;
        return (twoLines ? lineH * 2 : lineH) + 6;
    }

    /// <summary>レーンラベルバーの中身を描画する(旧DrawLaneLabels、2026-08-08にLaneHeaderBar用へ改称・公開化)。
    /// バー自体はもはやオーバーレイではなく専用領域に描かれるため、呼び出し側(LaneHeaderBar.OnRender)が
    /// 高さぴったりの領域を用意している前提でbarTop=0固定とする。横スクロールに追従させるため、
    /// 呼び出し側は事前にTranslateTransform(-水平オフセット, 0)を適用したうえで、left/widthには
    /// (水平オフセット, 見えている幅)をそのまま渡すこと(列のX座標は譜面本体と共通の絶対座標)。</summary>
    internal void PaintLaneLabelBar(DrawingContext dc, double left, double width)
    {
        if (Document is null) return;
        var layout = Document.CurrentLayout;
        var template = Document.CurrentTemplate;
        var tab = Document.CurrentTab;
        double fontSize = Math.Max(7, 9 * layout.ZoomScale);
        double lineH = fontSize + 3;
        bool twoLines = KeyboardModeActive || ShowLaneNoteCount;
        double barHeight = (twoLines ? lineH * 2 : lineH) + 6;
        double barTop = 0;

        dc.DrawRectangle(LaneLabelBackgroundBrush, null, new Rect(left, barTop, width, barHeight));

        foreach (var col in layout.Columns)
        {
            int normalCount = 0, freezeCount = 0;
            string? noteCountText = null;
            if (col.Kind == ColumnKind.Note && ShowLaneNoteCount)
            {
                var lane = tab.Lanes[col.NoteLaneIndex];
                normalCount = lane.Notes.Count;
                freezeCount = lane.Freezes.Count;
                // 2026-08-01要望対応: 「通常/フリーズ」を合算せず分けて表示する。実際の色分け(オレンジ/青、
                // スラッシュは白)はDrawNoteCountTextが行うため、ここでの文字列は表示有無の判定用。
                noteCountText = $"{normalCount}/{freezeCount}";
            }

            string? line1 = col.Kind switch
            {
                ColumnKind.TimeInfo => "時間情報",
                ColumnKind.Marker => "マーカー",
                ColumnKind.Measure => "拍子",
                ColumnKind.Speed => "speed",
                ColumnKind.Boost => "boost",
                ColumnKind.Bpm => "BPM",
                ColumnKind.Note => (KeyboardModeActive && ShowLaneNoteCount) ? noteCountText
                    : ShowLaneNameLabel ? template.Lanes[col.NoteLaneIndex].LaneId
                    : template.Lanes[col.NoteLaneIndex].KeyAssignLabel,
                ColumnKind.Word => WordLaneLabel(tab.WordLanes[col.NoteLaneIndex]),
                _ => null,
            };
            if (string.IsNullOrEmpty(line1)) continue;

            bool line1IsNoteCount = col.Kind == ColumnKind.Note && KeyboardModeActive && ShowLaneNoteCount;
            if (line1IsNoteCount)
                DrawNoteCountText(dc, col.CenterX, barTop + 3, normalCount, freezeCount, fontSize);
            else
                DrawLaneLabelText(dc, col.CenterX, barTop + 3, line1, fontSize, Brushes.White, col.Width);

            if (col.Kind == ColumnKind.Note && KeyboardModeActive)
            {
                var line2 = template.Lanes[col.NoteLaneIndex].KeyboardInputKeysLabel;
                if (!string.IsNullOrEmpty(line2))
                    DrawLaneLabelText(dc, col.CenterX, barTop + 3 + lineH, line2, fontSize, KeyboardInputKeyBrush, col.Width);
            }
            else if (col.Kind == ColumnKind.Note && ShowLaneNoteCount && noteCountText is not null)
            {
                DrawNoteCountText(dc, col.CenterX, barTop + 3 + lineH, normalCount, freezeCount, fontSize);
            }
        }
    }

    /// <summary>maxWidth省略時は従来通り縮小なしで描画する。指定時、ラベル幅がmaxWidthを超えると
    /// フォントサイズを縮小して収める(2026-08-02要望対応: 同じレーンに複数キーをアサインした場合
    /// ("E/R"等)にラベルが隣接レーンへはみ出して重なり、読みづらいとの指摘への対応)。
    /// 文字幅はフォントサイズにほぼ比例するため、比率から縮小後サイズを一発で計算し直す
    /// (反復ループは行わない、最小6ptまで)。</summary>
    private static void DrawLaneLabelText(DrawingContext dc, double centerX, double top, string text, double fontSize, Brush brush, double? maxWidth = null)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface, fontSize, brush, 1.0);
        if (maxWidth is { } w && ft.Width > w)
        {
            double shrunkSize = Math.Max(6, fontSize * (w / ft.Width));
            ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface, shrunkSize, brush, 1.0);
        }
        dc.DrawText(ft, new Point(centerX - ft.Width / 2, top));
    }

    /// <summary>2026-08-01要望対応: レーンのノート数を「通常ノート数/フリーズ数」の形式で、
    /// 通常ノートをオレンジ(NormalNoteCountBrush)、フリーズを青(FreezeNoteCountBrush)、
    /// 区切りのスラッシュを白で色分け表示する。1つのFormattedTextに範囲指定でブラシを
    /// 適用する(SetForegroundBrush)ことで、DrawText1回の呼び出しのまま3色を混在させている。</summary>
    private static void DrawNoteCountText(DrawingContext dc, double centerX, double top, int normalCount, int freezeCount, double fontSize)
    {
        string normalText = normalCount.ToString();
        string freezeText = freezeCount.ToString();
        string text = $"{normalText}/{freezeText}";
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface, fontSize, Brushes.White, 1.0);
        ft.SetForegroundBrush(NormalNoteCountBrush, 0, normalText.Length);
        ft.SetForegroundBrush(FreezeNoteCountBrush, normalText.Length + 1, freezeText.Length);
        dc.DrawText(ft, new Point(centerX - ft.Width / 2, top));
    }

    /// <summary>
    /// タグ形状(仕様書6.6): 左向き=speed/boost/BPM/拍子、右向き=マーカー。
    /// ラベル文字サイズはzoomScale(Ctrl+スクロール、ChartLayout.ZoomScale)に連動させる(2026-07-16h:
    /// 4Kモニター等でズームしても文字サイズが変わらず見づらいとの指摘対応)。
    /// マーカー(pointLeft:false)のラベルは、以前は常にcol右側に描画しており隣接カラム(小節レーン)へ
    /// はみ出していたバグを修正: マーカーレーン自身の幅にクリップして収める(2026-07-16h)。
    /// </summary>
    /// <summary>fontSizeBase=ZoomScale=1.0時の基準フォントサイズ(pt)。マーカータグは環境設定の
    /// MarkerFontSizeを渡す(2026-07-26)。それ以外(speed/boost/BPM/歌詞)は従来通り既定値9を使う。</summary>
    /// <param name="shiftLabelUp">2026-08-08要望対応: タブリンク相手データの数値表示専用。trueの場合、
    /// 数値ラベルの描画位置だけを1行分(フォントサイズ相当)上へずらす(タグ本体の位置・形状は変えない)。
    /// 同一tickに本体側の数値表示があっても重なって読めなくなるのを防ぐため。</param>
    private static void DrawEventTag(DrawingContext dc, ColumnInfo col, double y, Brush brush, string label, bool pointLeft, double zoomScale, double fontSizeBase = 9, bool mirrorShape = false, bool shiftLabelUp = false)
    {
        const double w = 20, h = 9;
        double cx = col.CenterX;
        // mirrorShape: 三角形の向きだけをpointLeftと逆にする(ラベルの表示側はpointLeft側の従来通りを維持、
        // 2026-07-26要望対応: 拍子マーカーの向きを左右反転)。
        bool triangleLeft = mirrorShape ? !pointLeft : pointLeft;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (triangleLeft)
            {
                ctx.BeginFigure(new Point(cx - w / 2, y), true, true);
                ctx.LineTo(new Point(cx + w / 2, y - h), true, false);
                ctx.LineTo(new Point(cx + w / 2, y + h), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(cx + w / 2, y), true, true);
                ctx.LineTo(new Point(cx - w / 2, y - h), true, false);
                ctx.LineTo(new Point(cx - w / 2, y + h), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(brush, new Pen(Brushes.Black, 0.5), geo);

        if (!string.IsNullOrEmpty(label))
        {
            double fontSize = Math.Max(7, fontSizeBase * zoomScale);
            // shiftLabelUp時は数値ラベルのY座標のみ1行分(フォントサイズ相当)上へずらす。
            // タグ本体(三角形、上のDrawGeometry)はyのまま描画済みなので、tick位置の正確さには影響しない。
            double labelY = shiftLabelUp ? y - fontSize : y;
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface, fontSize, Brushes.White, 1.0);

            if (pointLeft)
            {
                // 左向きタグ(speed/boost/BPM/拍子)はカラムの右側に描く(従来通り、はみ出し先は
                // ノートレーン側の余白なので問題ない)。
                dc.DrawText(text, new Point(col.X + col.Width + 2, labelY - fontSize / 2 - 1));
            }
            else
            {
                // 右向きタグ(マーカー)はマーカーレーン自身の幅にクリップして収める(2026-07-16h修正:
                // 以前は無条件にcol右側へ描画しており、隣接する小節レーンへはみ出していた)。
                dc.PushClip(new RectangleGeometry(new Rect(col.X, labelY - fontSize, col.Width, fontSize * 2)));
                dc.DrawText(text, new Point(col.X + 1, labelY - fontSize / 2 - 1));
                dc.Pop();
            }
        }
    }

    /// <summary>
    /// ドラッグ中(左ボタンを離すまで)のカーソル追従ゴースト描画(2026-07-16h追加)。
    /// SmartToolController.MoveObjectsPreview/ResizeFreezePreviewを見るだけで、モデルへは一切触れない
    /// (実際の移動・リサイズは従来通りマウスアップ時に1回だけ確定する)。黄色い輪郭線のゴーストで
    /// 「今どこへ移動しようとしているか」を常時表示する。
    /// </summary>
    private void DrawDragPreview(DrawingContext dc, ChartLayout layout, DifficultyTab tab, ChartProject project)
    {
        if (Controller is null) return;
        var ghostPen = new Pen(Brushes.Yellow, 1.5);
        int laneCount = layout.Template.KeyCount;

        if (Controller.MoveObjectsPreview is { } mv && (mv.LaneDelta != 0 || mv.TickDelta != 0))
        {
            foreach (var r in mv.Targets)
            {
                switch (r.Kind)
                {
                    case ObjectKind.Note:
                        {
                            int newLane = Math.Clamp(r.Lane + mv.LaneDelta, 0, Math.Max(0, laneCount - 1));
                            long newTick = r.Tick + mv.TickDelta;
                            var col = layout.NoteColumn(newLane);
                            double y = layout.TickToY(newTick);
                            double half = layout.NoteSize / 2;
                            dc.DrawRectangle(null, ghostPen, new Rect(col.CenterX - half, y - half, half * 2, half * 2));
                            break;
                        }
                    case ObjectKind.FreezeStart:
                    case ObjectKind.FreezeEnd:
                    case ObjectKind.FreezeBody:
                        {
                            var freeze = tab.Lanes[r.Lane].Freezes.FirstOrDefault(f => f.StartTick == r.Tick);
                            if (freeze is null) break;
                            int newLane = Math.Clamp(r.Lane + mv.LaneDelta, 0, Math.Max(0, laneCount - 1));
                            var col = layout.NoteColumn(newLane);
                            double y1 = layout.TickToY(freeze.StartTick + mv.TickDelta);
                            double y2 = layout.TickToY(freeze.EndTick + mv.TickDelta);
                            double half = layout.NoteSize / 2;
                            dc.DrawRectangle(null, ghostPen, new Rect(col.CenterX - half / 2, Math.Min(y1, y2), half, Math.Abs(y2 - y1)));
                            dc.DrawEllipse(null, ghostPen, new Point(col.CenterX, y1), half / 2, half / 2);
                            dc.DrawEllipse(null, ghostPen, new Point(col.CenterX, y2), half / 2, half / 2);
                            break;
                        }
                    case ObjectKind.Speed:
                        DrawGhostTag(dc, ghostPen, layout.Column(ColumnKind.Speed), layout.TickToY(r.Tick + mv.TickDelta));
                        break;
                    case ObjectKind.Boost:
                        DrawGhostTag(dc, ghostPen, layout.Column(ColumnKind.Boost), layout.TickToY(r.Tick + mv.TickDelta));
                        break;
                    case ObjectKind.Bpm:
                        DrawGhostTag(dc, ghostPen, layout.Column(ColumnKind.Bpm), layout.TickToY(r.Tick + mv.TickDelta));
                        break;
                    case ObjectKind.Marker:
                        DrawGhostTag(dc, ghostPen, layout.Column(ColumnKind.Marker), layout.TickToY(r.Tick + mv.TickDelta));
                        break;
                    case ObjectKind.Word:
                        // 2026-07-23(TBD 4): 歌詞はレーンを跨いだ移動をサポートしないため、常に自分自身のレーンで描く
                        DrawGhostTag(dc, ghostPen, layout.WordColumn(r.Lane), layout.TickToY(r.Tick + mv.TickDelta));
                        break;
                }
            }
        }

        if (Controller.ResizeFreezePreview is { } rf)
        {
            var col = layout.NoteColumn(rf.Lane);
            double y1 = layout.TickToY(rf.StartTick);
            double y2 = layout.TickToY(rf.EndTick);
            double half = layout.NoteSize / 2;
            dc.DrawRectangle(null, ghostPen, new Rect(col.CenterX - half / 2, Math.Min(y1, y2), half, Math.Abs(y2 - y1)));
            dc.DrawEllipse(null, ghostPen, new Point(col.CenterX, y1), half / 2, half / 2);
            dc.DrawEllipse(null, ghostPen, new Point(col.CenterX, y2), half / 2, half / 2);
        }

        // 右ドラッグ=矩形選択のプレビュー(2026-07-16j: 「よくある青い四角形」で範囲を表示する要望対応)
        if (Controller.RectSelectPreview is { } rsel)
        {
            var rect = new Rect(new Point(rsel.Start.X, rsel.Start.Y), new Point(rsel.Current.X, rsel.Current.Y));
            var fillBrush = Freeze(new SolidColorBrush(Color.FromArgb(60, 0x40, 0x90, 0xFF)));
            var rectPen = new Pen(Brushes.DodgerBlue, 1.5);
            dc.DrawRectangle(fillBrush, rectPen, rect);
        }

        // 右ドラッグ=連続削除のプレビュー(2026-07-16j: 削除対象になったオブジェクトを離すまで
        // 持続的にマーキングする要望対応)。赤い枠でドラッグパスが触れた全対象を毎フレーム描き直す。
        if (Controller.DragDeleteTouchedPreview is { Count: > 0 } touched)
        {
            var deletePen = new Pen(Brushes.OrangeRed, 2.0);
            foreach (var r in touched)
                DrawDragDeleteMark(dc, layout, tab, laneCount, r, deletePen);
        }
    }

    /// <summary>DragDeleteTouchedPreview内の1件を、その種別に応じた枠で強調表示する。</summary>
    private void DrawDragDeleteMark(DrawingContext dc, ChartLayout layout, DifficultyTab tab, int laneCount, ObjectRef r, Pen deletePen)
    {
        switch (r.Kind)
        {
            case ObjectKind.Note:
                {
                    var col = layout.NoteColumn(Math.Clamp(r.Lane, 0, Math.Max(0, laneCount - 1)));
                    double y = layout.TickToY(r.Tick);
                    double half = layout.NoteSize / 2 + 3;
                    dc.DrawRectangle(null, deletePen, new Rect(col.CenterX - half, y - half, half * 2, half * 2));
                    break;
                }
            case ObjectKind.FreezeStart:
            case ObjectKind.FreezeEnd:
            case ObjectKind.FreezeBody:
                {
                    var freeze = tab.Lanes[r.Lane].Freezes.FirstOrDefault(f => f.StartTick == r.Tick);
                    if (freeze is null) break;
                    var col = layout.NoteColumn(Math.Clamp(r.Lane, 0, Math.Max(0, laneCount - 1)));
                    double y1 = layout.TickToY(freeze.StartTick);
                    double y2 = layout.TickToY(freeze.EndTick);
                    double half = layout.NoteSize / 2 + 3;
                    dc.DrawRectangle(null, deletePen, new Rect(col.CenterX - half, Math.Min(y1, y2) - half, half * 2, Math.Abs(y2 - y1) + half * 2));
                    break;
                }
            case ObjectKind.Speed:
                DrawDragDeleteTagMark(dc, layout.Column(ColumnKind.Speed), layout.TickToY(r.Tick), deletePen);
                break;
            case ObjectKind.Boost:
                DrawDragDeleteTagMark(dc, layout.Column(ColumnKind.Boost), layout.TickToY(r.Tick), deletePen);
                break;
            case ObjectKind.Bpm:
                DrawDragDeleteTagMark(dc, layout.Column(ColumnKind.Bpm), layout.TickToY(r.Tick), deletePen);
                break;
            case ObjectKind.Marker:
                DrawDragDeleteTagMark(dc, layout.Column(ColumnKind.Marker), layout.TickToY(r.Tick), deletePen);
                break;
            case ObjectKind.TimeSignature:
                DrawDragDeleteTagMark(dc, layout.Column(ColumnKind.Measure), layout.TickToY(r.Tick), deletePen);
                break;
            case ObjectKind.Word:
                DrawDragDeleteTagMark(dc, layout.WordColumn(r.Lane), layout.TickToY(r.Tick), deletePen);
                break;
        }
    }

    private static void DrawDragDeleteTagMark(DrawingContext dc, ColumnInfo col, double y, Pen deletePen)
    {
        dc.DrawEllipse(null, deletePen, new Point(col.CenterX, y), 11, 11);
    }

    private static void DrawGhostTag(DrawingContext dc, Pen ghostPen, ColumnInfo col, double y)
    {
        dc.DrawEllipse(null, ghostPen, new Point(col.CenterX, y), 9, 9);
    }

    /// <summary>選択中オブジェクトの周りに黄色い枠を重ねて描く(仕様書6.3.1の選択表示)</summary>
    private static void DrawSelectionHighlights(DrawingContext dc, ChartLayout layout, EditorDocument doc, long tickMin, long tickMax)
    {
        var tab = doc.CurrentTab;
        var project = doc.Project;
        double half = layout.NoteSize / 2;

        foreach (var r in doc.Selection)
        {
            if (r.Kind is ObjectKind.TimeSignature)
            {
                var sig = project.TimeSignatures.FirstOrDefault(x => x.MeasureIndex == r.Tick);
                if (sig is null) continue;
                var engine = project.CreateTimingEngine();
                long tick = engine.MeasureStartTick(sig.MeasureIndex);
                if (tick < tickMin || tick > tickMax) continue;
                var col = layout.Column(ColumnKind.Measure);
                dc.DrawEllipse(null, SelectionPen, new Point(col.CenterX, layout.TickToY(tick)), 12, 12);
                continue;
            }

            if (r.Tick < tickMin || r.Tick > tickMax) continue;

            switch (r.Kind)
            {
                case ObjectKind.Note:
                    {
                        var col = layout.NoteColumn(r.Lane);
                        double y = layout.TickToY(r.Tick);
                        dc.DrawRectangle(null, SelectionPen, new Rect(col.CenterX - half - 2, y - half - 2, half * 2 + 4, half * 2 + 4));
                        break;
                    }
                case ObjectKind.FreezeStart:
                case ObjectKind.FreezeEnd:
                case ObjectKind.FreezeBody:
                    {
                        var f = tab.Lanes[r.Lane].Freezes.FirstOrDefault(x => x.StartTick == r.Tick);
                        if (f is null) continue;
                        var col = layout.NoteColumn(r.Lane);
                        double y1 = layout.TickToY(f.StartTick);
                        double y2 = layout.TickToY(f.EndTick);
                        dc.DrawRectangle(null, SelectionPen, new Rect(col.CenterX - half - 2, Math.Min(y1, y2) - 2, half * 2 + 4, Math.Abs(y2 - y1) + 4));
                        break;
                    }
                case ObjectKind.Speed:
                    HighlightTag(dc, layout.Column(ColumnKind.Speed), layout.TickToY(r.Tick));
                    break;
                case ObjectKind.Boost:
                    HighlightTag(dc, layout.Column(ColumnKind.Boost), layout.TickToY(r.Tick));
                    break;
                case ObjectKind.Bpm:
                    HighlightTag(dc, layout.Column(ColumnKind.Bpm), layout.TickToY(r.Tick));
                    break;
                case ObjectKind.Marker:
                    HighlightTag(dc, layout.Column(ColumnKind.Marker), layout.TickToY(r.Tick));
                    break;
                case ObjectKind.Word:
                    if (r.Lane < tab.WordLanes.Count) HighlightTag(dc, layout.WordColumn(r.Lane), layout.TickToY(r.Tick));
                    break;
            }
        }
    }

    private static void HighlightTag(DrawingContext dc, ColumnInfo col, double y) =>
        dc.DrawEllipse(null, SelectionPen, new Point(col.CenterX, y), 13, 11);

    private static readonly Pen PlaybackLinePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x00))), 3));

    /// <summary>音楽再生中のカレントフレーム位置を示す横線(要望2026-07-15、黄色め太め)</summary>
    /// <summary>再生開始フレームの可視化ライン(全レーン横断、2026-07-17f、未解決事項§2-2)。
    /// 色・太さはAppSettings(表示設定ダイアログ)で変更可能。</summary>
    private void DrawPlaybackStartLine(DrawingContext dc, ChartLayout layout, TimingEngine engine, long tickMin, long tickMax)
    {
        if (Document?.CurrentTab.PlaybackStartFrame is not { } frame) return;
        double tick = engine.FrameToTick(frame);
        if (tick < tickMin || tick > tickMax) return;
        double y = layout.TickToY(tick);
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        var pen = new Pen(Freeze(new SolidColorBrush(PlaybackStartLineColor)), PlaybackStartLineWidth);
        pen.Freeze();
        dc.DrawLine(pen, new Point(left, y), new Point(right, y));
    }

    /// <summary>時間情報レーンの「時間範囲選択」ハイライト帯(2026-07-27要望対応、旧レーン入替マクロ
    /// 専用の範囲選択モードから置き換え)。始点/終点それぞれのマーカー線は設置済みなら単独でも表示し
    /// (片方だけ置いた状態が視認できるように)、両方設置済みの場合はその間を半透明の帯で塗る
    /// (DrawPlaybackStartLine/CursorHighlight帯の描画パターンを踏襲)。</summary>
    private void DrawTimeRangeSelectionHighlight(DrawingContext dc, ChartLayout layout, DifficultyTab tab, long tickMin, long tickMax)
    {
        if (tab.TimeRangeSelectionStartTick is null && tab.TimeRangeSelectionEndTick is null) return;
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;

        if (tab.TimeRangeSelectionStartTick is { } st && tab.TimeRangeSelectionEndTick is { } et)
        {
            double loTick = Math.Min(st, et), hiTick = Math.Max(st, et);
            if (!(hiTick < tickMin || loTick > tickMax))
            {
                double y0 = layout.TickToY(loTick), y1 = layout.TickToY(hiTick);
                var fillColor = Color.FromArgb(0x40, MacroRangeHighlightColor.R, MacroRangeHighlightColor.G, MacroRangeHighlightColor.B);
                var rect = new Rect(left, Math.Min(y0, y1), right - left, Math.Abs(y1 - y0));
                dc.DrawRectangle(Freeze(new SolidColorBrush(fillColor)), null, rect);
            }
        }

        var markerPen = new Pen(Freeze(new SolidColorBrush(MacroRangeHighlightColor)), MacroRangeMarkerWidth);
        markerPen.Freeze();
        foreach (var tick in new[] { tab.TimeRangeSelectionStartTick, tab.TimeRangeSelectionEndTick })
        {
            if (tick is not { } t || t < tickMin || t > tickMax) continue;
            double y = layout.TickToY(t);
            dc.DrawLine(markerPen, new Point(left, y), new Point(right, y));
        }
    }

    private void DrawPlaybackLine(DrawingContext dc, ChartLayout layout, long tickMin, long tickMax)
    {
        if (PlaybackTick is not { } tick || tick < tickMin || tick > tickMax) return;
        double y = layout.TickToY(tick);
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        dc.DrawLine(PlaybackLinePen, new Point(left, y), new Point(right, y));
    }
}
