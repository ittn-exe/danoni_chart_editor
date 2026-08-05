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
        // 2026-07-30確認: danoni_main.jsを確認したところ、単一の"settingUse"/"displayUse"という
        // パラメータ名は本体側に存在せず(参照されないため出力しても無視される「死んだUI」だった)、
        // 実際はg_canDisabledSettings/g_displays(danoni_constants.js)の各要素ごとに動的生成される
        // "{要素名}Use"という個別パラメータ群だと判明。全項目をここへ列挙して差し替える。
        // g_canDisabledSettings系(設定変更の許可/禁止、単純なtrue/false):
        // 2026-08-05要望対応: Bool型は「使用する」チェック+true/falseラジオボタンの形式へ統一した
        // (旧: 単一チェックボックスでON=true出力・OFF=未出力のみ)。既定値は原則"true"(要望に基づく)。
        new("speedUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("motionUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("scrollUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("reverseUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("shuffleUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("autoPlayUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("gaugeUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        // excessiveUse: g_canDisabledSettingsとしては単純なtrue/falseだが、本体側は同じヘッダー名を
        // 難易度ごとの"有効可否,初期ON/OFF"の複合値($区切り複数)としても読み取る、より複雑な二重の
        // 意味を持つ(danoni_main.js確認済み)。ここでは前者(単純なtrue/false)のみサポートし、
        // 難易度ごとの複合指定は将来課題とする(TBD参照)。
        new("excessiveUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("appearanceUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("playWindowUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("stepAreaUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("frzReturnUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("shakingUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("effectUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("camoufrageUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("camoufrageTypeUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("swappingUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("judgRangeUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("autoRetryUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        // g_displays系: "有効可否,初期ON/OFF"(例"true,ON"、省略時は"true"のみ)の複合値のため、
        // 単純なBool型では表現できずRaw型(生文字列)で暫定対応する。
        new("stepZoneUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("judgmentUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("lifeGaugeUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("scoreUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("musicInfoUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("filterLineUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("velocityUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("colorUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("backgroundUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("arrowEffectUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("specialUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("difSelectorUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        // scoreDetailUse: 2026-07-30確認、Bool型ではなく「表示したい項目名をカンマ区切りで列挙する」
        // 形式(有効な項目名: Density/Speed/ToolDif/HighScore/MiniMap、レガシー別名Velocity→Speed・
        // DifLevel→ToolDifも本体側で変換される)。Bool出力("true"等)では該当項目名が無く全滅するため
        // Raw型(生文字列)へ変更。
        new("scoreDetailUse", HeaderParamType.Raw, "設定時の初期設定"),
        new("transKeyUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("colorCdPaddingUse", HeaderParamType.Bool, "設定時の初期設定", "true"),
        new("customFont", HeaderParamType.Text, "設定時の初期設定"),
        new("displayChainOFF", HeaderParamType.Raw, "設定時の初期設定"),
        new("keyGroupOrder", HeaderParamType.Raw, "設定時の初期設定"),
        // customGauge/customGauge{N}/gaugeXXX{N}: 2026-07-26よりGaugeEditorWindow(専用ウィンドウ、
        // 設定メニューから起動)へ移行済み。ここでのRaw型汎用UIからは除外(DosExporter.AppendGaugeHeaders参照)。
        // colorDataType: 廃止機能のためUIには含めない(仕様書6.4.4)

        // --- プレイ時の初期設定 ---
        // 2026-07-16l: 本体側(danoniplus)の既定はtrueだが、エディタでは意図しないfrzColor無効化を
        // 避けるため既定falseとする(ユーザー指定)。ONにするとfrzColorの指定が強制的にOFFになる
        // (MainWindow側のUI連動・DosExporterの出力抑止を参照)。2026-08-05: 他のBool項目は既定trueへ
        // 統一したが、本項目のみユーザー確定仕様により既定falseを維持する(例外)。
        new("defaultFrzColorUse", HeaderParamType.Bool, "プレイ時の初期設定", "false"),
        new("frzScopeFromAC", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
        new("jdgPosReset", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
        new("bottomWordSet", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
        // 2026-07-30確認: danoni_main.jsを確認したところ、wordAutoReverseは2値の真偽ではなく
        // "auto"(既定、未指定時)/"ON"/"OFF"の3値である(C_DIS_AUTO/C_FLG_ON/C_FLG_OFF)。
        // 「使用する」チェックOFF=ヘッダー未出力=auto相当、チェックON時はON/OFFを明示的に選べる
        // Dropdown型へ変更し、「明示的にOFFにする」指定ができなかった問題を解消する。
        new("wordAutoReverse", HeaderParamType.Dropdown, "プレイ時の初期設定", "ON", ["ON", "OFF"]),
        new("frzStartjdgUse", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
        new("excessiveJdgUse", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
        new("finishView", HeaderParamType.Bool, "プレイ時の初期設定", "true"),
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
        new("customTitleUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("customBackUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("customBackMainUse", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("commentAutoBr", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("commentExternal", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("masktitleButton", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("maskresultButton", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("resultMotionSet", HeaderParamType.Bool, "タイトル・結果画面の初期設定", "true"),
        new("titleSize", HeaderParamType.Number, "タイトル・結果画面の初期設定"),
        new("resultDelayFrame", HeaderParamType.Number, "タイトル・結果画面の初期設定"),
        new("resultFormat", HeaderParamType.Raw, "タイトル・結果画面の初期設定"),
        new("resultValsView", HeaderParamType.Raw, "タイトル・結果画面の初期設定"),

        // --- カスタムデータの取込 ---
        new("autoPreload", HeaderParamType.Bool, "カスタムデータの取込", "true"),
        new("bgCanvasUse", HeaderParamType.Bool, "カスタムデータの取込", "true"),
        new("baseBright", HeaderParamType.Bool, "カスタムデータの取込", "true"),
        new("customJs", HeaderParamType.Text, "カスタムデータの取込"),
        new("customCss", HeaderParamType.Text, "カスタムデータの取込"),
        new("settingType", HeaderParamType.Text, "カスタムデータの取込"),
        new("syncBackPath", HeaderParamType.Text, "カスタムデータの取込"),
        new("preloadImages", HeaderParamType.Raw, "カスタムデータの取込"),
        new("imgType", HeaderParamType.Raw, "カスタムデータの取込"),

        // --- デフォルトデザインの利用有無 ---
        new("customTitleArrowUse", HeaderParamType.Bool, "デフォルトデザインの利用有無", "true"),
        new("customTitleAnimationUse", HeaderParamType.Bool, "デフォルトデザインの利用有無", "true"),
        new("customReadyUse", HeaderParamType.Bool, "デフォルトデザインの利用有無", "true"),

        // --- タイトル文字エフェクト ---
        new("titleLineHeight", HeaderParamType.Number, "タイトル文字エフェクト"),
        new("titleAnimationClass", HeaderParamType.Text, "タイトル文字エフェクト"),
        new("titlegrd", HeaderParamType.Color, "タイトル文字エフェクト", "#FFFFFF"),
        new("titleArrowgrd", HeaderParamType.Color, "タイトル文字エフェクト", "#FFFFFF"),
        new("titleAnimation", HeaderParamType.Raw, "タイトル文字エフェクト"),
        new("titleArrowName", HeaderParamType.Raw, "タイトル文字エフェクト"),

        // --- クレジット・共通設定 ---
        new("autoSpread", HeaderParamType.Bool, "クレジット・共通設定", "true"),
        new("heightVariable", HeaderParamType.Bool, "クレジット・共通設定", "true"),
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
        // 2026-08-05: debugのみ、誤ってON状態で出力してしまうリスクを避けるため既定falseを維持する
        // (ユーザー確定仕様、他のBool項目の既定trueからの例外)。
        new("debug", HeaderParamType.Bool, "クエリパラメータ(例外枠)", "false"),
    ];
}
