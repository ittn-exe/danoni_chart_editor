namespace DanoniEditor.PluginContracts;

/// <summary>
/// プラグインが譜面データを安全に書き換えるための、限定的な編集API(2026-07-26、段階2の土台)。
/// 本体のUndo/Redoスタックへ正しく積まれる形で実行されるため、プラグインによる変更もCtrl+Zで
/// 取り消せる。最初は「ノート配置/削除」程度の小さな範囲のみを解放し、必要に応じて今後拡張する。
/// </summary>
public interface IPluginEditApi
{
    /// <summary>指定レーン・tickへ通常ノートを配置する。既に何かが置かれている等の理由で
    /// 配置できなかった場合はfalseを返す。</summary>
    bool PlaceNote(int laneIndex, long tick);

    /// <summary>指定レーン・tickの通常ノートを削除する。対象が無かった場合はfalseを返す。</summary>
    bool DeleteNote(int laneIndex, long tick);
}
