namespace DanoniEditor.App;

/// <summary>
/// 右パネル④「その他のヘッダー機能」タブの入力型分類(仕様書6.4.4)。
/// Raw = 複合パラメータの暫定対応(専用UIは今回省略、dos.txt互換の生文字列をそのまま入力する枠)。
/// </summary>
internal enum HeaderParamType { Bool, Number, Text, Color, Dropdown, Raw }

/// <summary>dos_headerパラメータ1件分の定義(名前・型・所属カテゴリ・既定値・選択肢)。</summary>
internal sealed record HeaderParamDef(string Name, HeaderParamType Type, string Category, string Default = "", string[]? Options = null);

/// <summary>
/// 仕様書6.4.4の全パラメータ一覧(colorDataTypeは廃止機能のため仕様書通りUIから除外)。
/// カテゴリの並び順・グルーピングは仕様書の表と同じ。複合パラメータ(dummyId/difColor/
/// unStockCategory/stockForceDel/displayChainOFF/keyGroupOrder/customGauge/resultFormat/
/// resultValsView/preloadImages/imgType/titleAnimation/titleArrowName/skinType)は
/// Raw型(生文字列入力)で暫定対応。専用の複合UIが必要になった場合は個別に差し替える。
/// </summary>
internal static class ExtraHeaderDefs
{
    public static readonly HeaderParamDef[] All =
    [
        // --- 楽曲・譜面情報 ---
        new("dosNo", HeaderParamType.Number, "楽曲・譜面情報", "0"),
        new("musicNo", HeaderParamType.Number, "楽曲・譜面情報", "0"),
        new("packageName", HeaderParamType.Text, "楽曲・譜面情報"),
        new("musicGroup", HeaderParamType.Text, "楽曲・譜面情報"),
        new("musicFolder", HeaderParamType.Text, "楽曲・譜面情報"),
        new("difCustomLink", HeaderParamType.Text, "楽曲・譜面情報"),
        new("dummyId", HeaderParamType.Raw, "楽曲・譜面情報"),
        new("difColor", HeaderParamType.Raw, "楽曲・譜面情報"),

        // --- プレイ時間制御・譜面位置調整 ---
        new("endFrame", HeaderParamType.Number, "プレイ時間制御・譜面位置調整"),
        new("fadeFrame", HeaderParamType.Number, "プレイ時間制御・譜面位置調整"),
        new("adjustment", HeaderParamType.Number, "プレイ時間制御・譜面位置調整", "0"),
        new("playbackRate", HeaderParamType.Number, "プレイ時間制御・譜面位置調整", "1"),
        new("unStockCategory", HeaderParamType.Raw, "プレイ時間制御・譜面位置調整"),
        new("stockForceDel", HeaderParamType.Raw, "プレイ時間制御・譜面位置調整"),

        // --- 設定時の初期設定 ---
        new("settingUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("displayUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("difSelectorUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("scoreDetailUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("transKeyUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("colorCdPaddingUse", HeaderParamType.Bool, "設定時の初期設定"),
        new("customFont", HeaderParamType.Text, "設定時の初期設定"),
        new("displayChainOFF", HeaderParamType.Raw, "設定時の初期設定"),
        new("keyGroupOrder", HeaderParamType.Raw, "設定時の初期設定"),
        new("customGauge", HeaderParamType.Raw, "設定時の初期設定"),
        // colorDataType: 廃止機能のためUIには含めない(仕様書6.4.4)

        // --- プレイ時の初期設定 ---
        // 2026-07-16l: 本体側(danoniplus)の既定はtrueだが、エディタでは意図しないfrzColor無効化を
        // 避けるため既定falseとする(ユーザー指定)。ONにするとfrzColorの指定が強制的にOFFになる
        // (MainWindow側のUI連動・DosExporterの出力抑止を参照)。
        new("defaultFrzColorUse", HeaderParamType.Bool, "プレイ時の初期設定", "false"),
        new("frzScopeFromAC", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("jdgPosReset", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("bottomWordSet", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("wordAutoReverse", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("frzStartjdgUse", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("excessiveJdgUse", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("finishView", HeaderParamType.Bool, "プレイ時の初期設定"),
        new("playingX", HeaderParamType.Number, "プレイ時の初期設定"),
        new("playingY", HeaderParamType.Number, "プレイ時の初期設定"),
        new("playingWidth", HeaderParamType.Number, "プレイ時の初期設定"),
        new("playingHeight", HeaderParamType.Number, "プレイ時の初期設定"),
        new("stepY", HeaderParamType.Number, "プレイ時の初期設定"),
        new("stepYR", HeaderParamType.Number, "プレイ時の初期設定"),
        new("arrowJdgX", HeaderParamType.Number, "プレイ時の初期設定"),
        new("arrowJdgY", HeaderParamType.Number, "プレイ時の初期設定"),
        new("frzJdgX", HeaderParamType.Number, "プレイ時の初期設定"),
        new("frzJdgY", HeaderParamType.Number, "プレイ時の初期設定"),
        new("shortcutX", HeaderParamType.Number, "プレイ時の初期設定"),
        new("shortcutY", HeaderParamType.Number, "プレイ時の初期設定"),
        new("customCreditWidth", HeaderParamType.Number, "プレイ時の初期設定"),
        new("scArea", HeaderParamType.Number, "プレイ時の初期設定"),
        new("minSpeed", HeaderParamType.Number, "プレイ時の初期設定"),
        new("maxSpeed", HeaderParamType.Number, "プレイ時の初期設定"),
        new("maxLifeVal", HeaderParamType.Number, "プレイ時の初期設定"),
        new("readyDelayFrame", HeaderParamType.Number, "プレイ時の初期設定"),
        new("readyAnimationFrame", HeaderParamType.Number, "プレイ時の初期設定"),
        new("stretchYRate", HeaderParamType.Number, "プレイ時の初期設定"),
        new("readyHtml", HeaderParamType.Text, "プレイ時の初期設定"),
        new("readyAnimationName", HeaderParamType.Text, "プレイ時の初期設定"),
        new("setShadowColor", HeaderParamType.Color, "プレイ時の初期設定", "#000000"),
        new("frzShadowColor", HeaderParamType.Color, "プレイ時の初期設定", "#000000"),
        new("defaultColorgrd", HeaderParamType.Color, "プレイ時の初期設定", "#000000"),
        new("readyColor", HeaderParamType.Color, "プレイ時の初期設定", "#FFFFFF"),

        // --- タイトル・結果画面の初期設定 ---
        new("customTitleUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("customBackUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("customBackMainUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("commentAutoBr", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("commentExternal", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("masktitleButton", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("maskresultButton", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("resultMotionSet", HeaderParamType.Bool, "タイトル・結果画面の初期設定"),
        new("titleSize", HeaderParamType.Number, "タイトル・結果画面の初期設定"),
        new("resultDelayFrame", HeaderParamType.Number, "タイトル・結果画面の初期設定"),
        new("resultFormat", HeaderParamType.Raw, "タイトル・結果画面の初期設定"),
        new("resultValsView", HeaderParamType.Raw, "タイトル・結果画面の初期設定"),

        // --- カスタムデータの取込 ---
        new("autoPreload", HeaderParamType.Bool, "カスタムデータの取込"),
        new("bgCanvasUse", HeaderParamType.Bool, "カスタムデータの取込"),
        new("baseBright", HeaderParamType.Bool, "カスタムデータの取込"),
        new("customJs", HeaderParamType.Text, "カスタムデータの取込"),
        new("customCss", HeaderParamType.Text, "カスタムデータの取込"),
        new("settingType", HeaderParamType.Text, "カスタムデータの取込"),
        new("syncBackPath", HeaderParamType.Text, "カスタムデータの取込"),
        new("preloadImages", HeaderParamType.Raw, "カスタムデータの取込"),
        new("imgType", HeaderParamType.Raw, "カスタムデータの取込"),

        // --- デフォルトデザインの利用有無 ---
        new("customTitleArrowUse", HeaderParamType.Bool, "デフォルトデザインの利用有無"),
        new("customTitleAnimationUse", HeaderParamType.Bool, "デフォルトデザインの利用有無"),
        new("customReadyUse", HeaderParamType.Bool, "デフォルトデザインの利用有無"),

        // --- タイトル文字エフェクト ---
        new("titleLineHeight", HeaderParamType.Number, "タイトル文字エフェクト"),
        new("titleAnimationClass", HeaderParamType.Text, "タイトル文字エフェクト"),
        new("titlegrd", HeaderParamType.Color, "タイトル文字エフェクト", "#FFFFFF"),
        new("titleArrowgrd", HeaderParamType.Color, "タイトル文字エフェクト", "#FFFFFF"),
        new("titleAnimation", HeaderParamType.Raw, "タイトル文字エフェクト"),
        new("titleArrowName", HeaderParamType.Raw, "タイトル文字エフェクト"),

        // --- クレジット・共通設定 ---
        new("autoSpread", HeaderParamType.Bool, "クレジット・共通設定"),
        new("heightVariable", HeaderParamType.Bool, "クレジット・共通設定"),
        new("windowWidth", HeaderParamType.Number, "クレジット・共通設定"),
        new("windowHeight", HeaderParamType.Number, "クレジット・共通設定"),
        new("hashTag", HeaderParamType.Text, "クレジット・共通設定"),
        new("commentVal", HeaderParamType.Text, "クレジット・共通設定"),
        new("keyRetry", HeaderParamType.Text, "クレジット・共通設定"),
        new("keyTitleBack", HeaderParamType.Text, "クレジット・共通設定"),
        new("makerView", HeaderParamType.Text, "クレジット・共通設定"),
        new("releaseDate", HeaderParamType.Text, "クレジット・共通設定"),
        new("windowAlign", HeaderParamType.Dropdown, "クレジット・共通設定", "center", ["left", "center", "right"]),
        new("skinType", HeaderParamType.Raw, "クレジット・共通設定"),

        // --- クエリパラメータ(例外枠) ---
        new("debug", HeaderParamType.Bool, "クエリパラメータ(例外枠)", "false"),
    ];
}
