namespace DanoniEditor.Editing;

/// <summary>
/// クリップボードへ保存する1オブジェクト分のスナップショット(仕様書13章: Ctrl+X/C/V、6.3上段「クリップボード系」)。
/// tickはコピー範囲内の最小tickからの相対値で持つ(貼り付け時にCurrentTickを基準点として復元する)。
/// laneはNote/Freezeのみ意味を持ち、元のレーン番号をそのまま保持する(横方向の基準ずらしは行わない設計。
/// 7.2「拍/小節単位の内部管理」によりtickはBPMに依存しないため、そのままの相対関係で別プロジェクトへも
/// 違和感なく貼り付けられる)。DurationTicksはFreezeのみ、Valueはspeed/boost/BPMのみ、Commentはマーカーのみ使用する。
/// </summary>
public readonly record struct ClipboardEntry(
    ObjectKind Kind,
    int Lane,
    long TickOffset,
    long DurationTicks,
    double Value,
    string Comment);

/// <summary>
/// プロセス内クリップボード(仕様書13章)。現状のアプリはウィンドウ・ドキュメントが常に1つのみのため、
/// static保持だけで「プロジェクトを跨いだ貼り付け」(7.2/8.1: 別プロジェクトを開き直した後もPasteできる)
/// を満たせる。マルチプロジェクトタブやマルチインスタンス対応(TBD#10)まで進んだ際は、
/// OSクリップボード(テキストシリアライズ経由)への差し替えを検討する。
/// </summary>
public static class EditorClipboard
{
    private static IReadOnlyList<ClipboardEntry>? _entries;

    /// <summary>貼り付け可能な内容を保持しているか(仕様書6.3上段「貼り付け」の有効化条件)</summary>
    public static bool HasContent => _entries is { Count: > 0 };

    public static IReadOnlyList<ClipboardEntry>? Entries => _entries;

    public static void Set(IReadOnlyList<ClipboardEntry> entries) => _entries = entries.Count > 0 ? entries : null;

    /// <summary>主にテスト用: クリップボードを空に戻す</summary>
    public static void Clear() => _entries = null;
}
