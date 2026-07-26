using System.Windows;

namespace DanoniEditor.PluginContracts;

/// <summary>
/// 右パネルへ独自タブとしてドッキングするUIを提供するプラグイン(2026-07-26)。
/// </summary>
public interface IEditorPanelPlugin : IEditorPlugin
{
    /// <summary>右パネルのタブ見出しに表示する文字列。</summary>
    string PanelTitle { get; }

    /// <summary>タブの中身として表示するUI(自由な設定画面等)を1つ生成する。
    /// エディタ側はこの戻り値をそのままTabItem.Contentへ設定するだけで、内部の
    /// レイアウト・状態管理は全てプラグイン側の責任とする。呼ばれるのはアプリ起動時に1回だけ。</summary>
    UIElement CreatePanel();
}
