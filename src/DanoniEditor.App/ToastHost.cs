using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DanoniEditor.App;

/// <summary>通知の重要度。Infoは状態バーのみ、Warning/Errorは状態バー+ポップアップで知らせる。</summary>
internal enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// メイン画面の右下に重ねて表示する通知ポップアップ(2026-10-06)。ウィンドウ内オーバーレイとして
/// 実装し、OSの通知機能は使わない。各通知は右上の×ボタンで閉じるまで表示され続ける
/// (自動では消えない)。複数の通知は縦に積み重ね、同じキーの通知が既に表示中なら重複して出さない。
/// 通知以外の領域はマウス操作を透過する(背後のエディタを操作できる)。
/// </summary>
internal sealed class ToastHost : StackPanel
{
    private const int MaxToasts = 5;

    private readonly Dictionary<string, Border> _byKey = new();

    private static readonly ControlTemplate CloseButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='bd' Background='Transparent' CornerRadius='3' Width='22' Height='22'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#44FFFFFF'/></Trigger>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    private static readonly ControlTemplate ActionButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='bd' Background='#33FFFFFF' CornerRadius='3' Padding='10,3'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#55FFFFFF'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    public ToastHost()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        // 状態バーに被らないよう、下に余白を取る
        Margin = new Thickness(0, 0, 16, 36);
        MaxWidth = 420;
        Panel.SetZIndex(this, 1000);
    }

    /// <summary>通知を表示する。keyが同じ通知が既に表示中の場合は何もしない(重複表示の防止)。
    /// 呼び出しスレッドは問わない(UIスレッド以外ならUIスレッドへ回す)。</summary>
    public void Show(NotificationLevel level, string message, string? key = null,
                     string? actionText = null, Action? action = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(level, message, key, actionText, action));
            return;
        }

        key ??= $"{level}:{message}";
        if (_byKey.ContainsKey(key)) return;

        var toast = BuildToast(level, message, key, actionText, action);
        _byKey[key] = toast;
        Children.Add(toast);

        while (Children.Count > MaxToasts)
            Remove((Border)Children[0]);

        toast.Opacity = 0;
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>表示中の通知をすべて閉じる。</summary>
    public void DismissAll()
    {
        foreach (var t in Children.OfType<Border>().ToList()) Remove(t);
    }

    private void Remove(Border toast)
    {
        Children.Remove(toast);
        if (toast.Tag is string key) _byKey.Remove(key);
    }

    private static (Color Accent, string Glyph) Style(NotificationLevel level) => level switch
    {
        NotificationLevel.Error => (Color.FromRgb(0xE5, 0x48, 0x4D), "×"),
        NotificationLevel.Warning => (Color.FromRgb(0xF5, 0xA6, 0x23), "!"),
        _ => (Color.FromRgb(0x4C, 0x9B, 0xF5), "i"),
    };

    private Border BuildToast(NotificationLevel level, string message, string key, string? actionText, Action? action)
    {
        var (accent, glyph) = Style(level);

        var icon = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph, Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new TextBlock
        {
            Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var toast = new Border
        {
            Tag = key,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2E)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(0, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 8, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.45 },
        };

        var close = new Button
        {
            Content = "✕", Template = CloseButtonTemplate, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            ToolTip = "閉じる", VerticalAlignment = VerticalAlignment.Top, Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "通知を閉じる");
        close.Click += (_, _) => Remove(toast);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(close);

        UIElement content = grid;
        if (actionText is not null && action is not null)
        {
            var actionButton = new Button
            {
                Content = actionText, Template = ActionButtonTemplate, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 8, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            actionButton.Click += (_, _) =>
            {
                Remove(toast);
                action();
            };
            var panel = new StackPanel();
            panel.Children.Add(grid);
            panel.Children.Add(actionButton);
            content = panel;
        }

        toast.Child = content;
        return toast;
    }
}
