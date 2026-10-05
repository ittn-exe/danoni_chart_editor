namespace DanoniEditor.App;

// 通知(状態バー/ポップアップ)の振り分け(2026-10-06)。
//   Info    : 状態バーのみ(操作結果など、気付かなくても困らないもの)
//   Warning : 状態バー+ポップアップ(対処すれば直る問題。音源ファイルが見つからない等)
//   Error   : 状態バー+ポップアップ(機能が働かなかった・データに影響しうるもの。読み込み失敗、自動保存失敗等)
public partial class MainWindow
{
    private void Notify(NotificationLevel level, string message, string? key = null,
                        string? actionText = null, Action? action = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Notify(level, message, key, actionText, action));
            return;
        }

        var firstLine = message.Split('\n')[0];
        StatusText.Text = firstLine;

        if (level >= NotificationLevel.Warning)
            Toasts.Show(level, message, key, actionText, action);
    }
}
