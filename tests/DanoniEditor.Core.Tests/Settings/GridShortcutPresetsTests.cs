using DanoniEditor.Core.Settings;

namespace DanoniEditor.Core.Tests.Settings;

/// <summary>
/// Ctrl+1〜9,0,-,^ グリッド分解能ショートカット(2026-07-26)のプリセット対応表テスト。
/// ユーザー指定の割り当て(オリジナルセット/SKB拡張セット)がそのまま反映されているか、
/// 全12スロットがSnapService.Divisionsの値と一致するかを検証する。
/// </summary>
public class GridShortcutPresetsTests
{
    [Fact]
    public void Original_MatchesUserSpecifiedAscendingOrder()
    {
        // オリジナルセット: 4,8,12,16,20,24,28,32,40,48,56,64分(単純な昇順)
        int[] expected = [4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], GridShortcutPresets.DivisionForIndex(GridShortcutPresets.Original, i));
    }

    [Fact]
    public void SkbExtended_MatchesUserSpecifiedOrder()
    {
        // SKB拡張セット: 1〜7はSKBエディタのCtrl+1〜7割り当て(4/8/16/12/24/48/32分)をそのまま踏襲、
        // 8〜12(SKBに存在しない分)は20/28/40/56/64分を昇順で追加
        int[] expected = [4, 8, 16, 12, 24, 48, 32, 20, 28, 40, 56, 64];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], GridShortcutPresets.DivisionForIndex(GridShortcutPresets.SkbExtended, i));
    }

    [Fact]
    public void UnknownPresetId_FallsBackToOriginal()
    {
        Assert.Equal(4, GridShortcutPresets.DivisionForIndex("bogus", 0));
        Assert.Equal(64, GridShortcutPresets.DivisionForIndex("bogus", 11));
    }

    [Fact]
    public void OutOfRangeIndex_ReturnsNull()
    {
        Assert.Null(GridShortcutPresets.DivisionForIndex(GridShortcutPresets.Original, -1));
        Assert.Null(GridShortcutPresets.DivisionForIndex(GridShortcutPresets.Original, 12));
    }

    [Fact]
    public void BothPresets_ContainExactlySameTwelveDivisions_JustReordered()
    {
        // どちらのセットも同じ12個の分解能を、キー配列だけ変えて割り当てている
        // (SnapService.Divisionsの12値と過不足なく一致することを保証する回帰テスト)
        int[] snapDivisions = [4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64];
        var original = GridShortcutPresets.DivisionsFor(GridShortcutPresets.Original).OrderBy(x => x).ToArray();
        var skb = GridShortcutPresets.DivisionsFor(GridShortcutPresets.SkbExtended).OrderBy(x => x).ToArray();
        Assert.Equal(snapDivisions, original);
        Assert.Equal(snapDivisions, skb);
    }
}
