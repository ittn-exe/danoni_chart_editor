using DanoniEditor.Editing;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// <see cref="IPluginHost"/>の実装(2026-07-26、プラグイン対応の土台)。プラグイン1つにつき
/// 1インスタンスを生成し、<paramref name="pluginId"/>でプラグイン専用データ(PluginData)の
/// 名前空間を自動的に分離する。
/// </summary>
internal sealed class PluginHostImpl(string pluginId, Func<EditorDocument?> getDocument, IPluginEditApi edit) : IPluginHost
{
    public PluginChartContext? CurrentChart => PluginChartContextBuilder.Build(getDocument());

    public IPluginEditApi Edit { get; } = edit;

    public event Action? ChartChanged;

    /// <summary>PluginManagerから、ドキュメント切替・編集の発生タイミングで呼ばれる。</summary>
    public void RaiseChartChanged() => ChartChanged?.Invoke();

    private string ScopedKey(string key) => $"{pluginId}.{key}";

    public string? GetPluginData(string key)
    {
        var doc = getDocument();
        if (doc is null) return null;
        return doc.Project.PluginData.TryGetValue(ScopedKey(key), out var value) ? value : null;
    }

    public void SetPluginData(string key, string value)
    {
        var doc = getDocument();
        if (doc is null) return;
        doc.Project.PluginData[ScopedKey(key)] = value;
        doc.NotifyChanged();
    }
}
