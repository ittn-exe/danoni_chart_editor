using System.Windows;
using System.Windows.Input;
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

    /// <summary>2026-08-09要望対応: 「右パネルの入力欄で編集後、譜面ビュー内のグリッド外をクリックしても
    /// フォーカスが戻らずBackSpace等のショートカットが効かない」不具合の修正。本クラスは
    /// ScrollViewer外の専用領域(譜面ビュー上部/下部の見出しバー)で、Focusable=falseかつ独自の
    /// マウス処理を何も持たないため、ここをクリックしてもWPFの既定動作(フォーカス可能要素への
    /// 自動フォーカス移動)が働かず、右パネルのTextBoxにフォーカスが残ったままになっていた
    /// (MainWindow_PreviewKeyDownのtextInputFocused判定に引っかかり、ClearPlaybackStartLine等の
    /// ショートカットが無視される)。ここも「譜面ビューの一部」としてクリックを扱い、対になる
    /// ChartCanvasへ明示的にフォーカスを委譲する。</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        TargetCanvas?.Focus();
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
