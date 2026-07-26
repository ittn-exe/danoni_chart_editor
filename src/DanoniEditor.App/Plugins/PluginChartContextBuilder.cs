using DanoniEditor.Editing;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// <see cref="EditorDocument"/>(Core/Editingの内部モデル)から、プラグイン向けの読み取り専用
/// スナップショット<see cref="PluginChartContext"/>を組み立てる(2026-07-26、プラグイン対応の土台)。
/// この変換を1箇所に集約しておくことで、Core/Editing側のモデルがどう変わってもプラグイン向けの
/// 公開形だけは安定させられる。
/// </summary>
internal static class PluginChartContextBuilder
{
    public static PluginChartContext? Build(EditorDocument? doc)
    {
        if (doc is null) return null;

        var template = doc.CurrentTemplate;
        var tab = doc.CurrentTab;
        var engine = doc.Project.CreateTimingEngine();

        var lanes = new List<PluginLaneInfo>(template.Lanes.Count);
        for (int i = 0; i < template.Lanes.Count && i < tab.Lanes.Count; i++)
        {
            var laneDef = template.Lanes[i];
            var laneNotes = tab.Lanes[i];
            lanes.Add(new PluginLaneInfo(
                LaneId: laneDef.LaneId,
                KeyAssign: string.Join("/", laneDef.KeyAssign),
                ColorGroup: laneDef.ColorGroup,
                NoteTicks: [.. laneNotes.Notes],
                FreezeTicks: [.. laneNotes.Freezes.Select(f => (f.StartTick, f.EndTick))]));
        }

        return new PluginChartContext
        {
            KeyTypeId = template.KeyTypeId,
            DifficultyName = tab.DifficultyName,
            Lanes = lanes,
            TickToFrame = tick => engine.TickToFrame(tick),
            FrameToTick = frame => (long)Math.Round(engine.FrameToTick(frame)),
        };
    }
}
