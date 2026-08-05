namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// EditorClipboard(staticなクリップボード)を触るテストクラスを直列実行させるためのxUnitコレクション
/// (2026-08-06、テストのフレーキー対策)。
///
/// xUnitは既定で「異なるテストクラス同士」を並列実行するため、ClipboardTestsと
/// PasteWithLaneMappingTestsが同時に走ると、双方がコンストラクタで呼ぶEditorClipboard.Clear()や
/// コピー操作が互いの状態を踏み荒らし、実装は正しいのに稀に落ちる(CopySelection_ExcludesTick0Bpm_
/// AndTimeSignatureやPasteWithLaneMapping_UnmappedSourceLane_IsNotPastedが不定期にFAILする)。
/// 同一コレクションに属するクラスは直列実行されるため、この競合を防げる。
/// </summary>
[CollectionDefinition("EditorClipboard", DisableParallelization = true)]
public sealed class EditorClipboardCollection;
