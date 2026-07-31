using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Editing;

/// <summary>
/// コピーマネージャー(異なるキー種間のコピー&ペースト、2026-07-31)で発生しうる、通常ノート・
/// フリーズアロー同士の衝突をどう解決するかの設定値。AppSettingsのCopyManagerXxxModeプロパティを
/// そのまま受け渡す薄いレコード(Editing層はAppSettingsを直接参照しないための橋渡し)。
/// </summary>
public sealed record PasteConflictOptions(
    string NoteVsFreezeHeadMode,     // "note" | "freeze"
    string NoteVsFreezeBodyMode,     // "trim" | "ignoreNote"
    string NoteVsFreezeTailMode,     // "trim" | "ignoreNote"
    string FreezeHeadVsHeadMode,     // "long" | "short"
    string FreezeHeadVsBodyMode,     // "trim" | "ignoreFreeze"
    string FreezeHeadVsTailMode)     // "trim" | "ignoreFreeze"
{
    /// <summary>AppSettingsの既定値と同じ組み合わせ(テスト・フォールバック用)</summary>
    public static PasteConflictOptions Default { get; } = new("note", "trim", "trim", "long", "trim", "trim");
}

/// <summary>PasteLaneStateが行った1件の変更操作(呼び出し側がIEditActionへ変換するための記録、2026-07-31)。
/// 発生順にPasteLaneState.Operationsへ積まれる。</summary>
public abstract record PasteLaneOp;

/// <summary>新規ノートを配置した(コピーで貼り付けられたノート)</summary>
public sealed record AddNoteOp(long Tick) : PasteLaneOp;

/// <summary>既存の通常ノートを衝突解決のため削除した</summary>
public sealed record RemoveExistingNoteOp(long Tick) : PasteLaneOp;

/// <summary>新規フリーズを配置した(コピーで貼り付けられたフリーズ。衝突解決による短縮後の範囲)</summary>
public sealed record AddFreezeOp(long StartTick, long EndTick) : PasteLaneOp;

/// <summary>既存のフリーズを衝突解決のため丸ごと削除した(短縮では足りない/両端一致で敗れた等)</summary>
public sealed record RemoveExistingFreezeOp(long StartTick) : PasteLaneOp;

/// <summary>既存のフリーズの終端を衝突解決のため短縮した(始点は変わらない=色・コメント等の
/// 付随データは引き継ぎ不要でそのまま有効)</summary>
public sealed record TrimExistingFreezeOp(FreezeNote Original, long NewEndTick) : PasteLaneOp;

/// <summary>
/// コピーマネージャーによる貼り付け時、1レーン分の通常ノート・フリーズを衝突解決しながら段階的に
/// 配置していくための作業状態(2026-07-31)。「既にチャートへ存在するデータ」と「今回の貼り付けで
/// 追加中のオブジェクト」を区別せず1つの状態として扱う(貼り付けバッチ内部での衝突〈複数のコピー元
/// レーンが同じ貼り付け先レーンへ割り当てられた場合等〉も、既存データとの衝突も、同じロジックで解決する)。
///
/// 呼び出し側は既存のNotes/Freezesから本クラスを構築し、tick順にTryPlaceNote/TryPlaceFreezeを
/// 呼び出していく。各呼び出しはこの状態(Notes/Freezes)を直接書き換えつつ、発生した変更をOperations
/// へ記録する。呼び出し側はOperationsを順番にたどるだけで、正しい順序(既存オブジェクトの削除/短縮を
/// 新規オブジェクトの配置より必ず先に行う)のIEditAction列を組み立てられる。
/// </summary>
public sealed class PasteLaneState
{
    /// <summary>16分音符1つ分のtick数(4拍子基準)。「16分手前で切る」系ルールで使用する。</summary>
    public static readonly long Sixteenth = 4L * TimingEngine.TicksPerBeat / 16;

    public List<long> Notes { get; }
    public List<FreezeNote> Freezes { get; }

