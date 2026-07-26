using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.PluginContracts;

namespace MyPlugin; // TODO: 好きな名前空間へ変更してください

/// <summary>
/// プラグイン雛形です。以下のTODOを埋めてから使い始めてください。
///
/// - IEditorPanelPluginだけ使いたい場合は「: IEditorPlugin, IEditorPanelPlugin」に、
/// - IChartOverlayPluginだけ使いたい場合は「: IEditorPlugin, IChartOverlayPlugin」に、
/// どちらも要らない場合はそのメンバーごと削除して構いません(IEditorPluginは必須)。
///
/// 詳しい仕様は plugin-sdk/docs/plugin_api_reference.md (または .html) を参照してください。
/// 動く実例が必要な場合は plugin-sdk/sample/ 以下のHandMovementSamplePluginも参照できます。
/// </summary>
public sealed class MyPlugin : IEditorPlugin, IEditorPanelPlugin, IChartOverlayPlugin
{
    // TODO: 他のプラグインと重複しない一意なIDへ変更してください(半角英数字推奨)
    public string Id => "yourname.myplugin";

    // TODO: UI上に表示する名前
    public string Name => "マイプラグイン";

    // TODO: バージョン文字列(本体側は検証しません)
    public string Version => "0.1.0";

    private IPluginHost? _host;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        // TODO: 必要なら host.ChartChanged += ... で状態変化の通知を受け取れます
    }

    // ===== 右パネルへのUI差し込み(IEditorPanelPlugin) =====
    // 不要な場合は、このセクションごと・宣言部の「IEditorPanelPlugin」ごと削除してください。

    public string PanelTitle => "マイプラグイン"; // TODO: タブ見出し

    public UIElement CreatePanel()
    {
        // TODO: ここに実際のUIを組み立ててください。以下はダミー表示です。
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = "ここにプラグインのUIを実装してください。" });
        return panel;
    }

    // ===== 譜面ビューへのオーバーレイ描画(IChartOverlayPlugin) =====
    // 不要な場合は、このセクションごと・宣言部の「IChartOverlayPlugin」ごと削除してください。

    public void RenderOverlay(DrawingContext dc, PluginChartViewTransform transform, PluginChartContext? chart)
    {
        // TODO: ここに実際の描画処理を実装してください。chartはnullになりうる点に注意。
        if (chart is null) return;
    }
}
