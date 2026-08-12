using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DanoniEditor.App;

/// <summary>
/// ボタン操作直後の一時的な「OK」フィードバック表示(2026-08-08新設、☆登録ボタン用)。
/// 「押したはずだが本当に登録されたか分かりづらい」というユーザー指摘への対応。指定のTextBlockを
/// 2秒間表示してから自動的に隠す(DispatcherTimerによるワンショット、都度使い捨てで生成するため
/// 連打してもタイマーが多重に残ることはあっても表示状態自体は最後のTickで正しくCollapsedになる)。
/// </summary>
internal static class TransientOkFeedback
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(2);

    public static void Show(TextBlock target)
    {
        target.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = DefaultDuration };
        timer.Tick += (_, _) =>
        {
            target.Visibility = Visibility.Collapsed;
            timer.Stop();
        };
        timer.Start();
    }
}
