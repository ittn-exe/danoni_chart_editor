using DanoniEditor.Core.Models;
using DanoniEditor.Editing;

namespace DanoniEditor.Core.Tests.Editing;

/// <summary>
/// レーン入替マクロの適用(ApplyLaneSwapMacroAction、仕様書11.1、2026-07-30)のテスト。
/// - 順列配列に従ってレーンのノート配置データ(LaneNotes一式)が入れ替わること
/// - Undoで元に戻ること
/// - レーンの定義(この操作ではLaneDef自体には触れない)に影響しないこと
/// </summary>
public class LaneSwapMacroActionTests
{
    [Fact]
    public void Do_ReversesLaneOrder_5KeyMirrorExample()
    {
        var doc = TestFixtures.NewDocument();
        var lanes = doc.CurrentTab.Lanes;
        Assert.Equal(5, lanes.Count);

        // 各レーンへ「自分のインデックス」を示す一意なノートを置き、入れ替え後にどのデータが
        // どこへ来たかを判別できるようにする。
        for (int i = 0; i < lanes.Count; i++)
            lanes[i].Notes.Add(100 + i);

        // モデルケース: 5keyを丸ごと逆順にする [4,3,2,1,0]
        doc.Execute(new ApplyLaneSwapMacroAction([4, 3, 2, 1, 0], "逆順"));

        for (int i = 0; i < lanes.Count; i++)
        {
            int originalLane = 4 - i;
            Assert.Contains(100 + originalLane, lanes[i].Notes);
        }
    }

    [Fact]
    public void Undo_RestoresOriginalLaneOrder()
    {
        var doc = TestFixtures.NewDocument();
        var lanes = doc.CurrentTab.Lanes;
        for (int i = 0; i < lanes.Count; i++)
            lanes[i].Notes.Add(100 + i);

        doc.Execute(new ApplyLaneSwapMacroAction([3, 1, 2, 0, 4], "左右ミラー"));
        doc.Undo();

        for (int i = 0; i < lanes.Count; i++)
            Assert.Contains(100 + i, lanes[i].Notes);
    }

    [Fact]
    public void Do_MovesFreezesColorOverridesAndAnnotationsTogether()
    {
        var doc = TestFixtures.NewDocument();
        var lanes = doc.CurrentTab.Lanes;

        lanes[0].Freezes.Add(new FreezeNote(200, 300));
        lanes[0].ColorOverrides.Add(new NColorEntry(200, "#ff0000", null));
        lanes[0].Annotations.Add(new NoteAnnotation(200, "コメント", true));

        // レーン0の内容をレーン1へ移すマッピング: 位置1が元レーン0のデータを持ってくる
        doc.Execute(new ApplyLaneSwapMacroAction([1, 0, 2, 3, 4], "0と1を入替"));

        Assert.Empty(lanes[0].Freezes);
        Assert.Single(lanes[1].Freezes);
        Assert.Equal(200, lanes[1].Freezes[0].StartTick);
        Assert.Single(lanes[1].ColorOverrides);
        Assert.Single(lanes[1].Annotations);
    }
}