    /// <summary>この状態に対して行った変更の記録(発生順)。</summary>
    public List<PasteLaneOp> Operations { get; } = [];

    public PasteLaneState(IEnumerable<long> existingNotes, IEnumerable<FreezeNote> existingFreezes)
    {
        Notes = [.. existingNotes];
        Freezes = [.. existingFreezes];
    }

    /// <summary>通常ノートを1件、衝突解決しながら配置を試みる。配置した場合はtrue(Operationsの末尾が
    /// 必ずAddNoteOpになる)、見送った(無視した)場合はfalseを返す。</summary>
    public bool TryPlaceNote(long tick, PasteConflictOptions opt)
    {
        // 同一レーン同一tickの通常ノート同士の重複は、常に2件目以降をパスする(元仕様通り)
        if (Notes.Contains(tick)) return false;

        // --- 通常ノート + フリーズ先頭(完全一致) ---
        var headHit = Freezes.FirstOrDefault(f => f.StartTick == tick);
        if (headHit is not null)
        {
            if (opt.NoteVsFreezeHeadMode == "freeze") return false; // フリーズを優先、ノートは破棄
            RemoveExistingFreeze(headHit); // 通常ノートを優先、フリーズ先頭を破棄
            AddNote(tick);
            return true;
        }

        // --- 通常ノート + フリーズ終端(完全一致) ---
        var tailHit = Freezes.FirstOrDefault(f => f.EndTick == tick);
        if (tailHit is not null)
        {
            if (opt.NoteVsFreezeTailMode == "ignoreNote") return false;
            TrimOrRemoveExistingFreeze(tailHit, tick);
            AddNote(tick);
            return true;
        }

        // --- 通常ノート + フリーズ帯(始点・終点を除く区間内部) ---
        var bodyHit = Freezes.FirstOrDefault(f => tick > f.StartTick && tick < f.EndTick);
        if (bodyHit is not null)
        {
            if (opt.NoteVsFreezeBodyMode == "ignoreNote") return false;
            TrimOrRemoveExistingFreeze(bodyHit, tick);
            AddNote(tick);
            return true;
        }

        AddNote(tick);
        return true;
    }

