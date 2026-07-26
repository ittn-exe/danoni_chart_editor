namespace DanoniEditor.PluginContracts;

/// <summary>1レーン分の読み取り専用情報(2026-07-26、プラグイン向けAPI)。</summary>
/// <param name="LaneId">テンプレート上のレーンID。</param>
/// <param name="KeyAssign">プレイテストで割り当てられている物理キーのラベル(複数キーは/区切り)。</param>
/// <param name="ColorGroup">テンプレートのcolorGroup値。</param>
/// <param name="NoteTicks">通常ノートのtick一覧(昇順とは限らない、元データ順)。</param>
/// <param name="FreezeTicks">フリーズノートの(開始tick, 終了tick)一覧。</param>
public sealed record PluginLaneInfo(
    string LaneId,
    string KeyAssign,
    int ColorGroup,
    IReadOnlyList<long> NoteTicks,
    IReadOnlyList<(long Start, long End)> FreezeTicks);

/// <summary>
/// 現在編集中の難易度タブを、Core層の内部モデルを直接公開せずに読み取り専用で渡すための窓口
/// (2026-07-26、プラグイン向けAPI)。内部実装(DifficultyTab等)が変わってもこの形が保たれる限り
/// プラグインは影響を受けない。
/// </summary>
public sealed class PluginChartContext
{
    public required string KeyTypeId { get; init; }
    public required string DifficultyName { get; init; }
    public required IReadOnlyList<PluginLaneInfo> Lanes { get; init; }

    /// <summary>tick→frame変換(TimingEngine.TickToFrame相当)。</summary>
    public required Func<long, double> TickToFrame { get; init; }

    /// <summary>frame→tick変換(TimingEngine.FrameToTick相当)。</summary>
    public required Func<double, long> FrameToTick { get; init; }
}
