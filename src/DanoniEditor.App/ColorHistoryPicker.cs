using System.Text.RegularExpressions;
using DanoniEditor.Core.Settings;

namespace DanoniEditor.App;

/// <summary>
/// 色コード使用履歴(仕様書6.4.2/14章 colorHistory、2026-07-23実装)。
/// 単純な#RRGGBB形式のみを履歴対象とする(グラデーション等の生文字列は対象外、ユーザー確定仕様)。
/// 色入力欄(②色設定タブ・表示設定ダイアログ・環境設定・ncolor_data色編集タブ)から共通で使う。
///
/// 2026-08-08: 履歴の表示・選択UI(旧Show、ポップアップ)はColorPickerPopup(お気に入り機能新設に伴う
/// 統合カラーピッカー)へ統合したためここでは扱わない。このクラスは記録処理(Record)のみを残す。
/// </summary>
internal static partial class ColorHistoryPicker
{
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex SimpleHexRegex();

    /// <summary>単純な#RRGGBBとしてパース可能な値のみ履歴の先頭に記録する(重複は先頭へ移動、
    /// ColorHistoryLimit件で切り詰め)。呼び出し側で保存(AppSettings.Save)まで行うこと。</summary>
    public static void Record(AppSettings settings, string value)
    {
        var trimmed = value.Trim();
        if (!SimpleHexRegex().IsMatch(trimmed)) return;

        settings.ColorHistory.RemoveAll(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        settings.ColorHistory.Insert(0, trimmed);
        int limit = Math.Max(1, settings.ColorHistoryLimit);
        if (settings.ColorHistory.Count > limit)
            settings.ColorHistory.RemoveRange(limit, settings.ColorHistory.Count - limit);
    }
}
