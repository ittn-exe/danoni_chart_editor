namespace DanoniEditor.PluginContracts;

/// <summary>
/// 譜面ビューの現在の座標変換をプラグインへ橋渡しするための窓口(2026-07-26)。
/// ChartLayout(DanoniEditor.Editing)の内部実装をそのまま公開する代わりに、
/// プラグインが自前でオーバーレイを正しい位置に描けるよう最小限の変換関数だけを渡す。
/// </summary>
public sealed class PluginChartViewTransform
{
    /// <summary>tickを譜面ビュー上のY座標(px)へ変換する。</summary>
    public required Func<long, double> TickToScreenY { get; init; }

    /// <summary>ノートレーンindex(0始まり)を、そのレーン列の中心X座標(px)へ変換する。</summary>
    public required Func<int, double> LaneToScreenX { get; init; }

    /// <summary>ノートレーン1本分の幅(px)。</summary>
    public required double NoteLaneWidth { get; init; }

    /// <summary>現在表示中のビューポート幅・高さ(px、譜面ビューのスクロール可視領域)。</summary>
    public required double ViewportWidth { get; init; }
    public required double ViewportHeight { get; init; }

    /// <summary>現在Reverse表示(tick0を下端にする表示)が有効かどうか。</summary>
    public required bool IsReverse { get; init; }
}
