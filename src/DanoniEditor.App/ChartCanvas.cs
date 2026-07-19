using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Audio;
using DanoniEditor.Core.Timing;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// 譜面編集エリアの描画本体(仕様書6.1/6.6、07-16手記§6の設計ロック分)。
/// ItemsControl/ObservableCollectionでノート1つ1つをUIElement化せず、OnRenderで直接DrawingContextに
/// 描く。可視範囲(ViewportRect、MainWindow側がScrollViewer.ScrollChangedで更新)の外は計算すらしない。
/// 画像アセット(./img)は現状リポジトリに存在しないため、ベクター描画のみ(仕様書のフォールバック経路)。
/// </summary>
public sealed class ChartCanvas : FrameworkElement
{
    // --- 07-16手記§5: 既定レーン色・イベントタグ色 ---
    private static readonly string[] DefaultLaneColors =
        ["#99ffff", "#ffff33", "#ffffff", "#ff0066", "#ff9966", "#99ff99"];

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
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xEE, 0x00)));
    private static readonly Pen SelectionPen = Freeze(new Pen(SelectionBrush, 2));
    private static readonly Typeface Typeface = new("Segoe UI");

    private static readonly Dictionary<string, Brush> BrushCache = [];

    // --- ノート画像キャッシュ(仕様書2.1/4.3): ./img/{arrow,c,giko,iyo,monar,morara,onigiri}.png ---
    // 画像が無い場合(現状リポジトリに未同梱)はnullを返し、呼び出し側がベクター描画にフォールバックする。
    private static readonly string? ImgDir = AppPaths.FindAssetDir("img");
    private static readonly Dictionary<string, BitmapImage?> ImageCache = [];

    internal static BitmapImage? GetNoteImage(string noteGraphic) // 2026-07-17g: PlaytestWindowと共用のためinternal化
    {
        if (ImageCache.TryGetValue(noteGraphic, out var cached)) return cached;
        BitmapImage? image = null;
        if (ImgDir is not null)
        {
            var path = Path.Combine(ImgDir, $"{noteGraphic}.png");
            if (File.Exists(path))
            {
                try
                {
                    image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                }
                catch { image = null; } // 壊れた画像等はベクターへフォールバック
            }
        }
        ImageCache[noteGraphic] = image;
        return image;
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

    private void InvalidateAll()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>現在スクロールで見えている範囲(canvasローカル座標、px)。MainWindowがScrollChangedで更新する。</summary>
    public Rect ViewportRect { get; set; } = Rect.Empty;

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

    /// <summary>
    /// 表示設定(AppSettings由来)をまとめて適用し、再描画する。MainWindowが起動時・設定変更時に呼ぶ。
    /// </summary>
    public void ApplyDisplaySettings(bool showNoteImages, bool showHighlightGrid, double highlightLineWidth, Color highlightLineColor)
    {
        ShowNoteImages = showNoteImages;
        ShowHighlightGrid = showHighlightGrid;
        HighlightLineWidth = highlightLineWidth;
        HighlightLineColor = highlightLineColor;
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

    // =====================================================================
    // 波形表示+StartNumber編集モード(2026-07-18、要望メモ07-15項目7・8)
    // =====================================================================

    /// <summary>波形ピークキャッシュ(MainWindowがバックグラウンドデコード後に設定)。null=未読込</summary>
    public WaveformPeaks? Waveform { get; set; }

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
        Document.Project.StartNumber = _snStartNumber0 + dpx * _snFramesPerPx;
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
            long maxTick = (long)Document.CurrentLayout.YToTick(guideY + 2000) + 1;
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

    public void UpdateViewport(Rect rect)
    {
        ViewportRect = rect;
        InvalidateVisual();
    }

    /// <summary>マウス操作の受け皿(仕様書6.3.1)。MainWindowがEditorDocumentと紐付けて生成する。</summary>
    public SmartToolController? Controller { get; set; }

    public ChartCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
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

        CaptureMouse();
        Controller.BeginLeft(PosOf(e.GetPosition(this)), ModifiersOf(e));
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
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
        if (StartNumberEditMode) { SnMove(e); return; }
        if (Controller is null || !IsMouseCaptured) return;
        Controller.Move(PosOf(e.GetPosition(this)));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (StartNumberEditMode) { SnUp(e); return; }
        if (Controller is null) return;
        Controller.End(PosOf(e.GetPosition(this)));
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (Controller is null) return;
        Controller.End(PosOf(e.GetPosition(this)));
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    /// <summary>
    /// Shift+ホイール=ハイスピ(pxPerTick)、Ctrl+ホイール=作業エリア全体のズーム
    /// (ChartLayout.ZoomScale。カラム幅・ノート表示サイズがまとめて連動する、仕様書4.3+2026-07-16修正)。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Document is null) return;
        var layout = Document.CurrentLayout;
        double step = e.Delta > 0 ? 1.1 : 1 / 1.1;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // 2026-07-17: カーソル位置を中心に拡大縮小する要望対応。PxPerTick変更前後で
            // カーソル下のtickの画面上位置が変わらないよう、ScrollViewerの垂直オフセットを補正する。
            double mouseY = e.GetPosition(this).Y;
            double tickAtCursor = layout.YToTick(mouseY);
            var sv = FindAncestorScrollViewer();
            double oldOffset = sv?.VerticalOffset ?? 0;

            layout.PxPerTick = Math.Clamp(layout.PxPerTick * step, ChartLayout.MinPxPerTick, ChartLayout.MaxPxPerTick); // 2026-07-19g: 分解能スケールに追従
            // 2026-07-17f: Ctrl+Shift+スクロール=縦(ハイスピ)と横(全体ズーム)の同時伸縮
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                layout.SetZoom(layout.ZoomScale * step);
            e.Handled = true;
            InvalidateAll();

            if (sv is not null)
            {
                double newY = layout.TickToY(tickAtCursor);
                double desiredOffset = newY - (mouseY - oldOffset);
                sv.ScrollToVerticalOffset(Math.Max(0, desiredOffset));
            }
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            layout.SetZoom(layout.ZoomScale * step);
            e.Handled = true;
            InvalidateAll();
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
        long maxTick = MaxTickInProject(Document);
        double h = layout.ContentHeight(maxTick);
        return new Size(layout.TotalWidth, h);
    }

    private static long MaxTickInProject(EditorDocument doc)
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

        var viewport = ViewportRect.IsEmpty ? new Rect(0, 0, layout.TotalWidth, Math.Max(RenderSize.Height, 600)) : ViewportRect;
        double yTop = Math.Max(0, viewport.Top - 32);   // 少し余裕を持ってカリング
        double yBottom = viewport.Bottom + 32;
        long tickMin = Math.Max(0, (long)layout.YToTick(yTop) - 1);
        long tickMax = (long)layout.YToTick(yBottom) + 1;

        DrawWaveform(dc, layout, engine, yTop, yBottom); // 最下層(2026-07-18)。カラム背景は半透明のため透ける
        DrawColumnBackgrounds(dc, layout, yTop, yBottom);
        DrawColumnSeparators(dc, layout, yTop, yBottom);
        DrawGridAndMeasureLines(dc, layout, engine, Document.Snap, tickMin, tickMax);
        DrawNotesAndFreezes(dc, layout, tab, project, tickMin, tickMax);
        DrawValueEvents(dc, layout, tab, project, tickMin, tickMax);
        DrawMarkers(dc, layout, project, tickMin, tickMax);
        DrawTimeSignatures(dc, layout, project, engine, tickMin, tickMax);
        DrawSelectionHighlights(dc, layout, Document, tickMin, tickMax);
        DrawDragPreview(dc, layout, tab, project);
        DrawPlaybackLine(dc, layout, tickMin, tickMax);
        DrawPlaybackStartLine(dc, layout, engine, tickMin, tickMax);
        DrawGuideLine(dc, layout, engine, yTop, yBottom); // StartNumber編集モードのガイド線(2026-07-18)
    }

    private static readonly Brush WaveformBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0x55, 0x4F, 0xC3, 0xF7)));
    private static readonly Pen GuidePen = MakeFrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 2) { DashStyle = new DashStyle([6, 3], 0) });

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
        double pxPerFrame = (layout.TickToY(tickMax) - layout.TickToY(tickMin)) / (fMax - fMin);
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

    private void DrawGridAndMeasureLines(DrawingContext dc, ChartLayout layout, TimingEngine engine, SnapService snap, long tickMin, long tickMax)
    {
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;

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
            long gridStart = tickMin - (tickMin % g);
            for (long t = gridStart; t <= tickMax; t += g)
            {
                if (t < 0 || t % TimingEngine.TicksPerBeat == 0) continue; // 拍線と重複させない
                double y = layout.TickToY(t);
                dc.DrawLine(new Pen(GridLineBrush, 1), new Point(left, y), new Point(right, y));
            }
        }

        // 拍線(medium、4分=48tick間隔)。フレーム情報モード中はフレームグリッドに譲る
        if (Document?.IsFrameEditMode != true)
        {
            long beatStart = tickMin - (tickMin % TimingEngine.TicksPerBeat);
            for (long t = beatStart; t <= tickMax; t += TimingEngine.TicksPerBeat)
            {
                if (t < 0) continue;
                double y = layout.TickToY(t);
                dc.DrawLine(new Pen(BeatLineBrush, 1), new Point(left, y), new Point(right, y));
            }
        }

        // 小節線(strong、小節番号付き、仕様書7.5)
        int measure = 0;
        long tick = 0;
        while (tick <= tickMax)
        {
            if (tick >= tickMin - 4L * TimingEngine.TicksPerBeat * 4)
            {
                double y = layout.TickToY(tick);
                dc.DrawLine(new Pen(MeasureLineBrush, 1.5), new Point(left, y), new Point(right, y));
                // 小節番号のフォントサイズもZoomScaleに連動させる(2026-07-16h、Ctrl+スクロールで
                // 拡大しても文字サイズが変わらず見づらいとの指摘対応)
                double measureFontSize = Math.Max(7, 11 * layout.ZoomScale);
                var text = new FormattedText(measure.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface, measureFontSize, Brushes.White, 1.0);
                dc.DrawText(text, new Point(2, y - measureFontSize - 1));
            }
            var sig = engine.SignatureAt(tick);
            tick += sig.TicksPerMeasure;
            measure++;
            if (measure > 100000) break; // 安全弁(拍子破損時の無限ループ防止)
        }
    }

    private void DrawNotesAndFreezes(DrawingContext dc, ChartLayout layout, DifficultyTab tab, ChartProject project, long tickMin, long tickMax)
    {
        // 強調グリッド用ブラシ(ShowNoteImages=false時のみ使用、レンダー1回につき1個を使い回す)
        var highlightBrush = Freeze(new SolidColorBrush(HighlightLineColor));

        for (int i = 0; i < tab.Lanes.Count; i++)
        {
            var col = layout.NoteColumn(i);
            var laneDef = layout.Template.Lanes[i];
            var brush = LaneBrush(tab, project, laneDef.ColorGroup);
            double cx = col.CenterX;
            double half = layout.NoteSize / 2;
            var image = GetNoteImage(laneDef.NoteGraphic); // ./img/{noteGraphic}.png、無ければnull(ベクターへフォールバック)

            var (frzNoteColor, frzBandColor) = FrzColors(tab, project, laneDef.ColorGroup, brush);
            foreach (var f in tab.Lanes[i].Freezes)
            {
                if (f.EndTick < tickMin || f.StartTick > tickMax) continue;
                double y1 = layout.TickToY(f.StartTick);
                double y2 = layout.TickToY(f.EndTick);

                // 帯(フリーズ胴体)はfrzColorのスロット[1]("帯(通常)")を使う(仕様書6.4.2)
                var bandBrush = Freeze(new SolidColorBrush(frzBandColor) { Opacity = 0.5 });
                dc.DrawRectangle(bandBrush, null, new Rect(cx - half / 2, y1, half, y2 - y1));

                // 2026-07-16j: ShowNoteImages/ShowHighlightGridは独立トグルになったため、
                // 「画像 or ベクターフォールバック」を描いた上で、強調グリッドは条件を問わず追加で重ねて描く。
                if (ShowNoteImages && image is not null)
                {
                    // 始点・終点ともレーンのノート画像を使う(2026-07-16h)。frzColorのスロット[0]の色を
                    // 乗算着色する(2026-07-16k: 画像表示時もfrzColorが反映されない不具合の対応)。
                    DrawNoteImage(dc, image, laneDef, cx, y1, layout.NoteSize, frzNoteColor);
                    DrawNoteImage(dc, image, laneDef, cx, y2, layout.NoteSize, frzNoteColor);
                }
                else if (ShowNoteImages)
                {
                    // 画像が無い場合のベクターフォールバック。色はfrzColorのスロット[0]("始点終点(通常)")
                    var noteBrush = Freeze(new SolidColorBrush(frzNoteColor));
                    dc.DrawEllipse(noteBrush, null, new Point(cx, y1), half / 2, half / 2);
                    dc.DrawEllipse(noteBrush, new Pen(Brushes.White, 1), new Point(cx, y2), half / 2, half / 2);
                }

                if (ShowHighlightGrid)
                {
                    // 強調グリッド: 始点・終点それぞれの位置に横棒を描く(2026-07-16h、2026-07-16jで独立トグル化)
                    dc.DrawRectangle(highlightBrush, null, new Rect(col.X, y1 - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
                    dc.DrawRectangle(highlightBrush, null, new Rect(col.X, y2 - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
                }
            }

            foreach (var t in tab.Lanes[i].Notes)
            {
                if (t < tickMin || t > tickMax) continue;
                double y = layout.TickToY(t);
                // 2026-07-16j: 独立トグル化。ノート画像(or ベクターフォールバック)と強調グリッドは
                // 排他ではなく、それぞれのフラグに応じて重ねて描く。
                if (ShowNoteImages)
                {
                    if (image is not null)
                        // setColorの色を乗算着色する(2026-07-16k: 画像表示時もsetColorが反映されない不具合の対応)。
                        DrawNoteImage(dc, image, laneDef, cx, y, layout.NoteSize, ((SolidColorBrush)brush).Color);
                    else
                        dc.DrawRectangle(brush, new Pen(Brushes.Black, 0.5), new Rect(cx - half, y - half, half * 2, half * 2));
                }

                if (ShowHighlightGrid)
                {
                    // ノート画像の有無に関わらず、レーン列の幅いっぱいに強調グリッド(横棒)を描く
                    // (2026-07-16b: 「ノート画像だけだとレーン上の位置がわかりづらい」対応
                    //  2026-07-16j: ノート画像ONでも併用できるよう独立トグル化)。
                    // HitTest/選択/EditActionsは既存のNote判定(座標ベース)をそのまま使うため無変更。
                    dc.DrawRectangle(highlightBrush, null,
                        new Rect(col.X, y - HighlightLineWidth / 2, col.Width, HighlightLineWidth));
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

    /// <summary>
    /// レーン色。tab自身のSetColorOverrideが無ければ、1タブ目(=共通値の実体、仕様書6.4.2)の値に
    /// フォールバックする(「全ての難易度で共通」チェックON時、②タブのプレビューと表示を一致させるため、
    /// 2026-07-16f修正。以前はここでいきなり本クラス既定色に落ちてしまい、共通値が反映されなかった)。
    /// </summary>
    internal static Brush LaneBrush(DifficultyTab tab, ChartProject project, int colorGroup) // 2026-07-17g: internal化(同上)
    {
        var overrides = tab.SetColorOverride ?? (project.Tabs.Count > 0 ? project.Tabs[0].SetColorOverride : null);
        if (overrides is not null && colorGroup >= 0 && colorGroup < overrides.Count)
            return BrushOf(overrides[colorGroup]);
        var hex = DefaultLaneColors[colorGroup % DefaultLaneColors.Length];
        return BrushOf(hex);
    }

    /// <summary>
    /// フリーズの表示色(仕様書6.4.2 frzColor)。1グループにつき4スロット
    /// [0]始点終点(通常) [1]帯(通常) [2]始点終点(判定中) [3]帯(判定中) を仕様書通りフラットに保持している
    /// (エディタはプレイ判定を行わないため[2][3]は現状未使用)。tab自身のFrzColorOverrideが無ければ
    /// 1タブ目(共通値の実体)へ、さらに個々のスロットが空欄ならsetColorの値へ自動補完する
    /// (仕様書6.4.2の自動補完ルール)。2026-07-16h: 従来はfrzColorが一切参照されずsetColor直流用だった。
    /// </summary>
    internal static (Color NoteColor, Color BandColor) FrzColors(DifficultyTab tab, ChartProject project, int colorGroup, Brush setColorBrush) // 2026-07-17g: internal化(同上)
    {
        var fallback = ((SolidColorBrush)setColorBrush).Color;

        // 2026-07-16l: defaultFrzColorUse=trueの間、frzColorの指定は強制的にOFF扱いにする
        // (dos-h0063の仕様上、trueの時は本体側の既定フリーズアロー色セットが使われ、frzColorの
        // 値そのものが無視されるため。エディタは本体既定色セットまでは実装していないので、
        // 代わりにsetColor由来の色へフォールバックする — ユーザー向けに開示済みの簡略化)。
        bool defaultFrzColorUse = project.ExtraHeaders.TryGetValue("defaultFrzColorUse", out var dfu) && dfu == "true";
        if (defaultFrzColorUse) return (fallback, fallback);

        var frz = tab.FrzColorOverride ?? (project.Tabs.Count > 0 ? project.Tabs[0].FrzColorOverride : null);
        if (frz is null) return (fallback, fallback);

        int baseIdx = colorGroup * 4;
        string? noteHex = baseIdx < frz.Count ? frz[baseIdx] : null;
        string? bandHex = baseIdx + 1 < frz.Count ? frz[baseIdx + 1] : null;

        Color noteColor = string.IsNullOrWhiteSpace(noteHex) ? fallback : TryParseColor(noteHex!, fallback);
        Color bandColor = string.IsNullOrWhiteSpace(bandHex) ? fallback : TryParseColor(bandHex!, fallback);
        return (noteColor, bandColor);
    }

    private static Color TryParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    private static void DrawValueEvents(DrawingContext dc, ChartLayout layout, DifficultyTab tab, ChartProject project, long tickMin, long tickMax)
    {
        var speedCol = layout.Column(ColumnKind.Speed);
        foreach (var e in tab.SpeedEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, speedCol, layout.TickToY(e.Tick), SpeedBrush, e.Value.ToString("0.00"), pointLeft: true, layout.ZoomScale);

        var boostCol = layout.Column(ColumnKind.Boost);
        foreach (var e in tab.BoostEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, boostCol, layout.TickToY(e.Tick), BoostBrush, e.Value.ToString("0.00"), pointLeft: true, layout.ZoomScale);

        var bpmCol = layout.Column(ColumnKind.Bpm);
        foreach (var e in project.BpmEvents)
            if (e.Tick >= tickMin && e.Tick <= tickMax)
                DrawEventTag(dc, bpmCol, layout.TickToY(e.Tick), BpmBrush, e.Bpm.ToString("0.##"), pointLeft: true, layout.ZoomScale);
    }

    /// <summary>マーカーコメントの表示方式(仕様書7.4: 全文/先頭数文字、環境設定から適用、2026-07-19b)</summary>
    public bool MarkerCommentFull { get; set; } = true;
    public int MarkerCommentHeadChars { get; set; } = 4;

    private void DrawMarkers(DrawingContext dc, ChartLayout layout, ChartProject project, long tickMin, long tickMax)
    {
        var col = layout.Column(ColumnKind.Marker);
        foreach (var m in project.Markers)
        {
            if (m.Tick < tickMin || m.Tick > tickMax) continue;
            var label = MarkerCommentFull || m.Comment.Length <= MarkerCommentHeadChars
                ? m.Comment
                : m.Comment[..MarkerCommentHeadChars] + "…";
            DrawEventTag(dc, col, layout.TickToY(m.Tick), MarkerBrush, label, pointLeft: false, layout.ZoomScale);
        }
    }

    private static void DrawTimeSignatures(DrawingContext dc, ChartLayout layout, ChartProject project, TimingEngine engine, long tickMin, long tickMax)
    {
        var col = layout.Column(ColumnKind.Measure);
        foreach (var s in project.TimeSignatures)
        {
            long tick = engine.MeasureStartTick(s.MeasureIndex);
            if (tick < tickMin || tick > tickMax) continue;
            DrawEventTag(dc, col, layout.TickToY(tick), TimeSigBrush, $"{s.Numerator}/{s.Denominator}", pointLeft: true, layout.ZoomScale);
        }
    }

    /// <summary>
    /// タグ形状(仕様書6.6): 左向き=speed/boost/BPM/拍子、右向き=マーカー。
    /// ラベル文字サイズはzoomScale(Ctrl+スクロール、ChartLayout.ZoomScale)に連動させる(2026-07-16h:
    /// 4Kモニター等でズームしても文字サイズが変わらず見づらいとの指摘対応)。
    /// マーカー(pointLeft:false)のラベルは、以前は常にcol右側に描画しており隣接カラム(小節レーン)へ
    /// はみ出していたバグを修正: マーカーレーン自身の幅にクリップして収める(2026-07-16h)。
    /// </summary>
    private static void DrawEventTag(DrawingContext dc, ColumnInfo col, double y, Brush brush, string label, bool pointLeft, double zoomScale)
    {
        const double w = 20, h = 9;
        double cx = col.CenterX;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (pointLeft)
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
            double fontSize = Math.Max(7, 9 * zoomScale);
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface, fontSize, Brushes.White, 1.0);

            if (pointLeft)
            {
                // 左向きタグ(speed/boost/BPM/拍子)はカラムの右側に描く(従来通り、はみ出し先は
                // ノートレーン側の余白なので問題ない)。
                dc.DrawText(text, new Point(col.X + col.Width + 2, y - fontSize / 2 - 1));
            }
            else
            {
                // 右向きタグ(マーカー)はマーカーレーン自身の幅にクリップして収める(2026-07-16h修正:
                // 以前は無条件にcol右側へ描画しており、隣接する小節レーンへはみ出していた)。
                dc.PushClip(new RectangleGeometry(new Rect(col.X, y - fontSize, col.Width, fontSize * 2)));
                dc.DrawText(text, new Point(col.X + 1, y - fontSize / 2 - 1));
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
                            dc.DrawRectangle(null, ghostPen, new Rect(col.CenterX - half / 2, y1, half, y2 - y1));
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
                }
            }
        }

        if (Controller.ResizeFreezePreview is { } rf)
        {
            var col = layout.NoteColumn(rf.Lane);
            double y1 = layout.TickToY(rf.StartTick);
            double y2 = layout.TickToY(rf.EndTick);
            double half = layout.NoteSize / 2;
            dc.DrawRectangle(null, ghostPen, new Rect(col.CenterX - half / 2, y1, half, y2 - y1));
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
                    dc.DrawRectangle(null, deletePen, new Rect(col.CenterX - half, y1 - half, half * 2, (y2 - y1) + half * 2));
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
                        dc.DrawRectangle(null, SelectionPen, new Rect(col.CenterX - half - 2, y1 - 2, half * 2 + 4, (y2 - y1) + 4));
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
        if (Document?.Project.PlaybackStartFrame is not { } frame) return;
        double tick = engine.FrameToTick(frame);
        if (tick < tickMin || tick > tickMax) return;
        double y = layout.TickToY(tick);
        double left = layout.Columns[0].X;
        double right = layout.Columns[^1].X + layout.Columns[^1].Width;
        var pen = new Pen(Freeze(new SolidColorBrush(PlaybackStartLineColor)), PlaybackStartLineWidth);
        pen.Freeze();
        dc.DrawLine(pen, new Point(left, y), new Point(right, y));
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
