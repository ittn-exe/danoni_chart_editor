using System.Text;

namespace DanoniEditor.App;

/// <summary>
/// 目視テスト開始時の音声再生状態を追跡する診断情報(2026-09-07要望対応)。
///
/// 「Spaceで目視テストを開始しても無音のまま・再生位置ラインが動かない・自動終了もしない」という
/// 再現方法不明な不具合の切り分けのため、StartVisualTest()呼び出しのたびに新規作成し、
/// PlaybackTimer_Tickで進捗を書き足していく。コード修正無しで次回発生時の内部状態を確認できるよう、
/// 「設定 > 環境報告作成」の出力(DiagnosticsReport)へ直近1回分をそのまま含める。
/// </summary>
internal sealed class VisualTestDiagnostics
{
    // --- 開始時点のスナップショット ---
    public DateTime StartedAtUtc { get; init; }
    public double RequestedStartFrame { get; init; }
    public bool AudioLoadedAtStart { get; init; }
    public double? DurationSecondsAtStart { get; init; }
    public bool KeyboardModeActiveAtStart { get; init; }
    public bool SplitViewEnabledAtStart { get; init; }
    public bool HandClapEnabledAtStart { get; init; }
    public string OutputStateAtStart { get; init; } = "";
    public bool PlayingFlagAtStart { get; init; }

    // --- PlaybackTimer_Tick側で随時更新する進捗 ---
    /// <summary>PlaybackTimer_Tick自体が呼ばれた回数(_document/_audioPlayer.Durationのnullチェック含む、
    /// タイマー自体が回っているかどうかの確認用)。</summary>
    public int TickInvokedCount { get; set; }
    /// <summary>↑のうちnullチェックで早期returnせず、実際に再生位置を読みに行った回数。</summary>
    public int TickProcessedCount { get; set; }
    public double? FirstTickPositionSeconds { get; set; }
    public double LatestPositionSeconds { get; set; }
    public DateTime LatestSampledAtUtc { get; set; }
    public string LatestOutputState { get; set; } = "";
    public bool LatestPlayingFlag { get; set; }
    /// <summary>WASAPI自動復旧(2026-09-13要望対応、PlaybackTimer_Tick参照)が発動した回数。
    /// 1以上なら、この目視テスト中に出力ストリームのスタックを検知・再構築したことを示す。</summary>
    public int AutoRecoveryCount { get; set; }

    // --- 終了時点 ---
    public DateTime? StoppedAtUtc { get; set; }
    public string? StopReason { get; set; }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"開始試行日時(UTC): {StartedAtUtc:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"要求した再生開始フレーム: {RequestedStartFrame:0.##}F");
        sb.AppendLine($"開始時_audioLoaded: {AudioLoadedAtStart}");
        sb.AppendLine($"開始時Duration: {(DurationSecondsAtStart is { } d ? $"{d:0.###}秒" : "(null、未読込)")}");
        sb.AppendLine($"開始時キーボードモード: {KeyboardModeActiveAtStart}");
        sb.AppendLine($"開始時分割ビュー: {SplitViewEnabledAtStart}");
        sb.AppendLine($"開始時ハンドクラップ有効: {HandClapEnabledAtStart}");
        sb.AppendLine($"開始時WASAPI出力状態: {OutputStateAtStart}");
        sb.AppendLine($"開始時_playingフラグ: {PlayingFlagAtStart}");
        sb.AppendLine();
        sb.AppendLine($"PlaybackTimer_Tick呼び出し回数: {TickInvokedCount}");
        sb.AppendLine($"うち実処理まで進んだ回数: {TickProcessedCount}" +
            (TickInvokedCount > 0 && TickProcessedCount == 0 ? " (※早期returnし続けています)" : ""));
        sb.AppendLine($"最初のTickでの再生位置: {(FirstTickPositionSeconds is { } f ? $"{f:0.###}秒" : "(未到達)")}");
        sb.AppendLine($"最新の再生位置: {LatestPositionSeconds:0.###}秒 (サンプル時刻UTC: {LatestSampledAtUtc:HH:mm:ss.fff})");
        sb.AppendLine($"最新WASAPI出力状態: {LatestOutputState}");
        sb.AppendLine($"最新_playingフラグ: {LatestPlayingFlag}");
        sb.AppendLine($"自動復旧の発動回数: {AutoRecoveryCount}");
        if (FirstTickPositionSeconds is { } first)
        {
            double delta = LatestPositionSeconds - first;
            sb.AppendLine($"最初のTickから最新までの再生位置の進み: {delta:0.###}秒 " +
                (delta > 0.05 ? "(進行あり)" : "(ほぼ停止、要注意)"));
        }
        sb.AppendLine();
        sb.AppendLine(StoppedAtUtc is { } stopped
            ? $"終了日時(UTC): {stopped:yyyy-MM-dd HH:mm:ss.fff} / 理由: {StopReason}"
            : "終了記録なし(目視テストが開始状態のまま、またはこの記録より後に再開されていません)");
        return sb.ToString();
    }
}
