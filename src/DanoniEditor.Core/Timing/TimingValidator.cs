namespace DanoniEditor.Core.Timing;

/// <summary>
/// BPM/拍子イベント列の妥当性検査。TimingEngineの構築時、およびプロジェクト読み込み時に使う。
/// 問題があれば人間が読める説明文を返し、無ければnullを返す。
/// </summary>
public static class TimingValidator
{
    /// <summary>1小節あたりのtick数が整数かつ正になる拍子の上限分母(4*TicksPerBeatの約数であること)。</summary>
    private static readonly long WholeNoteTicks = 4L * TimingEngine.TicksPerBeat;

    public static string? FindProblem(IReadOnlyList<BpmEvent> bpmEvents, IReadOnlyList<TimeSignatureEvent> timeSignatures)
    {
        foreach (var e in bpmEvents)
        {
            if (!double.IsFinite(e.Bpm) || e.Bpm <= 0)
                return $"BPMが不正です(tick={e.Tick}, bpm={e.Bpm})。0より大きい有限の数値が必要です";
            if (e.FrameAnchor is { } a && !double.IsFinite(a))
                return $"BPMイベントのFrameAnchorが不正です(tick={e.Tick})";
            if (e.Tick < 0)
                return $"BPMイベントのtickが負です(tick={e.Tick})";
        }

        foreach (var s in timeSignatures)
        {
            if (s.MeasureIndex < 0)
                return $"拍子イベントの小節番号が負です(measure={s.MeasureIndex})";
            if (s.Numerator <= 0 || s.Denominator <= 0)
                return $"拍子が不正です(measure={s.MeasureIndex}, {s.Numerator}/{s.Denominator})。分子・分母とも1以上が必要です";
            if (s.Numerator > 1024 || s.Denominator > WholeNoteTicks)
                return $"拍子の値が大きすぎます(measure={s.MeasureIndex}, {s.Numerator}/{s.Denominator})";
            if (s.TicksPerMeasure <= 0)
                return $"拍子の1小節あたりtick数が0以下になります(measure={s.MeasureIndex}, {s.Numerator}/{s.Denominator})";
        }
        return null;
    }
}
