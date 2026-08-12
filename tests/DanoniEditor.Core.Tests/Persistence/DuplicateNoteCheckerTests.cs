using DanoniEditor.Core.Models;
using DanoniEditor.Core.Persistence;
using DanoniEditor.Core.Tests.Editing;

namespace DanoniEditor.Core.Tests.Persistence;

/// <summary>2026-08-08新設の回帰テスト: プロジェクト読込時の重複ノート検出・解決
/// (DuplicateNoteChecker)。第三者報告のノート重複不具合(選択→移動/ペースト)を受けて、
/// 過去に重複ノートを含んだまま保存されたプロジェクトを開いた際の救済策として追加した。</summary>
public class DuplicateNoteCheckerTests
{
    private static ChartProject OneTabProject() => TestFixtures.NewProject();

    [Fact]
    public void FindDuplicates_NoDuplicates_ReturnsEmpty()
    {
        var project = OneTabProject();
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 96, 144]);

        var result = DuplicateNoteChecker.FindDuplicates(project);

        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_DetectsOverlappingNotes_WithCorrectCount()
    {
        var project = OneTabProject();
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 48, 48, 96]); // レーン0のtick48が3重

        var result = DuplicateNoteChecker.FindDuplicates(project);

        var loc = Assert.Single(result);
        Assert.Equal(0, loc.TabIndex);
        Assert.Equal(0, loc.Lane);
        Assert.Equal(48, loc.Tick);
        Assert.Equal(3, loc.Count);
    }

    [Fact]
    public void FindDuplicates_MultipleLocationsAcrossLanesAndTabs()
    {
        var repo = TestFixtures.Repository();
        var project = new ChartProject { ProjectName = "t" };
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "A"));
        project.Tabs.Add(DifficultyTab.CreateFor(repo.Get("5"), "B"));
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 48]);
        project.Tabs[0].Lanes[2].Notes.AddRange([96, 96]);
        project.Tabs[1].Lanes[1].Notes.AddRange([144, 144]);

        var result = DuplicateNoteChecker.FindDuplicates(project);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, d => d.TabIndex == 0 && d.Lane == 0 && d.Tick == 48);
        Assert.Contains(result, d => d.TabIndex == 0 && d.Lane == 2 && d.Tick == 96);
        Assert.Contains(result, d => d.TabIndex == 1 && d.Lane == 1 && d.Tick == 144);
    }

    [Fact]
    public void ResolveByRemoving_KeepsOneNote_RemovesExtras()
    {
        var project = OneTabProject();
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 48, 48, 96]);
        var duplicates = DuplicateNoteChecker.FindDuplicates(project);

        DuplicateNoteChecker.ResolveByRemoving(project, duplicates);

        var notes = project.Tabs[0].Lanes[0].Notes;
        Assert.Single(notes, t => t == 48); // 1件だけ残る
        Assert.Contains(96L, notes); // 重複していなかったノートは無関係
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void ResolveByMarking_NoExistingAnnotation_AddsWarningWithComment()
    {
        var project = OneTabProject();
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 48]);
        var duplicates = DuplicateNoteChecker.FindDuplicates(project);

        DuplicateNoteChecker.ResolveByMarking(project, duplicates);

        // 削除はされない(重複を維持)
        Assert.Equal(2, project.Tabs[0].Lanes[0].Notes.Count(t => t == 48));

        var annotation = Assert.Single(project.Tabs[0].Lanes[0].Annotations);
        Assert.Equal(48, annotation.Tick);
        Assert.True(annotation.Warning);
        Assert.Equal(DuplicateNoteChecker.WarningComment, annotation.Comment);
    }

    [Fact]
    public void ResolveByMarking_ExistingAnnotation_PreservesCommentAndShowIcon_OnlySetsWarning()
    {
        var project = OneTabProject();
        project.Tabs[0].Lanes[0].Notes.AddRange([48, 48]);
        project.Tabs[0].Lanes[0].Annotations.Add(new NoteAnnotation(48, "既存のメモ", false, ShowIcon: true));
        var duplicates = DuplicateNoteChecker.FindDuplicates(project);

        DuplicateNoteChecker.ResolveByMarking(project, duplicates);

        var annotation = Assert.Single(project.Tabs[0].Lanes[0].Annotations);
        Assert.Equal("既存のメモ", annotation.Comment); // 既存コメントは保持
        Assert.True(annotation.ShowIcon); // 既存フラグも保持
        Assert.True(annotation.Warning); // 警告のみ新たにON
    }
}
