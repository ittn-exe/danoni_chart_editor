using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace DanoniEditor.App;

/// <summary>
/// 設定メニューの「バージョン情報」ダイアログ(2026-07-31)。バージョンはDanoniEditor.App.csprojの
/// &lt;Version&gt;(AssemblyInformationalVersion経由、DiagnosticsReport.csと同じ取得方法)をそのまま表示する。
/// </summary>
internal sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "IDE (ITTN-DANONI-EDITOR)";
        Width = 380;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "IDE (ITTN-DANONI-EDITOR)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"バージョン: {GetVersionText()}",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "2026 K-AN / ITTN.EXE",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        });

        var closeButton = new Button
        {
            Content = "閉じる",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsCancel = true,
        };
        closeButton.Click += (_, _) => Close();
        panel.Children.Add(closeButton);

        Content = panel;
    }

    /// <summary>csprojの&lt;Version&gt;(AssemblyInformationalVersion)を読む。
    /// DiagnosticsReport.csと同じ取得方法・フォールバック順(2026-07-26パターン踏襲)。</summary>
    private static string GetVersionText() =>
        typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AboutWindow).Assembly.GetName().Version?.ToString()
        ?? "(不明)";
}
