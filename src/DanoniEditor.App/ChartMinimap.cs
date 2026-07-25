using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using DanoniEditor.Editing;

namespace DanoniEditor.App;

/// <summary>
/// 譜面ビュー右側のミニマップ(2026-07-26、ユーザー要望)。譜面全体(tick0〜末尾)を縦に圧縮した
/// 概観を表示し、クリック(またはドラッグ)した位置へChartScrollViewerをジャンプさせる。
/// ノート・フリーズの位置はDocument.CurrentLayoutのTickToY(Reverse反映済み)をそのままContentHeightで
/// 正規化して使うため、譜面ビュー本体の表示方向と常に一致する。ノート内容の更新は
/// EditorDocument.Changed(Execute/Undo/Redo/タブ切替で発火)を購読して追随し、スクロール位置(現在の
/// 表示範囲を示す半透明の矩形)はChartScrollViewer側のScrollChangedからInvalidateVisualを呼んでもらう
/// 形にする(MainWindow.ChartScrollViewer_ScrollChanged参照)。
/// </summary>
internal sealed class ChartMinimap : FrameworkElement
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly Brush NoteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush FreezeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0xcc, 0xff)));
    private static readonly Brush ViewportBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xff, 0xff, 0xff)));
    private static readonly Pen ViewportPen = Freeze(new Pen(Brushes.White, 1));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return (T)f; }

    private EditorDocument? _document;
    public EditorDocument? Document
    {
        get => _document;
        set
        {
            if (_document is not null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document is not null) _document.Changed += OnDocumentChanged;
            InvalidateVisual();
        }
    }

    /// <summary>ジャンプ先を実際にスクロールさせる対象(2026-07-26)。MainWindowが紐付ける。</summary>
    public ScrollViewer? TargetScrollViewer { get; set; }

    /// <summary>2026-07-26: ミニマップ操作後にキーボードフォーカスを戻す先(通常は譜面ビュー本体の
    /// ChartCanvas)。ミニマップ自体はFocusable=falseだが、クリック/ドラッグ操作の前に別のコントロール
    /// (右パネルのテキストボックス等)へフォーカスが残っていると、操作直後もそちらにショートカットキーが
    /// 奪われたままになるため、明示的にここへフォーカスを戻す。</summary>
    public UIElement? FocusTarget { get; set; }

    /// <summary>読み込み済み音楽ファイルの全体長(フレーム、2026-07-26追加)。ChartCanvas.AudioTotalFrames
    /// と同じ値をMainWindowが設定し、全体スクロール範囲(=ミニマップの表示範囲)をChartCanvas側と
    /// 一致させる。</summary>
    public double? AudioTotalFrames { get; set; }

    private void OnDocumentChanged() => InvalidateVisual();

    public ChartMinimap()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));

        if (_document is null) return;
        var layout = _document.CurrentLayout;
        var tab = _document.CurrentTab;
        long maxTick = ChartCanvas.MaxTickInProject(_document, AudioTotalFrames);
        layout.RefreshContentHeight(maxTick);
        double contentHeight = layout.ContentHeight(maxTick);
        if (contentHeight <= 0) return;

        double YOf(long tick) => layout.TickToY(tick) / contentHeight * h;

        foreach (var lane in tab.Lanes)
        {
            foreach (var t in lane.Notes)
            {
                double y = YOf(t);
                dc.DrawRectangle(NoteBrush, null, new Rect(2, y, w - 4, 1.5));
            }
            foreach (var f in lane.Freezes)
            {
                double y1 = YOf(f.StartTick), y2 = YOf(f.EndTick);
                dc.DrawRectangle(FreezeBrush, null, new Rect(2, Math.Min(y1, y2), w - 4, Math.Max(1.5, Math.Abs(y2 - y1))));
            }
        }

        // 現在の表示範囲(ChartScrollViewer.VerticalOffset/ViewportHeight/ExtentHeightの比率をそのまま使う)。
        if (TargetScrollViewer is { ExtentHeight: > 0 } sv)
        {
            double top = sv.VerticalOffset / sv.ExtentHeight * h;
            double vh = Math.Max(2, sv.ViewportHeight / sv.ExtentHeight * h);
            dc.DrawRectangle(ViewportBrush, ViewportPen, new Rect(0.5, top, w - 1, vh));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        JumpTo(e.GetPosition(this).Y);
        RestoreFocus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
            JumpTo(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        RestoreFocus();
    }

    /// <summary>ミニマップはFocusable=falseのためクリックでフォーカスを奪うことは無いはずだが、
    /// 操作前から他コントロールにフォーカスが残っているケースに備え、操作の都度FocusTargetへ
    /// 明示的にフォーカスを戻す(2026-07-26)。</summary>
    private void RestoreFocus()
    {
        if (FocusTarget is null) return;
        Keyboard.Focus(FocusTarget);
    }

    /// <summary>クリック/ドラッグ位置(ミニマップ内Y、0=先頭tick)を、対応するChartScrollViewerの
    /// 垂直オフセットへ変換してスクロールする(クリック位置が画面中央に来るようセンタリング)。</summary>
    private void JumpTo(double y)
    {
        if (TargetScrollViewer is not { ExtentHeight: > 0 } sv) return;
        double h = Math.Max(1, ActualHeight);
        double proportion = Math.Clamp(y / h, 0, 1);
        double targetOffset = proportion * sv.ExtentHeight - sv.ViewportHeight / 2;
        sv.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, Math.Max(0, sv.ExtentHeight - sv.ViewportHeight)));
    }
}
