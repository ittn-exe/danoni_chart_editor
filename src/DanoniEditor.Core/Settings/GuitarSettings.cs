namespace DanoniEditor.Core.Settings;

/// <summary>
/// ギター系キー種(danoniplus custom.js「std_gt.js」相当、GuitarFreaks形式のピック+フレット判定)の
/// プレーテスト設定(2026-09-27要望対応)。環境設定「ギター」カテゴリで編集する。
/// std_gt.jsのGTR_*定数のうち、エディタのプレーテストへ移植する項目をここへ追加していく想定
/// (ハードコードにせず、この専用カテゴリに集約する運用。GuitarPlaytestEngine参照)。
/// </summary>
public sealed class GuitarSettings
{
    /// <summary>ギター判定(GuitarPlaytestEngine)を適用する対象キー種ID一覧
    /// (std_gt.js GTR_TARGET_KEYS相当)。対象キー種のテンプレートは、いずれかのレーンの
    /// DataNameが"space"であること(ピックレーンとして扱う)。既定はn5g/n9g。</summary>
    public List<string> TargetKeyTypeIds { get; set; } = ["n5g", "n9g"];

    // =====================================================================
    // Phase2(2026-09-27要望対応、続き): ピック先行の猶予・離し遅れ免除・ダブルピック許可。
    // std_gt.jsのGTR_PICK_EARLY_GRACE/GTR_RELEASE_EXEMPT_*/GTR_FRZ_END_GRACE/GTR_ALLOW_REPICK相当。
    // =====================================================================

    /// <summary>ピック先行の猶予(フレーム、std_gt.js GTR_PICK_EARLY_GRACE相当)。ピックを押した瞬間に
    /// 指板(フレット)が一致していなくても、即ミスにせずこの猶予内であれば指板が追いつくのを待つ。
    /// 猶予を超えても一致しなければそのまま保留を諦め、以後は通常の枠外タイムアウト判定に任せる。</summary>
    public double PickEarlyGraceFrames { get; set; } = 4;

    /// <summary>離し遅れ免除機能そのものの有効/無効(std_gt.js GTR_RELEASE_EXEMPT_ENABLE相当)。
    /// falseの場合、ReleaseExemptMaxFrames・FreezeEndGraceFramesの両方の免除を行わない
    /// (このエディタでは両者を1つのON/OFFへ集約している)。既定true。</summary>
    public bool ReleaseExemptEnable { get; set; } = true;

    /// <summary>離し遅れ免除の指定フレーム数(std_gt.js GTR_RELEASE_EXEMPT_MAX_FRAMES相当)。
    /// 直前に解決した1ノート/1組を押しっぱなしにしていても、この猶予内なら「余計な押下」として
    /// 扱わない。ReleaseExemptEnable=trueのときのみ意味を持つ。</summary>
    public double ReleaseExemptMaxFrames { get; set; } = 8;

    /// <summary>フリーズ終端後の残り押し猶予(フレーム、std_gt.js GTR_FRZ_END_GRACE相当)。
    /// フリーズが終端した(成功・失敗いずれも)直後は、そのレーンを押しっぱなしにしていても
    /// この猶予内なら「余計な押下」として扱わない。ReleaseExemptEnable=trueのときのみ意味を持つ
    /// (コードで必要なレーンは猶予中でも実押下が必要で、甘くはならない)。</summary>
    public double FreezeEndGraceFrames { get; set; } = 10;

    /// <summary>ダブルピック(判定窓が切れるまでの打ち直し)を許可するか(std_gt.js GTR_ALLOW_REPICK相当)。
    /// falseの場合、ピック先行の猶予で保留中に追加のピック入力があっても無視する
    /// (成立もペナルティも起こさない)。既定true。</summary>
    public bool AllowRepick { get; set; } = true;

    // =====================================================================
    // Phase4(2026-09-27c要望対応): 見た目の演出。std_gt.jsのGTR_LINE_*/GTR_OPEN_*/
    // GTR_HAMMER_FILL_OPACITY/GTR_HOLD_GLOW_*/GTR_HIDE_FRZ_BOTTOM相当。
    // オートピック/オートネック(Phase3)は本体実装の全体オートプレイに一元化する方針のため対応しない
    // (GuitarPlaytestEngineのクラス冒頭コメント参照)。
    // =====================================================================

