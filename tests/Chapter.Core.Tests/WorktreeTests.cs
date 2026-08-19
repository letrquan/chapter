using Chapter.Core;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Adding, removing, moving, locking and pruning worktrees, against repositories the test
/// creates and destroys.
///
/// Same rule as <see cref="RefTests"/> and <see cref="StagingTests"/>: never the validation
/// repos. This is the one area where a mistake deletes a directory rather than a ref.
///
/// Most of what follows pins git's behaviour rather than the app's, because Phase 7 rests on
/// a handful of facts that are easy to assume wrongly — that <c>worktree add -b</c> creates
/// the branch before it checks the path, that a single <c>--force</c> covers a dirty tree but
/// not a locked one, and that prune leaves a locked worktree alone even when its directory
/// has gone.
/// </summary>
public class WorktreeTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");

        // Pinned rather than inherited, for the reason RefTests gives: the Windows
        // installer's default rewrites every line ending on checkout.
        await RunAsync(root, "config", "core.autocrlf", "false");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        return root;
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
    }

    private static async Task<WorkspaceService> NewWorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    /// <summary>A path beside the repository, registered for cleanup before it exists.</summary>
    private string Sibling(string root, string suffix)
    {
        var path = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-" + suffix);
        _created.Add(path);
        return path;
    }

    public void Dispose()
    {
        // Linked worktrees first: removing the repository out from under one leaves a
        // directory git still believes in.
        foreach (var root in Enumerable.Reverse(_created)) Delete(root);
        GC.SuppressFinalize(this);
    }

    private static void Delete(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // -----------------------------------------------------------------------
    // Adding
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Adds_a_worktree_on_a_new_branch()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "feature");

        var result = await workspace.Worktrees.AddAsync(root, path, "feature", createBranch: true);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(path, "A.txt")));

        var worktrees = await workspace.Worktrees.ListAsync(root);
        var added = worktrees.Single(w => !w.IsMain);

        Assert.Equal("feature", added.Branch);
        Assert.Equal(path, added.Path, ignoreCase: true);
    }

    [Fact]
    public async Task Adds_a_worktree_on_a_branch_that_already_exists()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        await RunAsync(root, "branch", "existing");

        var path = Sibling(root, "existing");
        var result = await workspace.Worktrees.AddAsync(root, path, "existing");

        Assert.True(result.Success, result.Message);

        var worktrees = await workspace.Worktrees.ListAsync(root);
        Assert.Equal("existing", worktrees.Single(w => !w.IsMain).Branch);
    }

    [Fact]
    public async Task A_destination_that_is_taken_is_refused_before_the_branch_is_created()
    {
        // The reason AddAsync checks the path itself instead of letting git refuse. Git
        // creates the branch *first* and only then discovers the directory is occupied, so
        // the retry after fixing the path fails with a second error about something the user
        // never did. Both halves are asserted: the refusal, and the branch not existing.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var path = Sibling(root, "taken");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "something.txt"), "in the way\n");

        var result = await workspace.Worktrees.AddAsync(root, path, "wanted", createBranch: true);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);

        var branches = await workspace.Branches.ListAsync(root);
        Assert.DoesNotContain(branches, b => b.Name == "wanted");
    }

    [Fact]
    public async Task An_empty_directory_is_not_in_the_way()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var path = Sibling(root, "empty");
        Directory.CreateDirectory(path);

        var result = await workspace.Worktrees.AddAsync(root, path, "into-empty", createBranch: true);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task A_branch_open_in_another_worktree_is_reported_as_such()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var result = await workspace.Worktrees.AddAsync(root, Sibling(root, "again"), "main");

        Assert.False(result.Success);

        // The kind the UI branches on: nothing is at risk and there is nothing to force, so
        // the answer is "go to the worktree that has it" rather than a warning.
        Assert.Equal(GitFailure.CheckedOutElsewhere, result.Failure);
    }

    [Fact]
    public async Task A_name_that_exists_only_on_a_remote_becomes_a_branch_that_tracks_it()
    {
        // Git's own dwim, and the reason the panel decides between `-b` and a bare name
        // rather than always creating: naming a branch that exists on exactly one remote
        // sets up tracking. Passing `-b` would make a same-named branch from the local HEAD
        // that tracks nothing — indistinguishable in the list, and not what was asked for.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        // Configured, not merely present under refs/remotes: without a remote.origin section
        // git has nothing telling it that namespace belongs to a remote. The URL is never
        // contacted.
        await RunAsync(root, "remote", "add", "origin", root);
        await RunAsync(root, "update-ref", "refs/remotes/origin/topic", "HEAD");

        var path = Sibling(root, "topic");
        var result = await workspace.Worktrees.AddAsync(root, path, "topic");

        Assert.True(result.Success, result.Message);

        var branch = (await workspace.Branches.ListAsync(root)).Single(b => b.Name == "topic");
        Assert.Equal("origin/topic", branch.Upstream);
    }

    [Fact]
    public async Task A_name_git_would_reject_is_refused_before_anything_runs()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var result = await workspace.Worktrees
            .AddAsync(root, Sibling(root, "bad"), "has spaces", createBranch: true);

        Assert.False(result.Success);
        Assert.Equal("", result.CommandLine);
    }

    // -----------------------------------------------------------------------
    // Removing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_worktree_leaves_its_branch_and_its_commits_behind()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "done");

        await workspace.Worktrees.AddAsync(root, path, "done", createBranch: true);
        await RunAsync(path, "commit", "--allow-empty", "-m", "work");

        var result = await workspace.Worktrees.RemoveAsync(root, path);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(path));

        // The half the confirmation promises: committed work is untouched, because it is in
        // the repository rather than in the directory.
        var branches = await workspace.Branches.ListAsync(root);
        var branch = branches.Single(b => b.Name == "done");
        Assert.Equal("work", branch.Subject);
        Assert.Null(branch.CheckedOutIn);
    }

    [Fact]
    public async Task Removing_the_worktree_the_request_came_from_works_because_the_command_runs_elsewhere()
    {
        // Every mutation in this service runs in the *main* worktree, which is why a request
        // that names the worktree it was made from is not a special case. Running it in the
        // target would leave git's own process standing in a directory it is deleting —
        // undeletable on Windows, undefined on POSIX.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "self");

        await workspace.Worktrees.AddAsync(root, path, "self", createBranch: true);

        var result = await workspace.Worktrees.RemoveAsync(path, path);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task A_worktree_with_uncommitted_work_is_kept_until_the_removal_is_forced()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "busy");

        await workspace.Worktrees.AddAsync(root, path, "busy", createBranch: true);
        await File.WriteAllTextAsync(Path.Combine(path, "agent.txt"), "work in progress\n");

        var refused = await workspace.Worktrees.RemoveAsync(root, path);

        Assert.False(refused.Success);
        Assert.True(Directory.Exists(path));

        // Classified rather than left as Unknown, because this refusal is the one the UI
        // turns into the permanent-loss question.
        Assert.Equal(GitFailure.WouldLoseChanges, refused.Failure);

        var forced = await workspace.Worktrees.RemoveAsync(root, path, force: true);

        Assert.True(forced.Success, forced.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task A_locked_worktree_survives_even_a_forced_removal()
    {
        // Deliberate: git wants `--force --force` here and the app passes one. A lock is
        // somebody's explicit instruction to leave this alone, so the way past it is to
        // unlock — which is its own visible action — rather than a flag on the removal that
        // quietly overrides them.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "locked");

        await workspace.Worktrees.AddAsync(root, path, "locked", createBranch: true);
        await workspace.Worktrees.LockAsync(root, path, "an agent is running");

        var refused = await workspace.Worktrees.RemoveAsync(root, path, force: true);

        Assert.False(refused.Success);
        Assert.True(Directory.Exists(path));

        await workspace.Worktrees.UnlockAsync(root, path);
        var removed = await workspace.Worktrees.RemoveAsync(root, path);

        Assert.True(removed.Success, removed.Message);
    }

    [Fact]
    public async Task The_main_worktree_cannot_be_removed()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var result = await workspace.Worktrees.RemoveAsync(root, root);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(root));

        // Refused by the app rather than by git, so the sentence names the reason rather
        // than repeating "'.' is a main working tree".
        Assert.Contains("main worktree", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_worktree_belonging_to_another_repository_is_refused()
    {
        // The target is resolved against this repository's own list rather than passed to
        // git. Without that, a path from the front-end reaches any worktree on the machine.
        var root = await NewRepoAsync();
        var elsewhere = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var stranger = Sibling(elsewhere, "stranger");
        await workspace.Worktrees.AddAsync(elsewhere, stranger, "stranger", createBranch: true);

        var result = await workspace.Worktrees.RemoveAsync(root, stranger);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(stranger));
        Assert.Contains("not part of this repository", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Locking and moving
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_lock_reason_comes_back_in_the_list()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "held");

        await workspace.Worktrees.AddAsync(root, path, "held", createBranch: true);
        var locked = await workspace.Worktrees.LockAsync(root, path, "waiting on the agent");

        Assert.True(locked.Success, locked.Message);

        var listed = (await workspace.Worktrees.ListAsync(root)).Single(w => !w.IsMain);
        Assert.True(listed.IsLocked);
        Assert.Equal("waiting on the agent", listed.LockReason);

        await workspace.Worktrees.UnlockAsync(root, path);
        Assert.False((await workspace.Worktrees.ListAsync(root)).Single(w => !w.IsMain).IsLocked);
    }

    [Fact]
    public async Task Moving_a_worktree_takes_its_branch_and_its_working_files_with_it()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var from = Sibling(root, "before");
        var to = Sibling(root, "after");

        await workspace.Worktrees.AddAsync(root, from, "moving", createBranch: true);
        await File.WriteAllTextAsync(Path.Combine(from, "note.txt"), "carried\n");

        var result = await workspace.Worktrees.MoveAsync(root, from, to);

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(from));
        Assert.Equal("carried\n", await File.ReadAllTextAsync(Path.Combine(to, "note.txt")));

        var moved = (await workspace.Worktrees.ListAsync(root)).Single(w => !w.IsMain);
        Assert.Equal(to, moved.Path, ignoreCase: true);
        Assert.Equal("moving", moved.Branch);
    }

    [Fact]
    public async Task A_move_onto_an_occupied_path_is_refused_without_touching_either()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var from = Sibling(root, "here");
        var to = Sibling(root, "occupied");

        await workspace.Worktrees.AddAsync(root, from, "here", createBranch: true);
        Directory.CreateDirectory(to);

        var result = await workspace.Worktrees.MoveAsync(root, from, to);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(from));
    }

    // -----------------------------------------------------------------------
    // Pruning
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Prune_names_what_it_would_forget_before_it_forgets_it()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "vanished");

        await workspace.Worktrees.AddAsync(root, path, "vanished", createBranch: true);

        // What an agent's cleanup script does, and what the roadmap describes as already
        // true of one of the validation repos.
        Directory.Delete(path, recursive: true);

        var listed = (await workspace.Worktrees.ListAsync(root)).Single(w => !w.IsMain);
        Assert.True(listed.IsPrunable);

        var preview = await workspace.Worktrees.PreviewPruneAsync(root);
        var entry = Assert.Single(preview);

        // The directory's own basename, which is all git keeps once the directory is gone —
        // and the only name the confirmation has to show for it.
        Assert.Equal(Path.GetFileName(path), entry.Name);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));

        var pruned = await workspace.Worktrees.PruneAsync(root);
        Assert.True(pruned.Success, pruned.Message);

        Assert.Single(await workspace.Worktrees.ListAsync(root));
        Assert.Empty(await workspace.Worktrees.PreviewPruneAsync(root));
    }

    [Fact]
    public async Task Prune_leaves_a_locked_worktree_alone_even_when_its_directory_is_gone()
    {
        // The case locking exists for — a worktree on a drive that is not always mounted —
        // and the reason the preview is a dry run of the command rather than a reading of
        // the list's prunable flags. Git applies both rules; the app applies neither.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);
        var path = Sibling(root, "detachable");

        await workspace.Worktrees.AddAsync(root, path, "detachable", createBranch: true);
        await workspace.Worktrees.LockAsync(root, path, "lives on a USB drive");
        Directory.Delete(path, recursive: true);

        Assert.Empty(await workspace.Worktrees.PreviewPruneAsync(root));

        await workspace.Worktrees.PruneAsync(root);

        Assert.Equal(2, (await workspace.Worktrees.ListAsync(root)).Count);
    }

    [Fact]
    public void Prune_preview_reads_gits_lines_and_ignores_anything_else()
    {
        var entries = WorktreeService.ParsePrunePreview(
            "Removing worktrees/gone: gitdir file points to non-existent location\n" +
            "something else entirely\n" +
            "Removing worktrees/other: no such file\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal("gone", entries[0].Name);
        Assert.Equal("gitdir file points to non-existent location", entries[0].Reason);
        Assert.Equal("other", entries[1].Name);
    }

    // -----------------------------------------------------------------------
    // Suggested paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_repository_with_no_linked_worktrees_is_offered_a_sibling()
    {
        // Sibling rather than nested, where there is no precedent to follow: a worktree
        // inside the main one shows up in that worktree's own status as an untracked
        // directory, which in this app means the repository being reviewed grows a phantom
        // change that is really another agent's entire checkout.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var suggestion = await workspace.Worktrees.SuggestPathAsync(root, "feature");

        Assert.Equal(Path.GetDirectoryName(root), Path.GetDirectoryName(suggestion));
        Assert.Equal($"{Path.GetFileName(root)}-feature", Path.GetFileName(suggestion));
    }

    [Fact]
    public async Task A_repository_that_nests_its_worktrees_is_offered_another_nested_one()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var nested = Path.Combine(root, ".worktrees", "first");
        await workspace.Worktrees.AddAsync(root, nested, "first", createBranch: true);

        var suggestion = await workspace.Worktrees.SuggestPathAsync(root, "second");

        Assert.Equal(Path.Combine(root, ".worktrees", "second"), suggestion);
    }

    [Fact]
    public async Task A_suggestion_steps_past_a_path_that_is_already_taken()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var occupied = Sibling(root, "feature");
        Directory.CreateDirectory(occupied);

        var suggestion = await workspace.Worktrees.SuggestPathAsync(root, "feature");

        Assert.Equal($"{Path.GetFileName(root)}-feature-2", Path.GetFileName(suggestion));
    }

    [Fact]
    public async Task A_branch_name_with_a_slash_becomes_one_directory_rather_than_two()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var suggestion = await workspace.Worktrees.SuggestPathAsync(root, "feature/login");

        Assert.Equal($"{Path.GetFileName(root)}-feature-login", Path.GetFileName(suggestion));
    }
}
