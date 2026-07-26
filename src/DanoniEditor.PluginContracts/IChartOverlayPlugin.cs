using System.Windows.Media;

namespace DanoniEditor.PluginContracts;

/// <summary>
/// 譜面ビュー上に独自のオーバーレイ描画を重ねるプラグイン(2026-07-26)。
/// 本体の描画(ノート・フリーズ・グリッド等)が完了した後、最前面に呼び出される。
/// </summary>
public interface IChartOverlayPlugin : IEditorPlugin
{
    /// <summary>譜面ビューの再描画のたびに呼ばれる。<paramref name="chart"/>はプロジェクトが
    /// 開かれていない場合や現在の難易度タブが取得できない場合にnullになりうる。</summary>
    void RenderOverlay(DrawingContext dc, PluginChartViewTransform transform, PluginChartContext? chart);
}
