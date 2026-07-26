using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.SamplePlugin;

/// <summary>
/// プラグイン基盤(2026-07-26新設)の動作確認用サンプル。「11keyにおける右手移動の可視化」の
/// 相談をきっかけに検討した拡張点を、実際に一通り使い切る最小構成で実装した:
/// - <see cref="IEditorPanelPlugin"/>: 右パネルへ独自タブをドッキング
/// - <see cref="IChartOverlayPlugin"/>: 譜面ビューへオーバーレイ描画
/// - <see cref="IPluginHost.CurrentChart"/> / <see cref="IPluginHost.ChartChanged"/>: 読み取り専用API
/// - <see cref="IPluginHost.Edit"/>: Undo対応の書き込みAPI(ボタン1つの簡易デモ)
///
/// 「どのレーンが右手か」は本来ユーザーが設定できるべきだが、これは基盤の動作確認が目的の
/// サンプルのため、後半半分のレーンを仮の右手とみなす単純な固定ロジックに留めている
/// (実運用のプラグインではIPluginHost.SetPluginData等で設定を保存する想定)。
/// </summary>
public sealed class HandMovementSamplePlugin : IEditorPanelPlugin, IChartOverlayPlugin
{
    public string Id => "sample.handmovement";
    public string Name => "サンプル: 右手移動可視化";
    public string Version => "0.1.0";
    public string PanelTitle => "右手移動(サンプル)";

    private IPluginHost? _host;
    private TextBlock? _infoText;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.ChartChanged += RefreshInfoText;
    }

    public UIElement CreatePanel()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock
        {
            Text = "プラグイン基盤の動作確認用サンプルですわ。右パネルへのタブ差し込み・"
                 + "譜面ビューへのオーバーレイ描画・ノート配置APIの3点を確認できます。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _infoText = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_infoText);

        var button = new Button { Content = "レーン0 tick0 にテスト配置", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => _host?.Edit.PlaceNote(0, 0);
        panel.Children.Add(button);

        RefreshInfoText();
        return panel;
    }

    private void RefreshInfoText()
    {
        if (_infoText is null) return;
        var chart = _host?.CurrentChart;
        _infoText.Text = chart is null
            ? "(プロジェクト未取得)"
            : $"キー種: {chart.KeyTypeId}k / 難易度: {chart.DifficultyName} / レーン数: {chart.Lanes.Count}";
    }

    private static readonly Brush RightHandBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x40, 0xC0, 0xFF));

    public void RenderOverlay(DrawingContext dc, PluginChartViewTransform transform, PluginChartContext? chart)
    {
        if (chart is null || chart.Lanes.Count == 0) return;
        int rightHandStart = chart.Lanes.Count / 2;
        double radius = transform.NoteLaneWidth * 0.15;

        for (int lane = rightHandStart; lane < chart.Lanes.Count; lane++)
        {
            double x = transform.LaneToScreenX(lane);
            foreach (var tick in chart.Lanes[lane].NoteTicks)
            {
                double y = transform.TickToScreenY(tick);
                if (y < -radius || y > transform.ViewportHeight + radius) continue; // 画面外はカリング
                dc.DrawEllipse(RightHandBrush, null, new Point(x, y), radius, radius);
            }
        }
    }
}
