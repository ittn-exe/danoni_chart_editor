using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Timing;

/// <summary>
/// speed/boostの「始点終点オートスムージング出力」(2026-07-30要望対応)。
/// ValueEvent.LinkGridDivisionが設定されている場合、そのイベントはtick順で直後(次)の同種イベントと
/// リンクしており、両者の間を指定した設置間隔(4/8/16/32分、既存ノートと同じ絶対tickグリッド基準)で
/// 区切った中間点を自動生成し、区間内を線形補間した値を割り当てる。
/// 生成した中間点はSpeedEvents/BoostEvents自体には追加しない(編集可能な元データと分離することで、
/// 選択・削除・ドラッグ移動のロジックを単純に保つ)。dos.txt出力(DosExporter)・プレイテスト
/// (PlaytestWindow)・プレビュー(PlayPreviewSurface)は、いずれもこのヘルパーで展開した結果を
/// 用いることで、リンクによる自動スムージングが3箇所すべてに一貫して反映される。
/// </summary>
public static class ValueEventSmoothing
{
    /// <summary>与えられたspeed_data/boost_data用のValueEventリスト(未ソートでも可)を、リンクによる
    /// 自動生成中間点を含めた展開後のリスト(tick昇順)に変換する。リンクが無ければ入力をtick順に
    /// 並べ替えただけの結果になる。</summary>
    public static List<ValueEvent> ExpandLinkedEvents(IReadOnlyList<ValueEvent> events)
    {
        if (events.Count == 0) return [];

        var sorted = events.OrderBy(e => e.Tick).ToList();
        var result = new List<ValueEvent>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            result.Add(sorted[i]);

            if (sorted[i].LinkGridDivision is not { } division || division <= 0) continue;
            if (i + 1 >= sorted.Count) continue;

            var next = sorted[i + 1];
            if (next.Tick <= sorted[i].Tick) continue;

            long step = TimingEngine.TicksPerBeat * 4 / division;
            if (step <= 0) continue;

            // 既存ノートと同じ絶対グリッド(tick0基準の等間隔)に揃えるため、tickの剰余で
            // 次のグリッド点を求める(自分自身のtickがちょうどグリッド上でも、そこは除外し
            // 「厳密に自分より後ろ」の最初のグリッド点から始める)。
            long firstGridTick = (sorted[i].Tick / step + 1) * step;
            for (long t = firstGridTick; t < next.Tick; t += step)
            {
                double ratio = (double)(t - sorted[i].Tick) / (next.Tick - sorted[i].Tick);
                double value = sorted[i].Value + (next.Value - sorted[i].Value) * ratio;
                result.Add(new ValueEvent(t, value));
            }
        }

        return result;
    }

    /// <summary>2026-08-23要望対応(BPMの「始点終点リンク」、直線ランプ): BpmEvent版のExpandLinkedEvents。
    /// BPMは拍位置(tick)に対して直線的に変化するランプとして扱われ、このエディタ内部の
    /// TimingEngine.TickToFrame/FrameToTickは対数/指数の解析解で厳密な値を計算するため、本来は
    /// 中間点への分解(近似)を必要としない。しかしSKB/FUJIエディタ向けエクスポートは離散的な
    /// BPM変化点しか扱えない外部形式であるため、それらの出力時にのみ、リンク区間をこのメソッドで
    /// 選択した設置間隔(LinkGridDivision)の細かい離散ステップへ分解して近似出力する
    /// (アルゴリズムはExpandLinkedEventsと同一、対象の型(ValueEvent/BpmEvent)のみ異なる)。
    /// 生成される中間点はFrameAnchorを持たない(FrameAnchorは元の宣言済みイベントにのみ意味を持つため)。</summary>
    public static List<BpmEvent> ExpandLinkedBpmEvents(IReadOnlyList<BpmEvent> events)
    {
        if (events.Count == 0) return [];

        var sorted = events.OrderBy(e => e.Tick).ToList();
        var result = new List<BpmEvent>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            result.Add(sorted[i]);

            if (sorted[i].LinkGridDivision is not { } division || division <= 0) continue;
            if (i + 1 >= sorted.Count) continue;

            var next = sorted[i + 1];
            if (next.Tick <= sorted[i].Tick) continue;

            long step = TimingEngine.TicksPerBeat * 4 / division;
            if (step <= 0) continue;

            long firstGridTick = (sorted[i].Tick / step + 1) * step;
            for (long t = firstGridTick; t < next.Tick; t += step)
            {
                double ratio = (double)(t - sorted[i].Tick) / (next.Tick - sorted[i].Tick);
                double bpm = sorted[i].Bpm + (next.Bpm - sorted[i].Bpm) * ratio;
                result.Add(new BpmEvent(t, bpm));
            }
        }

        return result;
    }
}
