using DanoniEditor.Core.Models;
using DanoniEditor.Core.Timing;

namespace DanoniEditor.Core.Analysis;

/// <summary>タイムラインイベント種別(analyze.jsの`ScoreToTimeline`が生成する
/// {time, type, lane, value?}の`type`に対応)。</summary>
public enum IttnTimelineEventType
{
    Normal,
    FreezeStart,
    FreezeEnd,
    SpeedChange,
}

/// <summary>
/// タイムライン上の1イベント(analyze.jsの`ScoreToTimeline`が返す配列要素の移植)。
/// JS版はdos.txtのテキストを再パースしてこれを作るが、当エディタ版はJS版と等価な
/// タイムラインを、エディタ自身のネイティブモデル(DifficultyTab)から直接構築する
/// (`IttnAnalyzerTimelineBuilder.Build`参照)。
/// </summary>
/// <param name="Time">絶対フレーム(60fps整数。DosExporterと同じ丸め規約
/// (<see cref="Math.Round(double, MidpointRounding)"/> AwayFromZero)で四捨五入済み)。</param>
/// <param name="Tick">絶対tick(丸めなしの元tick値。2026-07-25 BPM強化パートで追加。
/// VOLTAGE/ALT/MOV/SOFLANの小節・拍基準スライディング窓(<see cref="IttnAnalyzerConfig"/>の
/// [仮]付きMeasures/Beats系定数群)の窓境界判定に使う。STREAM/CHORD/FREEZE/ONIGIRI/JACKは
/// 従来通りTime(フレーム)のみを使い、このフィールドは参照しない。</param>
/// <param name="Type">イベント種別。</param>
/// <param name="Lane">レーンID(<see cref="LaneDef.LaneId"/>、KEY_MAP側の"lane"フィールドと
/// 完全一致することを確認済み)。SpeedChangeの場合はnull。</param>
/// <param name="Value">SpeedChangeの場合のみ有効な変速後の速度値。</param>
public sealed record IttnTimelineEvent(long Time, long Tick, IttnTimelineEventType Type, string? Lane, double? Value = null);

/// <summary>
/// DifficultyTabのネイティブモデル(tick単位)から、analyze.jsのScoreToTimelineと等価な
/// タイムライン(フレーム単位、時間昇順)を構築する。
/// </summary>
public static class IttnAnalyzerTimelineBuilder
{
    /// <summary>DosExporterのRoundFrameと同じ丸め規約(仕様書のdos.txt出力と同じ整数フレーム化)。
    /// blankFrame等の一定オフセットは足さない(全イベントに一様に加わるだけなので、フレーム差分のみを
    /// 見るこのアナライザーの計算結果には影響しない)。</summary>
    private static long RoundFrame(double frame) => (long)Math.Round(frame, MidpointRounding.AwayFromZero);

    public static List<IttnTimelineEvent> Build(DifficultyTab tab, KeyTemplate template, TimingEngine engine)
    {
        var events = new List<IttnTimelineEvent>();

        int laneCount = Math.Min(tab.Lanes.Count, template.Lanes.Count);
        for (int i = 0; i < laneCount; i++)
        {
            var laneId = template.Lanes[i].LaneId;
            var data = tab.Lanes[i];

            foreach (var tick in data.Notes.OrderBy(t => t))
                events.Add(new IttnTimelineEvent(RoundFrame(engine.TickToFrame(tick)), tick, IttnTimelineEventType.Normal, laneId));

            foreach (var frz in data.Freezes.OrderBy(f => f.StartTick))
            {
                events.Add(new IttnTimelineEvent(RoundFrame(engine.TickToFrame(frz.StartTick)), frz.StartTick, IttnTimelineEventType.FreezeStart, laneId));
                events.Add(new IttnTimelineEvent(RoundFrame(engine.TickToFrame(frz.EndTick)), frz.EndTick, IttnTimelineEventType.FreezeEnd, laneId));
            }
        }

        foreach (var speed in tab.SpeedEvents.OrderBy(e => e.Tick))
            events.Add(new IttnTimelineEvent(RoundFrame(engine.TickToFrame(speed.Tick)), speed.Tick, IttnTimelineEventType.SpeedChange, null, speed.Value));

        // JS版のArray.prototype.sort(2019年以降の主要実装で安定ソート保証)に合わせ、
        // .NETのOrderBy(安定ソート保証)で時間のみのキーソートを行う。
        return events.OrderBy(e => e.Time).ToList();
    }
}
