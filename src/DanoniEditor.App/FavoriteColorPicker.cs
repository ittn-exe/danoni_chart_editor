using System.Text.RegularExpressions;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// お気に入りの色(AppSettings.FavoriteColors、2026-08-08実装)への登録処理。
/// ColorHistoryPickerと同じく単純な#RRGGBB形式のみを対象とする(グラデーション等の生文字列は対象外、
/// ユーザー確定仕様)。削除は環境設定「カラーピッカー」カテゴリの一覧から行う(このクラスでは扱わない)。
/// </summary>
internal static partial class FavoriteColorPicker
{
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex SimpleHexRegex();

    /// <summary>色欄の現在値をお気に入りへ追加する。既に登録済み(大文字小文字を無視して一致)の場合は
    /// 何もしない(重複防止)。ColorHistoryと異なり上限は設けない。呼び出し側で保存
    /// (AppSettings.Save)まで行うこと。戻り値は実際に追加したかどうか。</summary>
    public static bool Register(AppSettings settings, string value)
    {
        var trimmed = value.Trim();
        if (!SimpleHexRegex().IsMatch(trimmed)) return false;
        if (settings.FavoriteColors.Any(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        settings.FavoriteColors.Add(trimmed);
        return true;
    }
}
