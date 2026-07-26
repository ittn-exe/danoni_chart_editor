namespace DanoniEditor.Core.Settings;

/// <summary>
/// キーマクロの手順の種類(2026-07-26要望対応、第三者からの要望: Ctrl+Shift+数字キーに割り当てて
/// 「複数の機能を順番に実行する」マクロが欲しいとのこと。例: 再生速度をnに設定し、再生開始位置を
/// xに設定し、プレイテストを開始、というような組み合わせ)。
/// 既存の「レーン入替マクロ」(仕様書11章、右パネルの「実行」ボタン、LaneSwapMacro)とは別機能なので
/// 混同しないよう「キーマクロ」と呼び分ける。
/// </summary>
public enum KeyMacroStepKind
{
    /// <summary>再生速度を設定する(目視テスト・プレイテスト共通、AppSettings.PlaybackSpeedと同じ値。
    /// Valueは倍率、例: 1.0=等倍)</summary>
    SetPlaybackSpeed,
    /// <summary>再生開始ライン(ChartProject.PlaybackStartFrame)を設定する(Valueは秒単位、
    /// 内部ではframe = Value*60に変換される)</summary>
    SetPlaybackStartSeconds,
    /// <summary>目視テストを開始する(Valueは使用しない)</summary>
    StartVisualTest,
    /// <summary>プレイテストを開始する(Valueは使用しない)</summary>
    StartPlaytest,
}

/// <summary>キーマクロの手順1件。KindによってValueの意味が変わる(KeyMacroStepKind参照)。</summary>
public sealed class KeyMacroStep
{
    public KeyMacroStepKind Kind { get; set; }
    public double Value { get; set; }
}

/// <summary>Ctrl+Shift+1〜9の1スロット分のキーマクロ定義。Stepsを先頭から順に実行する。</summary>
public sealed class KeyMacroDefinition
{
    /// <summary>1〜9(Ctrl+Shift+数字キーの数字)</summary>
    public int Slot { get; set; }
    public List<KeyMacroStep> Steps { get; set; } = [];
}
