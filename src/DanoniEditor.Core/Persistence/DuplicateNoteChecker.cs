using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Persistence;

/// <summary>プロジェクト内で、同一レーン・同一tickに通常ノートが複数個重なっている箇所1件分の情報
/// (2026-08-08新設)。Countは重複件数(2件重なっていればCount=2)。</summary>
public readonly record struct DuplicateNoteLocation(int TabIndex, int Lane, long Tick, int Count);

/// <summary>
/// プロジェクト読込時、通常ノートの重複配置(同一レーン・同一tickに複数件、仕様上あってはならない
/// 状態だがList&lt;long&gt;であるため過去のツール不具合等により実在し得る)を検出・解消する
/// (2026-08-08新設、第三者報告のノート重複不具合の修正に伴うユーザー確定仕様)。
///
/// 過去に作成されたプロジェクトファイルが、気付かないまま重複ノートを含んだ状態で保存されている
/// ケースを想定し、読込時に検出した場合はUI側で「重複を解消(2件目以降を削除)」/
/// 「重複を維持(該当箇所へ警告アイコンを設定)」をユーザーに選ばせる。WPF非依存(Core層)。
/// </summary>
public static class DuplicateNoteChecker
{
    /// <summary>この機能が自動設定するコメント文言(重複を維持した場合、警告アイコンに添える
    /// コメントとしてNoteAnnotation.Commentへ記録する。確認ダイアログの説明文とも文言を揃える)。</summary>
    public const string WarningComment = "読込時に同じ位置へ複数のノートが重なっていることを検出しました。";

    /// <summary>プロジェクト内の全タブ・全レーンを走査し、通常ノート(Notes)が同一tickに複数回
    /// 記録されている箇所を検出する。1箇所につき1件、発見順(タブ→レーン→tick昇順)で返す。</summary>
    public static IReadOnlyList<DuplicateNoteLocation> FindDuplicates(ChartProject project)
    {
        var result = new List<DuplicateNoteLocation>();
        for (int tabIndex = 0; tabIndex < project.Tabs.Count; tabIndex++)
        {
            var lanes = project.Tabs[tabIndex].Lanes;
            for (int lane = 0; lane < lanes.Count; lane++)
            {
                var counts = new Dictionary<long, int>();
                foreach (var tick in lanes[lane].Notes)
                    counts[tick] = counts.GetValueOrDefault(tick) + 1;

                foreach (var (tick, count) in counts.OrderBy(kv => kv.Key))
                    if (count > 1) result.Add(new DuplicateNoteLocation(tabIndex, lane, tick, count));
            }
        }
        return result;
    }

    /// <summary>「重複を解消」: 各重複箇所につき2件目以降を削除し、1件だけ残す
    /// (色指定・コメント等の付随データは1件目=最初にヒットした実体にそのまま残るため触らない)。</summary>
    public static void ResolveByRemoving(ChartProject project, IReadOnlyList<DuplicateNoteLocation> duplicates)
    {
        foreach (var d in duplicates)
        {
            var notes = project.Tabs[d.TabIndex].Lanes[d.Lane].Notes;
            int remove = d.Count - 1;
            // 末尾側から取り除く(先頭の1件だけを実体として残す)
            for (int i = notes.Count - 1; i >= 0 && remove > 0; i--)
            {
                if (notes[i] != d.Tick) continue;
                notes.RemoveAt(i);
                remove--;
            }
        }
    }

    /// <summary>「重複を維持」: 各重複箇所へ警告アイコンを設定する(NoteAnnotation.Warning=true)。
    /// 既にコメント/表示アイコン設定がある場合はそれを保持し、Warningのみtrueへ上書きする
    /// (インポート時の自動フラグ付けと同じ方針、仕様書のNoteAnnotationコメント参照)。</summary>
    public static void ResolveByMarking(ChartProject project, IReadOnlyList<DuplicateNoteLocation> duplicates)
    {
        foreach (var d in duplicates)
        {
            var annotations = project.Tabs[d.TabIndex].Lanes[d.Lane].Annotations;
            var existing = annotations.FirstOrDefault(a => a.Tick == d.Tick);
            annotations.RemoveAll(a => a.Tick == d.Tick);
            annotations.Add(existing is null
                ? new NoteAnnotation(d.Tick, WarningComment, Warning: true)
                : existing with { Warning = true });
        }
    }
}