    /// <summary>フリーズアローを1件、衝突解決しながら配置を試みる(始点start・終点end、start&lt;end前提)。
    /// 配置した場合はtrue(Operationsの末尾が必ずAddFreezeOpになる)、見送った場合はfalseを返す。</summary>
    public bool TryPlaceFreeze(long start, long end, PasteConflictOptions opt)
    {
        if (end <= start) return false;

        // ============================================================
        // 1. 新規フリーズの「先頭(start)」が何かと衝突していないかを解決する
        // ============================================================

        // --- 通常ノート + フリーズ先頭 ---
        if (Notes.Contains(start))
        {
            if (opt.NoteVsFreezeHeadMode == "note") return false; // 通常ノートを優先、フリーズは破棄
            RemoveExistingNote(start); // フリーズを優先、既存の通常ノートを破棄
        }

        // --- フリーズ先頭 + フリーズ先頭(完全一致) ---
        var sameHead = Freezes.FirstOrDefault(f => f.StartTick == start);
        if (sameHead is not null)
        {
            long existingLen = sameHead.EndTick - sameHead.StartTick;
            long newLen = end - start;
            bool keepExisting = opt.FreezeHeadVsHeadMode == "long"
                ? existingLen >= newLen
                : existingLen <= newLen;
            if (keepExisting) return false; // 既存を残す=新規フリーズを配置しない
            RemoveExistingFreeze(sameHead); // 新規を残す=既存フリーズ先頭を破棄
        }

        // --- フリーズ先頭 + フリーズ帯(既存フリーズの帯に新規の先頭が刺さる=新規側が「後ろ」) ---
        var startInBody = Freezes.FirstOrDefault(f => start > f.StartTick && start < f.EndTick);
        if (startInBody is not null)
        {
            if (opt.FreezeHeadVsBodyMode == "ignoreFreeze") return false; // 後ろ(=新規)を無視
            TrimOrRemoveExistingFreeze(startInBody, start);
        }

        // --- フリーズ先頭 + フリーズ終端(既存フリーズの終端に新規の先頭が一致=新規側が「後ろ」) ---
        var startAtTail = Freezes.FirstOrDefault(f => f.EndTick == start);
        if (startAtTail is not null)
        {
            if (opt.FreezeHeadVsTailMode == "ignoreFreeze") return false;
            TrimOrRemoveExistingFreeze(startAtTail, start);
        }

        // ============================================================
        // 2. 新規フリーズの範囲(帯・終端)に既存オブジェクトが刺さっていないかを解決する
        //    (この時点のendはstart側の衝突解決を終えた状態。以下で必要に応じてさらに短縮する)
        // ============================================================

        // --- 範囲内の既存の通常ノート(帯 or 終端一致) ---
        foreach (var n in Notes.Where(n => n > start && n <= end).OrderBy(n => n).ToList())
        {
            bool isTail = n == end;
            string mode = isTail ? opt.NoteVsFreezeTailMode : opt.NoteVsFreezeBodyMode;
            if (mode == "ignoreNote")
            {
                RemoveExistingNote(n);
            }
            else // trim: 新規フリーズ側を、そのノートの16分手前で切る
            {
                long newEnd = n - Sixteenth;
                if (newEnd <= start) return false; // 切りきれないほど近い場合は新規フリーズ自体を見送る
                end = newEnd;
                break; // 最も早い衝突位置で打ち切ったので、これより後ろは範囲外になる
            }
        }

        // --- 範囲内の既存フリーズの先頭(帯 or 終端一致、=既存側が「後ろ」) ---
        foreach (var f in Freezes.Where(f => f.StartTick > start && f.StartTick <= end).OrderBy(f => f.StartTick).ToList())
        {
            bool isTail = f.StartTick == end;
            string mode = isTail ? opt.FreezeHeadVsTailMode : opt.FreezeHeadVsBodyMode;
            if (mode == "ignoreFreeze")
            {
                RemoveExistingFreeze(f); // 後ろの既存フリーズを無視(破棄)
            }
            else // trim: 新規フリーズ(前・帯側)を、後ろのフリーズ先頭の16分手前で切る
            {
                long newEnd = f.StartTick - Sixteenth;
                if (newEnd <= start) return false;
                end = newEnd;
                break;
            }
        }

        if (end <= start) return false;
        AddFreeze(start, end);
        return true;
    }

    private void AddNote(long tick)
    {
        Notes.Add(tick);
        Operations.Add(new AddNoteOp(tick));
    }

    private void RemoveExistingNote(long tick)
    {
        Notes.Remove(tick);
        Operations.Add(new RemoveExistingNoteOp(tick));
    }

    private void AddFreeze(long start, long end)
    {
        Freezes.Add(new FreezeNote(start, end));
        Operations.Add(new AddFreezeOp(start, end));
    }

    private void RemoveExistingFreeze(FreezeNote f)
    {
        Freezes.Remove(f);
        Operations.Add(new RemoveExistingFreezeOp(f.StartTick));
    }

    /// <summary>既存フリーズfの終端を、referenceTickの16分手前まで短縮する。短縮しきれない
    /// (始点以下になってしまう)場合はフリーズごと破棄する。</summary>
    private void TrimOrRemoveExistingFreeze(FreezeNote f, long referenceTick)
    {
        long newEnd = referenceTick - Sixteenth;
        Freezes.Remove(f);
        if (newEnd <= f.StartTick)
        {
            Operations.Add(new RemoveExistingFreezeOp(f.StartTick));
        }
        else
        {
            Freezes.Add(f with { EndTick = newEnd });
            Operations.Add(new TrimExistingFreezeOp(f, newEnd));
        }
    }
}
