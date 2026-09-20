using System.Reflection;
using DanoniEditor.Collab.Protocol;
using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;

namespace DanoniEditor.Collab.Sync;

/// <summary>
/// プロジェクト全体スナップショットの送受信用の薄いラッパー(設計メモ2.3/3.2節)。
/// 既存の<see cref="ProjectSerializer"/>をそのまま流用し、独自の直列化形式は持たない。
/// </summary>
public static class SnapshotSync
{
    public static SnapshotMessage CreateSnapshot(ChartProject project) =>
        new(ProjectSerializer.Serialize(project));

    public static ChartProject ApplySnapshot(SnapshotMessage message) =>
        ProjectSerializer.Deserialize(message.ProjectJson);

    /// <summary>
    /// 2026-09-20 WPF側配線(簡易版): 既存の<see cref="EditorDocument"/>が保持するChartProject
    /// インスタンス(targetは参照を保ったまま。EditorDocument.Projectはget専用プロパティのため、
    /// 参照そのものの差し替えではなく中身の総入れ替えで対応する)へ、受信したスナップショットの
    /// 内容をまるごと反映する。true版のセル差分(設計メモ2.2節)が実装されるまでの簡易実装であり、
    /// 変更のあったセルに関わらずプロジェクト全体を都度上書きする(ユーザー確定仕様、2026-09-20)。
    /// 対象プロパティは<see cref="ChartProject"/>のpublicな読み書き可能プロパティ全て
    /// (リフレクションで列挙)とし、将来ChartProjectへプロパティが追加/削除されてもこの処理側の
    /// 修正が不要になるようにしている(「本体が正」原則、CLAUDE.md)。
    /// </summary>
    public static void ApplySnapshotInPlace(ChartProject target, SnapshotMessage message)
    {
        var incoming = ApplySnapshot(message);
        foreach (var prop in typeof(ChartProject).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue; // DisplayLabel等の算出プロパティは対象外
            if (prop.GetIndexParameters().Length > 0) continue;
            prop.SetValue(target, prop.GetValue(incoming));
        }
    }
}
