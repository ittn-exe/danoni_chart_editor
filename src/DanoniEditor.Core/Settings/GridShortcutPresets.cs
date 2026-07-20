namespace DanoniEditor.Core.Settings;

/// <summary>
/// Ctrl+1〜9,0,-,^(数字キー列12個)によるグリッド分解能(SnapService.Division)切替のプリセット
/// (2026-07-26)。SKBエディタのCtrl+1〜7割り当て(4/8/16/12/24/48/32分)は使用頻度順/実装順と
/// 思われる並びで直感的ではないため、「このエディタでキーボード操作デビューする人」向けに
/// 分解能の単純な昇順で割り当てた「オリジナルセット」と、「SKBエディタから乗り換えた人」向けに
/// SKBの1〜7番目の割り当てをそのまま踏襲した「SKB拡張セット」の2種類を用意する(ユーザー指定仕様、
/// 2026-07-26)。どちらもSnapService.Divisionsの12値すべてを、同じ12個の物理キー
/// (Ctrl+1,2,...,9,0,-,^)に割り当てる。エディタ本体にボタン/チェックボックス等のUIは設けず、
/// 環境設定(PreferencesWindow)からのみ選択できる(ユーザー指定仕様)。
/// </summary>
public static class GridShortcutPresets
{
    /// <summary>プリセットID「オリジナルセット」。分解能を単純に昇順で並べたもの
    /// (Ctrl+1=4分, 2=8分, 3=12分, 4=16分, 5=20分, 6=24分, 7=28分, 8=32分, 9=40分, 0=48分, -=56分, ^=64分)。</summary>
    public const string Original = "original";

    /// <summary>プリセットID「SKB拡張セット」。SKBエディタのCtrl+1〜7(4/8/16/12/24/48/32分)を
    /// そのまま踏襲し、SKBに存在しない8個目以降(20/28/40/56/64分)は昇順で割り当てたもの。</summary>
    public const string SkbExtended = "skbExtended";

    private static readonly int[] OriginalDivisions = [4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64];
    private static readonly int[] SkbExtendedDivisions = [4, 8, 16, 12, 24, 48, 32, 20, 28, 40, 56, 64];

    /// <summary>プリセットIDから対応する12要素の分解能配列(インデックス0〜11が
    /// Ctrl+1,2,...,9,0,-,^に対応)を返す。不明なIDはOriginalへフォールバックする。</summary>
    public static IReadOnlyList<int> DivisionsFor(string presetId) => presetId switch
    {
        SkbExtended => SkbExtendedDivisions,
        _ => OriginalDivisions,
    };

    /// <summary>物理キー位置インデックス(0〜11、Ctrl+1,2,...,9,0,-,^の順)から分解能値を得る。
    /// 範囲外のインデックスはnull。</summary>
    public static int? DivisionForIndex(string presetId, int index)
    {
        var divisions = DivisionsFor(presetId);
        return index >= 0 && index < divisions.Count ? divisions[index] : null;
    }
}
