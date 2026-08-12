using System.Windows;
using System.Windows.Media;

namespace DanoniEditor.App;

/// <summary>
/// レーンラベルヘッダー(speed/boost/BPM等の見出しバー、2026-08-08新設)。
/// 従来はChartCanvas.DrawLaneLabelsとして譜面本体と同じキャンバス上に「常に最前面」でオーバーレイ
/// 描画していたが、スクロールでその位置まで来たノート等がラベルの下へ完全に隠れて操作できなくなる
/// 不具合があった(要望対応: 「オーバーレイではなく違う領域として描写してほしい」)。
/// 本クラスはScrollViewerの外側に配置する専用の小さなFrameworkElementで、対になるChartCanvas
/// (TargetCanvas)の列レイアウト・トグル状態(KeyboardModeActive等)をそのまま読みに行き、
/// 描画本体はChartCanvas.PaintLaneLabelBar/MeasureLaneLabelBarHeightへ委譲する(ロジックの二重管理を
/// 避けるため)。横スクロールに追従させるため、UpdateHorizontalOffset(MainWindow.xaml.csの
/// ChartScrollViewer_ScrollChanged等から呼ばれる)で受け取ったオフセット分だけ、描画時に
/// TranslateTransformで平行移動する。
/// </summary>
public sealed class LaneHeaderBar : FrameworkElement
{
    /// <summary>描画内容・サイズの元になるChartCanvas(同じペインのScrollViewer内にあるインスタンス)。
    /// MainWindow.xaml.cs側でInitializeComponent直後に設定する。</summary>
    public ChartCanvas? TargetCanvas { get; set; }

    private double _horizontalOffset;

    /// <summary>対になるScrollViewerのHorizontalOffsetを反映する(列のX座標を譜面本体と一致させるため)。</summary>
    public void UpdateHorizontalOffset(double offset)
    {
        if (_horizontalOffset == offset) return;
        _horizontalOffset = offset;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = TargetCanvas?.MeasureLaneLabelBarHeight() ?? 0;
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (TargetCanvas?.Document is null) return;
        dc.PushTransform(new TranslateTransform(-_horizontalOffset, 0));
        try
        {
            TargetCanvas.PaintLaneLabelBar(dc, _horizontalOffset, ActualWidth);
        }
        finally
        {
            dc.Pop();
        }
    }
}
