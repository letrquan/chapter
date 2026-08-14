using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Integration tests against real repositories on this machine. Captured git output can
/// only prove the parser handles what I thought git emits; these prove it handles what
/// git actually emits, at real scale, on both worktree layouts.
///
/// They skip rather than fail when the repos are absent, so the suite stays green on any
/// other machine.
/// </summary>
public class RealRepoTests
{
    private const string HeatRepo = @"I:\MyProject\02-AI-ML-Projects\heat";
    private const string BookRepo = @"I:\MyProject\02-AI-ML-Projects\book";

    private static readonly GitCli Git = new();

    [SkippableFact]
    public async Task Book_reports_all_four_sibling_worktrees()
    {
        Skip.IfNot(Directory.Exists(BookRepo), $"{BookRepo} not present");

        var worktrees = await new WorktreeService(Git).ListAsync(BookRepo);

        // Siblings of the repo directory rather than nested inside it.
        Assert.Equal(4, worktrees.Count);
        Assert.Single(worktrees, w => w.IsMain);
        Assert.All(worktrees, w => Assert.NotNull(w.Branch));
        Assert.Contains(worktrees, w => w.Branch == "feat/review");
    }

    [SkippableFact]
    public async Task Heat_reports_its_nested_worktree_and_marks_it_prunable()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var worktrees = await new WorktreeService(Git).ListAsync(HeatRepo);

        Assert.Contains(worktrees, w => w.IsMain);

        // Nested under .worktrees/, and stale — the app must render it without crashing.
        var nested = worktrees.SingleOrDefault(w => !w.IsMain);
        Assert.NotNull(nested);
        Assert.Contains(".worktrees", nested.Path);
        Assert.True(nested.IsPrunable);
        Assert.False(nested.IsUsable);
    }

    [SkippableFact]
    public async Task Base_resolution_falls_through_to_local_main_when_origin_head_is_unset()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var resolver = new BaseBranchResolver(Git);

        var defaultBranch = await resolver.ResolveDefaultBranchAsync(HeatRepo);
        var diffBase = await resolver.ResolveBaseAsync(HeatRepo);

        // Neither validation repo sets origin/HEAD, so this exercises the fallback chain.
        Assert.Equal("main", defaultBranch);
        Assert.Equal("merge-base with main", diffBase.Description);
        Assert.Equal(40, diffBase.Sha.Length);
    }

    [SkippableFact]
    public async Task Heat_changed_files_match_git_and_include_uncommitted_cs_edits()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var resolver = new BaseBranchResolver(Git);
        var diffBase = await resolver.ResolveBaseAsync(HeatRepo);
        var files = await new DiffService(Git).GetChangedFilesAsync(HeatRepo, diffBase);

        Assert.NotEmpty(files);

        // The uncommitted edits sitting in this repo are C# under src/.
        Assert.Contains(files, f => f.Path.EndsWith(".cs", StringComparison.Ordinal));

        // Every entry must carry a usable path and a coherent side configuration.
        Assert.All(files, f =>
        {
            Assert.NotEmpty(f.Path);
            Assert.DoesNotContain('\\', f.Path);   // git-style separators throughout
            Assert.True(f.HasBaseSide || f.HasWorkingSide);
        });
    }

    [SkippableFact]
    public async Task Both_sides_of_a_modified_file_are_retrievable_and_differ()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var diff = new DiffService(Git);
        var diffBase = await new BaseBranchResolver(Git).ResolveBaseAsync(HeatRepo);
        var files = await diff.GetChangedFilesAsync(HeatRepo, diffBase);

        var modified = files.FirstOrDefault(f => f.Kind == ChangeKind.Modified && !f.IsBinary);
        Skip.If(modified is null, "no modified text file currently in the worktree");

        var baseContent = await diff.GetBaseContentAsync(HeatRepo, diffBase.Sha, modified!.BasePath);
        var workingContent = await DiffService.GetWorkingContentAsync(HeatRepo, modified.Path);

        Assert.False(baseContent.IsBinary);
        Assert.NotEmpty(baseContent.Text);
        Assert.NotEmpty(workingContent.Text);
        Assert.NotEqual(baseContent.Text, workingContent.Text);
    }

    [SkippableFact]
    public async Task Added_file_has_an_empty_base_side_rather_than_throwing()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var diff = new DiffService(Git);
        var diffBase = await new BaseBranchResolver(Git).ResolveBaseAsync(HeatRepo);
        var files = await diff.GetChangedFilesAsync(HeatRepo, diffBase);

        var added = files.FirstOrDefault(f => f.Kind is ChangeKind.Added or ChangeKind.Untracked);
        Skip.If(added is null, "no added or untracked file currently in the worktree");

        // git show fails for a path that did not exist at the base; that is expected,
        // not an error to surface.
        var baseContent = await diff.GetBaseContentAsync(HeatRepo, diffBase.Sha, added!.BasePath);

        Assert.Equal("", baseContent.Text);
        Assert.False(added.HasBaseSide);
    }
}
