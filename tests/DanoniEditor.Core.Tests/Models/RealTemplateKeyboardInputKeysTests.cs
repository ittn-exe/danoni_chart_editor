using DanoniEditor.Core.Models;

namespace DanoniEditor.Core.Tests.Models;

/// <summary>
/// リポジトリ直下の実テンプレート(./template/temp_*.json)が問題なく読み込め、SKB操作モード用の
/// keyboardInputKeys(2026-07-21)が予約キー(↑/↓/Space/B、キーボードモードのカーソル移動・削除に予約済み)
/// と衝突しておらず、同一テンプレート内で重複していないことを検証する(TestData/EditingTemplateの
/// テスト専用テンプレートとは別に、実際に出荷される全キー種のテンプレートを対象とする回帰テスト)。
/// </summary>
public class RealTemplateKeyboardInputKeysTests
{
    // キーボードモードでカーソル移動/削除に予約されている物理キーのラベル表記(KeyboardModeController/
    // MainWindow.HandleKeyboardModeKey準拠)。大小文字は区別しない。
    private static readonly HashSet<string> ReservedLabels =
        new(StringComparer.OrdinalIgnoreCase) { "↑", "↓", "Space", "SP", "␣", " ", "B" };

    private static string RealTemplateDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "template", "temp_5.json")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("リポジトリ直下の./templateが見つかりません");
        return Path.Combine(dir, "template");
    }

    [Fact]
    public void AllRealTemplates_Load_AndHaveKeyboardInputKeysForEveryLane()
    {
        var repo = new TemplateRepository(RealTemplateDir());
        var keyTypeIds = repo.ListKeyTypeIds().ToList();
        Assert.NotEmpty(keyTypeIds);

        foreach (var id in keyTypeIds)
        {
            var tpl = repo.Get(id);
            Assert.Equal(tpl.KeyCount, tpl.Lanes.Count);
            foreach (var lane in tpl.Lanes)
                Assert.NotNull(lane.KeyboardInputKeys); // 未設定でも空配列(既定値)であるべき、nullにはならない
        }
    }

    [Fact]
    public void AllRealTemplates_KeyboardInputKeys_DoNotCollideWithReservedKeys()
    {
        var repo = new TemplateRepository(RealTemplateDir());
        foreach (var id in repo.ListKeyTypeIds())
        {
            var tpl = repo.Get(id);
            foreach (var lane in tpl.Lanes)
            {
                foreach (var label in lane.KeyboardInputKeys)
                {
                    Assert.False(ReservedLabels.Contains(label),
                        $"temp_{id}.json の {lane.LaneId} レーンのkeyboardInputKeys='{label}' が予約キーと衝突していますわ");
                }
            }
        }
    }

    [Fact]
    public void AllRealTemplates_KeyboardInputKeys_AreUniqueWithinTemplate()
    {
        var repo = new TemplateRepository(RealTemplateDir());
        foreach (var id in repo.ListKeyTypeIds())
        {
            var tpl = repo.Get(id);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lane in tpl.Lanes)
            {
                foreach (var label in lane.KeyboardInputKeys)
                {
                    if (seen.TryGetValue(label, out var otherLane))
                        Assert.Fail($"temp_{id}.json: '{label}' が {otherLane} と {lane.LaneId} で重複していますわ");
                    seen[label] = lane.LaneId;
                }
            }
        }
    }
}
