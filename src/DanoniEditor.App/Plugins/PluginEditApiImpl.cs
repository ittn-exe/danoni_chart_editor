using DanoniEditor.Editing;
using DanoniEditor.PluginContracts;

namespace DanoniEditor.App.Plugins;

/// <summary>
/// <see cref="IPluginEditApi"/>の実装(2026-07-26、段階2の土台)。全プラグイン共通の1インスタンスを
/// 使い回す(プラグインごとの状態を持たないため)。実際の変更は<see cref="EditorDocument.Execute"/>
/// (=Undo/Redoスタック)経由で行うため、プラグインによる編集も通常操作と同じくCtrl+Zで取り消せる。
/// </summary>
internal sealed class PluginEditApiImpl(Func<EditorDocument?> getDocument) : IPluginEditApi
{
    public bool PlaceNote(int laneIndex, long tick)
    {
        var doc = getDocument();
        if (doc is null) return false;
        if (laneIndex < 0 || laneIndex >= doc.CurrentTab.Lanes.Count) return false;
        if (tick < 0) return false;
        if (doc.CurrentTab.Lanes[laneIndex].Notes.Contains(tick)) return false; // 既に配置済み
        doc.Execute(new PlaceNoteAction(laneIndex, tick));
        return true;
    }

    public bool DeleteNote(int laneIndex, long tick)
    {
        var doc = getDocument();
        if (doc is null) return false;
        if (laneIndex < 0 || laneIndex >= doc.CurrentTab.Lanes.Count) return false;
        if (!doc.CurrentTab.Lanes[laneIndex].Notes.Contains(tick)) return false; // 対象が無い
        doc.Execute(new DeleteNoteAction(laneIndex, tick));
        return true;
    }
}
