namespace DanoniEditor.Core.Settings;

/// <summary>
/// レーン入替マクロ(仕様書11章「レーン入替マクロ(ミラー/シャッフル)」、2026-07-30)。
/// テンプレートには含めず、AppSettings(エディタの環境設定)側で管理するユーザー定義マクロ。
/// テンプレート編集不要で「思いついた時に追加してすぐ使える」運用を想定している。
/// カレントプロジェクトの難易度タブのKeyTypeIdがTargetKeyTypeIdと一致する場合のみ呼び出し可能。
/// </summary>
public sealed class LaneSwapMacro
{
    public string MacroId { get; set; } = Guid.NewGuid().ToString("N");
    public string MacroName { get; set; } = "";
    public string TargetKeyTypeId { get; set; } = "";

    /// <summary>順列配列(仕様書11.1で確定)。インデックス=適用後のレーン位置、値=どのレーン位置の
    /// ノート配置データを持ってくるかを表す(例: 5keyの左右ミラーなら[3,1,2,0,4])。
    /// 要素数はTargetKeyTypeIdのテンプレートのレーン数と一致する。</summary>
    public List<int> LaneMapping { get; set; } = [];
}
