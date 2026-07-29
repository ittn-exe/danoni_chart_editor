namespace DanoniEditor.Core.Settings;

/// <summary>
/// マウスモード側のグローバルショートカットキーの識別子(2026-07-27要望対応:
/// 「ハードコードの記述をやめ、全て環境設定ファイルにて管理する」)。
/// 以下は対象外(この列挙には含めない):
/// ・キーボードモード中のノート入力・カーソル移動キー(各キーテンプレートで宣言、KeyboardModeController参照)。
/// ・キーボードモード中のCtrl+←/→(2小節移動)・Shift+Ctrl+←/→(4小節移動)は、同一キーに対し
///   Shiftキーで「移動量」が変わる特殊な内部分岐を持つため、単純なキー割り当てとして扱うと
///   カスタマイズ時に矛盾が生じる。今回は対象外のままハードコードで維持する。
/// ・Ctrl+1〜9,0,-,^(グリッド分解能切替)は既存のGridShortcutPreset設定で別途管理しているため対象外。
/// ・Ctrl+Shift+1〜9(キーマクロ実行)はスロット番号自体がトリガーキーを兼ねる設計のため対象外。
/// </summary>
public enum ShortcutId
{
    Undo,
    Redo,
    SaveProject,
    ExportDos,
    ScrollToStart,
    ScrollToEnd,
    InterruptVisualTestMouseMode,
    InterruptVisualTestKeyboardMode,
    StartPlaytest,
    ToggleKeyboardMode,
    CutSelection,
    CopySelection,
    PasteSelection,
    SelectAllTargets,
    SelectAllNotes,
    DeselectAll,
    ClearTimeRangeSelection,
    ScrollPageUp,
    ScrollPageDown,
    DeleteSelection,
    ClearPlaybackStartLine,
    ToggleVisualTest,
}

/// <summary>1つのショートカットキー割り当て。Keyは System.Windows.Input.Key の列挙名文字列で保持し、
/// Core層がWPFへ依存しないようにする(App層でEnum.TryParse&lt;Key&gt;して使う)。</summary>
public sealed class ShortcutBinding
{
    public string Key { get; set; } = "";
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public ShortcutBinding() { }

