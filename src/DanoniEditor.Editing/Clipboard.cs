namespace DanoniEditor.Editing;

/// <summary>ncolor_data個別色指定(NColorEntry)のスナップショット(2026-08-05)。tick等の同定情報を
/// 除いた値のみを持つ(ClipboardEntry側でTickOffset/Laneとして別管理するため二重に持たない)。</summary>
public readonly record struct ClipboardColor(
    string? Color, string? BandColor, bool AllFlag,
    string? ShadowColor, string? HitColor, string? HitBarColor, string? HitShadowColor);

/// <summary>コメント・警告フラグ(NoteAnnotation)のスナップショット(2026-08-05)。</summary>
public readonly record struct ClipboardAnnotation(string Comment, bool Warning);

/// <summary>
/// クリップボードへ保存する1オブジェクト分のスナップショット(仕様書13章: Ctrl+X/C/V、6.3上段「クリップボード系」)。
/// tickはコピー範囲内の最小tickからの相対値で持つ(貼り付け時は再生開始フレームを基準点として復元する、
/// 2026-08-04: 旧CurrentTick基準から変更)。
/// laneはNote/Freezeのみ意味を持ち、元のレーン番号をそのまま保持する(横方向の基準ずらしは行わない設計。
/// 7.2「拍/小節単位の内部管理」によりtickはBPMに依存しないため、そのままの相対関係で別プロジェクトへも
/// 違和感なく貼り付けられる)。DurationTicksはFreezeのみ、Valueはspeed/boost/BPMのみ、Commentはマーカーのみ使用する。
/// ColorOverride/Annotationは通常ノート・フリーズ(始点で同定)のみが持ちうる付随データで、
/// 2026-08-05要望対応: 「frame情報以外は全て保持してコピペしたい」に従い、Ctrl+C/V・Ctrl+ドラッグ複製
/// (CopyObjectsAction)の両方でここへ格納し、貼り付け/複製先へそのまま複製する。</summary>
public readonly record struct ClipboardEntry(
    ObjectKind Kind,
    int Lane,
    long TickOffset,
    long DurationTicks,
    double Value,
    string Comment,
    ClipboardColor? ColorOverride = null,
    ClipboardAnnotation? Annotation = null);

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
