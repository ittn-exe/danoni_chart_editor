using DanoniEditor.Editing;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// プラグインの読み込み・初期化・アクティブなドキュメントへの追従をまとめて管理する
/// (2026-07-26、プラグイン対応の土台)。MainWindowから見た唯一の入口。
/// </summary>
internal sealed class PluginManager
{
    public IReadOnlyList<IEditorPlugin> Plugins { get; }
    public IReadOnlyList<IEditorPanelPlugin> PanelPlugins { get; }
    public IReadOnlyList<IChartOverlayPlugin> OverlayPlugins { get; }
    public IReadOnlyList<string> LoadErrors => _errors;

    private readonly List<string> _errors = [];
    private readonly List<PluginHostImpl> _hosts = [];
    private readonly Func<EditorDocument?> _getDocument;
    private EditorDocument? _subscribed;

    public PluginManager(Func<EditorDocument?> getDocument)
    {
        _getDocument = getDocument;

        var loaded = PluginLoader.LoadAll(AppPaths.PluginsDir);
        _errors.AddRange(loaded.Errors);

        var editApi = new PluginEditApiImpl(getDocument);
        var initialized = new List<IEditorPlugin>();
        foreach (var plugin in loaded.Plugins)
        {
            try
            {
                var host = new PluginHostImpl(plugin.Id, getDocument, editApi);
                plugin.Initialize(host);
                _hosts.Add(host);
                initialized.Add(plugin);
            }
            catch (Exception ex)
            {
                var msg = $"{plugin.Id}: 初期化に失敗しましたわ({ex.Message})";
                _errors.Add(msg);
                PluginLog.Write(msg);
            }
        }

        Plugins = initialized;
        PanelPlugins = [.. initialized.OfType<IEditorPanelPlugin>()];
        OverlayPlugins = [.. initialized.OfType<IChartOverlayPlugin>()];
        PluginLog.Write($"起動時読み込み完了: {initialized.Count}件成功、{_errors.Count}件エラー(パネル{PanelPlugins.Count}件・オーバーレイ{OverlayPlugins.Count}件)");
    }

    /// <summary>ドキュメントの切替(セッション切替・新規作成・プロジェクトクローズ等)や、
    /// 譜面データの編集が発生したタイミングでMainWindow側から呼ぶ。アクティブなドキュメントが
    /// 変わった場合はChanged購読を張り替え、いずれの場合も全プラグインへ通知する。</summary>
    public void NotifyDocumentChanged()
    {
        var doc = _getDocument();
        if (!ReferenceEquals(_subscribed, doc))
        {
            if (_subscribed is not null) _subscribed.Changed -= NotifyDocumentChanged;
            _subscribed = doc;
            if (doc is not null) doc.Changed += NotifyDocumentChanged;
        }
        foreach (var host in _hosts) host.RaiseChartChanged();
    }
}