    public ShortcutBinding(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    /// <summary>表示用文字列(例: "Ctrl+Shift+A")。</summary>
    public string DisplayText()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>他の割り当てと同一キー・同一修飾キー組み合わせか(衝突検出用)。</summary>
    public bool ConflictsWith(ShortcutBinding other) =>
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
        && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    public ShortcutBinding Clone() => new(Key, Ctrl, Shift, Alt);
}

/// <summary>ShortcutIdごとの既定キー割り当て・表示名・「テキスト入力欄フォーカス中でも有効か」の
/// メタ情報(2026-07-27要望対応)。IgnoresTextFocus=trueの項目は、従来からテキストボックス
/// フォーカス中でも動作していた一般的な編集操作(Undo/Redo/保存等)に限定する。</summary>
public sealed record ShortcutMeta(string DisplayName, ShortcutBinding Default, bool IgnoresTextFocus);

public static class ShortcutDefaults
{
    public static readonly IReadOnlyDictionary<ShortcutId, ShortcutMeta> All = new Dictionary<ShortcutId, ShortcutMeta>
    {
        [ShortcutId.Undo] = new("元に戻す(Undo)", new("Z", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.Redo] = new("やり直し(Redo)", new("Y", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.SaveProject] = new("プロジェクト保存", new("S", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.ExportDos] = new("dos.txt書き出し", new("E", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.ScrollToStart] = new("譜面先頭へスクロール", new("Home", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.ScrollToEnd] = new("末尾ノートへスクロール", new("End", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.InterruptVisualTestMouseMode] = new("目視テスト中断(マウスモード中)", new("Space", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.InterruptVisualTestKeyboardMode] = new("目視テスト中断(キーボードモード中)", new("Enter", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.StartPlaytest] = new("プレイテスト開始", new("P", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.ToggleKeyboardMode] = new("キーボードモード切替", new("OemComma", ctrl: true), IgnoresTextFocus: true),
        [ShortcutId.CutSelection] = new("切り取り", new("X", ctrl: true), IgnoresTextFocus: false),
        [ShortcutId.CopySelection] = new("コピー", new("C", ctrl: true), IgnoresTextFocus: false),
        [ShortcutId.PasteSelection] = new("貼り付け", new("V", ctrl: true), IgnoresTextFocus: false),
        [ShortcutId.SelectAllTargets] = new("全てのオブジェクトを選択(環境設定の対象種別に従う)", new("A", ctrl: true, shift: true), IgnoresTextFocus: false),
        [ShortcutId.SelectAllNotes] = new("ノートレーン上の全てのオブジェクトを選択", new("A", ctrl: true), IgnoresTextFocus: false),
        [ShortcutId.DeselectAll] = new("オブジェクト選択の解除", new("A", shift: true), IgnoresTextFocus: false),
        [ShortcutId.ClearTimeRangeSelection] = new("時間情報レーンの範囲選択解除", new("Escape"), IgnoresTextFocus: false),
        [ShortcutId.ScrollPageUp] = new("1画面分上へスクロール", new("PageUp"), IgnoresTextFocus: false),
        [ShortcutId.ScrollPageDown] = new("1画面分下へスクロール", new("PageDown"), IgnoresTextFocus: false),
        [ShortcutId.DeleteSelection] = new("選択中オブジェクトの削除", new("Delete"), IgnoresTextFocus: false),
        [ShortcutId.ClearPlaybackStartLine] = new("再生開始ラインの指定解除", new("Back"), IgnoresTextFocus: false),
        [ShortcutId.ToggleVisualTest] = new("目視テスト開始/終了", new("Space"), IgnoresTextFocus: false),
    };
}

/// <summary>
/// キーボードモード(SKB操作モード)中専用のショートカット識別子(2026-07-29要望対応)。マウスモードの
/// ShortcutIdとは完全に独立したテーブルで管理し、同じ物理キーが重複して割り当てられることを意図的に
/// 許容する(キーボードモードのON/OFFによって同じキーの挙動が変わる、という仕様上の狙い通りの設計)。
/// 修飾キーは扱わない(Shiftは各操作の内部で「範囲選択」の補助フラグとして個別に読むため、
/// キーの割り当てそのものには含めない)。
/// 以下は対象外(この列挙には含めない):
/// ・ノート入力キー(KeyboardInputKeys、各キーテンプレートで宣言)。
/// ・目視テスト中の「ノート配置受付」時のキー(KeyAssign、キーテンプレート依存)。
/// ・Ctrl+←/→(2小節移動)・Shift+Ctrl+←/→(4小節移動)は、マウスモードのShortcutId同様、
///   Shiftで移動量が変わる特殊挙動のため対象外(ハードコードのまま維持)。
/// </summary>
public enum KeyboardModeShortcutId
{
    CursorUp,
    StepForward,
    StepBackward,
    CursorDown,
    MeasureBack,
    MeasureForward,
    DeleteAtCursor,
    ToggleVisualTest,
    TogglePane,
}

/// <summary>KeyboardModeShortcutIdの表示名・既定キー(修飾キーなし、単一のKey名文字列のみ)。</summary>
public sealed record KeyboardModeShortcutMeta(string DisplayName, string DefaultKey);

public static class KeyboardModeShortcutDefaults
{
    public static readonly IReadOnlyDictionary<KeyboardModeShortcutId, KeyboardModeShortcutMeta> All = new Dictionary<KeyboardModeShortcutId, KeyboardModeShortcutMeta>
    {
        [KeyboardModeShortcutId.CursorUp] = new("カーソル移動: 上(↑)", "Up"),
        [KeyboardModeShortcutId.StepForward] = new("カーソル移動: 前進(1グリッド)", "Space"),
        [KeyboardModeShortcutId.StepBackward] = new("カーソル移動: 後退(1グリッド)", "B"),
        [KeyboardModeShortcutId.CursorDown] = new("カーソル移動: 下(↓)", "Down"),
        [KeyboardModeShortcutId.MeasureBack] = new("1小節戻る(小節途中なら小節頭へ)", "Left"),
        [KeyboardModeShortcutId.MeasureForward] = new("1小節先へ進む", "Right"),
        [KeyboardModeShortcutId.DeleteAtCursor] = new("カーソル位置のノート/フリーズを削除", "Back"),
        [KeyboardModeShortcutId.ToggleVisualTest] = new("目視テスト開始/終了", "Enter"),
        [KeyboardModeShortcutId.TogglePane] = new("譜面ビュー分割時のアクティブペイン切替", "Tab"),
    };
}
