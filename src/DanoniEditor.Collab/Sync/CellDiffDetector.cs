using DanoniEditor.Collab.Protocol;
using DanoniEditor.Core.Models;

namespace DanoniEditor.Collab.Sync;

/// <summary>
/// <see cref="DifficultyTab"/>の「編集前」と「編集後」の状態を比較し、実際に変化したセルぶんの
/// <see cref="CollabMessage"/>を列挙する(設計メモ2.2節、真のセル差分。2026-09-20実装)。
///
/// EditActions.cs側の各操作へ「自分がどのセルを変更したか」を報告させる改修は工数が大きいため、
/// 代わりに<see cref="DanoniEditor.Editing.EditorDocument.BeforeEdit"/>/AfterEditの前後で
/// このクラスがタブ全体の状態を比較する方式を採用している(EditActions.cs自体は一切改修しない)。
/// タブあたりのノート・フリーズ総数が極端に多くない限り、1ジェスチャごとの比較コストは軽微。
/// </summary>
public static class CellDiffDetector
{
    /// <summary>1レーンぶんの「編集前」状態のコピー(参照ではなく値のスナップショット)。</summary>
    public sealed record LaneSnapshot(List<long> NoteTicks, List<FreezeNote> Freezes);

    /// <summary>指定タブの現在状態をスナップショットする(BeforeEdit時点で呼ぶ)。</summary>
    public static List<LaneSnapshot> Capture(DifficultyTab tab) =>
        tab.Lanes
            .Select(lane => new LaneSnapshot(
                [.. lane.Notes],
                [.. lane.Freezes.Select(f => new FreezeNote(f.StartTick, f.EndTick))]))
            .ToList();

    /// <summary>
    /// beforeとtabの現在状態(=after)を比較し、変化があったセルぶんのメッセージを列挙する。
    /// tabIndexは呼び出し側が付与する(DifficultyTab自身は自分が何番目のタブかを知らないため)。
    /// レーン数がbefore採取時から変わっている(テンプレート変更等の稀なケース)場合、共通する
    /// 範囲のみを比較対象とする(超過分は無視、実害は無い想定)。
    /// </summary>
    /// <summary>authorParticipantIdは、この差分を生成した張本人(ローカル編集者)のID
    /// (ノート所有者アイコン、設計メモ6.4節)。ホスト自身の編集はホスト自身のID、ゲストの編集は
    /// そのゲスト自身のIDを渡す想定(CollabSessionController.OnAfterLocalEdit参照)。</summary>
    public static IEnumerable<CollabMessage> Diff(int tabIndex, IReadOnlyList<LaneSnapshot> before, DifficultyTab tab, string authorParticipantId)
    {
        var laneCount = Math.Min(before.Count, tab.Lanes.Count);
        for (var laneIndex = 0; laneIndex < laneCount; laneIndex++)
        {
            var beforeLane = before[laneIndex];
            var afterLane = tab.Lanes[laneIndex];

            var beforeNotes = beforeLane.NoteTicks.ToHashSet();
            var afterNotes = afterLane.Notes.ToHashSet();
            foreach (var removedTick in beforeNotes)
                if (!afterNotes.Contains(removedTick))
                    yield return CellDiffApplier.CreateNoteCellMessage(tabIndex, laneIndex, removedTick, present: false, authorParticipantId);
            foreach (var addedTick in afterNotes)
                if (!beforeNotes.Contains(addedTick))
                    yield return CellDiffApplier.CreateNoteCellMessage(tabIndex, laneIndex, addedTick, present: true, authorParticipantId);

            var beforeFreezes = beforeLane.Freezes.ToDictionary(f => f.StartTick, f => f.EndTick);
            var afterFreezes = afterLane.Freezes.ToDictionary(f => f.StartTick, f => f.EndTick);
            foreach (var (startTick, _) in beforeFreezes)
                if (!afterFreezes.ContainsKey(startTick))
                    yield return CellDiffApplier.CreateFreezeRemoveMessage(tabIndex, laneIndex, startTick, authorParticipantId);
            foreach (var (startTick, endTick) in afterFreezes)
                if (!beforeFreezes.TryGetValue(startTick, out var beforeEndTick) || beforeEndTick != endTick)
                    yield return CellDiffApplier.CreateFreezeSetMessage(tabIndex, laneIndex, startTick, endTick, authorParticipantId);
        }
    }
}
