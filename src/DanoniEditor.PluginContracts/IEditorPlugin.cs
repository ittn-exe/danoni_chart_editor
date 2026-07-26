namespace DanoniEditor.PluginContracts;

/// <summary>
/// 全プラグインが実装する基底インターフェース(2026-07-26、プラグイン対応の土台)。
/// これ単体では何もしないため、実際には <see cref="IEditorPanelPlugin"/> や
/// <see cref="IChartOverlayPlugin"/> など、機能ごとの派生インターフェースと組み合わせて実装する
/// (1つのプラグインが複数の派生インターフェースを同時に実装しても構わない)。
/// </summary>
public interface IEditorPlugin
{
    /// <summary>プラグインを一意に識別するID(半角英数字推奨)。<see cref="IPluginHost.GetPluginData"/>等の
    /// 名前空間分けに使われるため、他プラグインと重複しない値にすること。</summary>
    string Id { get; }

    /// <summary>UI上に表示するプラグイン名(日本語可)。</summary>
    string Name { get; }

    /// <summary>プラグイン自身のバージョン文字列(表示用、エディタ側では特に検証しない)。</summary>
    string Version { get; }

    /// <summary>読み込み直後に一度だけ呼ばれる。以後の全ての本体とのやり取りは
    /// <paramref name="host"/>経由で行う。</summary>
    void Initialize(IPluginHost host);
}
