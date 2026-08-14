using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The four views of a worktree's work.
///
/// Assertions are on the relationships between scopes rather than on file counts, because
/// the counts change every time an agent touches the repository. The relationships do not.
/// </summary>
public class DiffScopeTests
{
    private const string HeatRepo = @"I:\MyProject\02-AI-ML-Projects\heat";

    private static readonly GitCli Git = new();

    private static async Task<IReadOnlyList<ChangedFile>> FilesForAsync(string repo, DiffScope scope)
    {
        var diffBase = await new BaseBranchResolver(Git).ResolveBaseAsync(repo, scope);
        return await new DiffService(Git).GetChangedFilesAsync(repo, diffBase);
    }

    [SkippableFact]
    public async Task Branch_view_is_the_union_of_committed_and_uncommitted()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var branch = await FilesForAsync(HeatRepo, DiffScope.Branch);
        var committed = await FilesForAsync(HeatRepo, DiffScope.Committed);
        var uncommitted = await FilesForAsync(HeatRepo, DiffScope.Uncommitted);

        var branchPaths = branch.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var union = committed.Select(f => f.Path)
            .Concat(uncommitted.Select(f => f.Path))
            .ToHashSet(StringComparer.Ordinal);

        // A file changed on the branch is either committed, or dirty, or both — there is
        // no fourth possibility, so the union must reproduce the branch view exactly.
        Assert.Equal(branchPaths.OrderBy(p => p, StringComparer.Ordinal),
                     union.OrderBy(p => p, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task Uncommitted_view_contains_only_files_marked_uncommitted()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var uncommitted = await FilesForAsync(HeatRepo, DiffScope.Uncommitted);
        Skip.If(uncommitted.Count == 0, "worktree is clean right now");

        Assert.All(uncommitted, f => Assert.True(f.IsUncommitted, $"{f.Path} was not marked uncommitted"));
    }

    [SkippableFact]
    public async Task Committed_view_excludes_untracked_files()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var committed = await FilesForAsync(HeatRepo, DiffScope.Committed);

        // Untracked files are working-tree state; they cannot appear in a comparison that
        // ends at a commit.
        Assert.DoesNotContain(committed, f => f.Kind == ChangeKind.Untracked);
        Assert.All(committed, f => Assert.False(f.Kind == ChangeKind.Untracked));
    }

    [SkippableFact]
    public async Task Branch_view_flags_which_files_are_still_dirty()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var branch = await FilesForAsync(HeatRepo, DiffScope.Branch);
        var uncommitted = await FilesForAsync(HeatRepo, DiffScope.Uncommitted);
        Skip.If(uncommitted.Count == 0, "worktree is clean right now");

        var dirtyPaths = uncommitted.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        // The marker in the branch-wide list has to agree with the uncommitted-only view,
        // or the dot is lying about what still needs committing.
        foreach (var file in branch)
            Assert.Equal(dirtyPaths.Contains(file.Path), file.IsUncommitted);
    }

    [SkippableFact]
    public async Task Bases_describe_themselves_distinctly_per_scope()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var resolver = new BaseBranchResolver(Git);

        var branch = await resolver.ResolveBaseAsync(HeatRepo, DiffScope.Branch);
        var committed = await resolver.ResolveBaseAsync(HeatRepo, DiffScope.Committed);
        var uncommitted = await resolver.ResolveBaseAsync(HeatRepo, DiffScope.Uncommitted);
        var last = await resolver.ResolveBaseAsync(HeatRepo, DiffScope.LastCommit);

        // Branch and Committed share a starting point and differ only in where they end.
        Assert.Equal(branch.Sha, committed.Sha);
        Assert.Null(branch.ToRef);
        Assert.Equal("HEAD", committed.ToRef);

        Assert.True(branch.IncludeUntracked);
        Assert.False(committed.IncludeUntracked);
        Assert.True(uncommitted.IncludeUntracked);
        Assert.False(last.IncludeUntracked);

        Assert.Equal("uncommitted changes", uncommitted.Description);
        Assert.Equal("last commit", last.Description);
        Assert.Equal("HEAD", last.ToRef);
    }

    [SkippableFact]
    public async Task Last_commit_view_matches_git_show_for_that_commit()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var files = await FilesForAsync(HeatRepo, DiffScope.LastCommit);

        // Cross-check against git itself rather than trusting our own plumbing.
        var expected = await Git.RunAsync(HeatRepo, default, "diff", "--name-only", "HEAD~1", "HEAD");
        var expectedPaths = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            expectedPaths.OrderBy(p => p, StringComparer.Ordinal),
            files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
    }
}