    /// <summary>ピックノーツ(コード/開放弦とも)に全レーン幅の横線を表示するか
    /// (std_gt.js GTR_LINE_*相当)。既定true。</summary>
    public bool ShowPickLine { get; set; } = true;

    /// <summary>ピック横線の色(6桁カラーコード)。</summary>
    public string PickLineColorHex { get; set; } = "#C0C0C0";

    /// <summary>ピック横線の不透明度(0〜1)。</summary>
    public double PickLineOpacity { get; set; } = 0.35;

    /// <summary>ピック横線の太さ(px)。</summary>
    public double PickLineHeight { get; set; } = 5;

    /// <summary>開放弦(フレット側に構成音が無いピックノーツ)の表示方法(std_gt.js GTR_OPEN_STYLE相当)。
    /// 0=×印(ピック横線と同色・同太さを流用) / 1=×印(専用色・太さ) / 2=×印の代わりに横線を重ねる。</summary>
    public int OpenStringStyle { get; set; } = 0;

    /// <summary>開放弦表示の色(6桁カラーコード)。OpenStringStyleが1または2のときのみ使用。</summary>
    public string OpenStringColorHex { get; set; } = "#FFFFFF";

    /// <summary>開放弦表示の不透明度(0〜100)。OpenStringStyleが1または2のときのみ使用。</summary>
    public double OpenStringOpacity { get; set; } = 50;

    /// <summary>開放弦表示の太さ(px)。OpenStringStyleが1または2のときのみ使用。</summary>
    public double OpenStringWidth { get; set; } = 7;

    /// <summary>開放弦×印の一辺の長さ(px)。</summary>
    public double OpenStringHeight { get; set; } = 30;

    /// <summary>ハンマリング(ピック非同時)ノートの矢印内側を塗りつぶして区別するか。既定true。</summary>
    public bool ShowHammerFill { get; set; } = true;

    /// <summary>ハンマリング矢印内側塗りつぶしの不透明度(0〜1)。</summary>
    public double HammerFillOpacity { get; set; } = 1.0;

    /// <summary>フレット押下中のレーンをステップゾーンのうっすら発光で示すか。既定true。</summary>
    public bool ShowHoldGlow { get; set; } = true;

    /// <summary>フレット押下発光の高さ(px、円形発光の直径)。</summary>
    public double HoldGlowHeight { get; set; } = 150;

    /// <summary>フリーズアローの終端矢印を非表示にするか(std_gt.js GTR_HIDE_FRZ_BOTTOM相当)。
    /// 既定true(帯だけを表示し、終端の矢印グラフィックは出さない)。</summary>
    public bool HideFreezeTailArrow { get; set; } = true;

    /// <summary>環境設定ウィンドウの作業コピー用(AppSettings.Cloneから呼ぶ、参照型のTargetKeyTypeIdsを
    /// 個別に複製する。AppSettings.KeyMacros等と同じ考え方)。値型・文字列プロパティ(Phase2/4で追加した
    /// 猶予フレーム数・色コード等)はMemberwiseCloneの浅いコピーでそのまま複製されるため、個別対応は不要。</summary>
    public GuitarSettings Clone() => new()
    {
        TargetKeyTypeIds = [.. TargetKeyTypeIds],
        PickEarlyGraceFrames = PickEarlyGraceFrames,
        ReleaseExemptEnable = ReleaseExemptEnable,
        ReleaseExemptMaxFrames = ReleaseExemptMaxFrames,
        FreezeEndGraceFrames = FreezeEndGraceFrames,
        AllowRepick = AllowRepick,
        ShowPickLine = ShowPickLine,
        PickLineColorHex = PickLineColorHex,
        PickLineOpacity = PickLineOpacity,
        PickLineHeight = PickLineHeight,
        OpenStringStyle = OpenStringStyle,
        OpenStringColorHex = OpenStringColorHex,
        OpenStringOpacity = OpenStringOpacity,
        OpenStringWidth = OpenStringWidth,
        OpenStringHeight = OpenStringHeight,
        ShowHammerFill = ShowHammerFill,
        HammerFillOpacity = HammerFillOpacity,
        ShowHoldGlow = ShowHoldGlow,
        HoldGlowHeight = HoldGlowHeight,
        HideFreezeTailArrow = HideFreezeTailArrow,
    };
}
