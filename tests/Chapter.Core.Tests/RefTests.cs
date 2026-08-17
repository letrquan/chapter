using Chapter.Core;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Branches, stash and tags against repositories the test creates and destroys.
///
/// Same rule as <see cref="StagingTests"/> and <see cref="WriteFoundationsTests"/>: never
/// the validation repos. Everything here switches branches, stashes and deletes refs, and a
/// test that did any of that to real work would lose it.
///
/// Most of these exist to pin git's *behaviour* rather than the app's, because Phase 3 is
/// built on several facts that are easy to assume wrongly — chiefly that the stash is
/// shared by every worktree in a repository.
/// </summary>
public class RefTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    /// <summary>
    /// A repository with one commit, plus a linked worktree on its own branch.
    ///
    /// The second worktree is not scenery: it is the state this app is built for, and it is
    /// what makes "checked out elsewhere" an ordinary outcome rather than an edge case.
    /// </summary>
    private async Task<(string Main, string Linked)> NewRepoWithWorktreeAsync()
    {
        var main = await NewRepoAsync();
        var linked = Path.Combine(Path.GetDirectoryName(main)!, Path.GetFileName(main) + "-linked");
        _created.Add(linked);

        await RunAsync(main, "branch", "feature");
        await RunAsync(main, "worktree", "add", linked, "feature");

        return (main, linked);
    }

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-ref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");

        // Pinned rather than inherited. A machine with core.autocrlf=true globally — which
        // is the Windows default from the installer — rewrites every line ending on
        // checkout, so a file this test wrote with \n comes back with \r\n and every content
        // assertion below fails for a reason that has nothing to do with what is being
        // tested. StagingTests pins it for the same reason and then varies it deliberately.
        await RunAsync(root, "config", "core.autocrlf", "false");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        return root;
    }

    private static async Task<GitResult> RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
        return result;
    }

    /// <summary>
    /// Gives the repository a remote and one tracking ref, without a network.
    ///
    /// The remote has to be *configured*, not merely have refs under
    /// <c>refs/remotes/origin/</c>: without a <c>remote.origin</c> section git refuses
    /// <c>--set-upstream-to</c> with "starting point 'origin/main' is not a branch", because
    /// nothing tells it that namespace belongs to a remote. The URL is never contacted.
    /// </summary>
    private static async Task AddFakeRemoteAsync(string root)
    {
        await RunAsync(root, "remote", "add", "origin", root);
        await RunAsync(root, "update-ref", "refs/remotes/origin/main", "HEAD");
    }

    private static async Task<WorkspaceService> NewWorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    public void Dispose()
    {
        // Linked worktrees first: removing the repository out from under one leaves a
        // directory git still believes in, and the delete below is less likely to succeed.
        foreach (var root in Enumerable.Reverse(_created)) Delete(root);
        GC.SuppressFinalize(this);
    }

    private static void Delete(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            // git marks objects read-only, which blocks a plain recursive delete.
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
    // Listing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Lists_branches_and_says_which_worktree_holds_each()
    {
        var (main, linked) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        var branches = await workspace.Branches.ListAsync(main);

        var mainBranch = branches.Single(b => b.Name == "main");
        var feature = branches.Single(b => b.Name == "feature");

        Assert.True(mainBranch.IsCurrent);
        Assert.False(feature.IsCurrent);

        // The field the whole feature turns on: `feature` is unavailable to this worktree
        // because the other one has it, and the UI can say so before anything is attempted.
        Assert.True(feature.IsCheckedOutElsewhere);
        Assert.Equal(linked, feature.CheckedOutIn, ignoreCase: true);
    }

    [Fact]
    public async Task The_same_list_read_from_the_other_worktree_swaps_which_branch_is_current()
    {
        var (main, linked) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        var fromLinked = await workspace.Branches.ListAsync(linked);

        Assert.True(fromLinked.Single(b => b.Name == "feature").IsCurrent);
        Assert.False(fromLinked.Single(b => b.Name == "main").IsCurrent);

        // Still checked out — just not here.
        Assert.True(fromLinked.Single(b => b.Name == "main").IsCheckedOutElsewhere);
    }

    [Fact]
    public async Task Reports_ahead_and_behind_against_an_upstream()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await AddFakeRemoteAsync(root);
        await RunAsync(root, "branch", "--set-upstream-to=origin/main", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "two\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "ahead by one");

        var branch = (await workspace.Branches.ListAsync(root)).Single(b => b.Name == "main");

        Assert.Equal("origin/main", branch.Upstream);
        Assert.Equal(1, branch.Ahead);
        Assert.False(branch.IsUpstreamGone);
    }

    [Fact]
    public async Task An_upstream_that_no_longer_exists_reads_as_gone_rather_than_as_in_sync()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await AddFakeRemoteAsync(root);
        await RunAsync(root, "branch", "--set-upstream-to=origin/main", "main");

        // The remote branch disappears while the configuration still names it — a colleague
        // deleting a merged branch, which is the ordinary way a branch becomes "gone".
        await RunAsync(root, "update-ref", "-d", "refs/remotes/origin/main");

        var branch = (await workspace.Branches.ListAsync(root)).Single(b => b.Name == "main");

        Assert.True(branch.IsUpstreamGone);

        // The counts stay null rather than zero. Zero would render as "in sync", which is
        // the one thing a deleted upstream definitely is not.
        Assert.Null(branch.Ahead);
        Assert.Null(branch.Behind);
    }

    // -----------------------------------------------------------------------
    // Switching
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Switching_to_a_branch_another_worktree_holds_is_refused_and_says_why()
    {
        var (main, _) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        var mutation = await workspace.Branches.SwitchAsync(main, "feature");

        Assert.False(mutation.Success);

        // Its own classification rather than a flavour of "would lose changes": nothing is
        // at risk and there is nothing to force, so the UI's answer is to offer that
        // worktree instead.
        Assert.Equal(GitFailure.CheckedOutElsewhere, mutation.Failure);
    }

    [Fact]
    public async Task A_dirty_tree_switches_anyway_when_no_file_disagrees()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "branch", "other");

        // Untracked, and touching nothing that differs between the branches — so git carries
        // it across. Pre-checking "the tree is dirty" would have refused this.
        await File.WriteAllTextAsync(Path.Combine(root, "Scratch.txt"), "work in progress\n");

        var mutation = await workspace.Branches.SwitchAsync(root, "other");

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("other", await workspace.Branches.CurrentBranchAsync(root));
        Assert.True(File.Exists(Path.Combine(root, "Scratch.txt")));
    }

    [Fact]
    public async Task A_dirty_tree_that_would_be_overwritten_is_refused_with_the_work_intact()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "other");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "other branch content\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "diverge");
        await RunAsync(root, "switch", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "local edit\n");

        var mutation = await workspace.Branches.SwitchAsync(root, "other");

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, mutation.Failure);

        // The refusal has to be total. A half-done switch would be worse than no switch.
        Assert.Equal("main", await workspace.Branches.CurrentBranchAsync(root));
        Assert.Equal("local edit\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
    }

    [Fact]
    public async Task Stash_and_switch_carries_the_work_onto_the_new_branch()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "other");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "other branch content\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "diverge");
        await RunAsync(root, "switch", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "carry me\n");

        var mutation = await workspace.Branches
            .SwitchAsync(root, "other", CheckoutStrategy.StashAndSwitch);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("other", await workspace.Branches.CurrentBranchAsync(root));

        // The work arrived, and the stash it travelled in was consumed rather than left
        // behind for the user to find later and wonder about.
        Assert.Equal("carry me\n", await File.ReadAllTextAsync(Path.Combine(root, "B.txt")));
        Assert.Empty(await workspace.Stashes.ListAsync(root));
    }

    [Fact]
    public async Task A_failed_stash_and_switch_puts_the_stashed_work_back()
    {
        var (main, _) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(main, "B.txt"), "do not lose me\n");

        // `feature` is held by the linked worktree, so the switch fails after the stash has
        // already emptied the tree. This is the path where the stash is the only copy.
        var mutation = await workspace.Branches
            .SwitchAsync(main, "feature", CheckoutStrategy.StashAndSwitch);

        Assert.False(mutation.Success);

        Assert.True(
            File.Exists(Path.Combine(main, "B.txt")),
            "the stash was not restored after the switch failed — the user's work is only in the stash");

        Assert.Empty(await workspace.Stashes.ListAsync(main));
    }

    // -----------------------------------------------------------------------
    // Create, rename, delete
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Creates_a_branch_and_switches_to_it()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var mutation = await workspace.Branches.CreateAsync(root, "feature/login");

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("feature/login", await workspace.Branches.CurrentBranchAsync(root));
    }

    [Fact]
    public async Task Creating_without_checking_out_leaves_the_worktree_where_it_was()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var mutation = await workspace.Branches.CreateAsync(root, "later", checkout: false);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("main", await workspace.Branches.CurrentBranchAsync(root));
        Assert.Contains(await workspace.Branches.ListAsync(root), b => b.Name == "later");
    }

    [Fact]
    public async Task An_invalid_name_is_refused_without_running_git()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var mutation = await workspace.Branches.CreateAsync(root, "no spaces allowed");

        Assert.False(mutation.Success);
        Assert.Equal(0, mutation.Attempts);
        Assert.Contains("spaces", mutation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Renaming_a_branch_checked_out_in_another_worktree_moves_that_worktree_with_it()
    {
        // Worth pinning because the obvious guess is that git refuses, as it does for a
        // delete. It does not: the rename succeeds and the other worktree follows the ref.
        var (main, linked) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        var mutation = await workspace.Branches.RenameAsync(main, "feature", "feature-renamed");

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("feature-renamed", await workspace.Branches.CurrentBranchAsync(linked));
    }

    [Fact]
    public async Task Deleting_a_branch_another_worktree_holds_is_refused()
    {
        var (main, _) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        var mutation = await workspace.Branches.DeleteAsync(main, "feature");

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.CheckedOutElsewhere, mutation.Failure);
    }

    [Fact]
    public async Task Deleting_an_unmerged_branch_is_refused_until_it_is_forced()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "wip");
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "unmerged work\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "work nobody else has");
        await RunAsync(root, "switch", "main");

        var refused = await workspace.Branches.DeleteAsync(root, "wip");

        Assert.False(refused.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, refused.Failure);

        var forced = await workspace.Branches.DeleteAsync(root, "wip", force: true);
        Assert.True(forced.Success, forced.Message);
    }

    [Fact]
    public async Task Deleting_a_branch_is_undoable_and_brings_its_commits_back()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "wip");
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "unmerged work\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "work nobody else has");

        var tip = (await Git.RunAsync(root, default, "rev-parse", "HEAD")).TrimEnd('\n', '\r');
        await RunAsync(root, "switch", "main");

        Assert.True((await workspace.Branches.DeleteAsync(root, "wip", force: true)).Success);
        Assert.DoesNotContain(await workspace.Branches.ListAsync(root), b => b.Name == "wip");

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);

        var restored = (await workspace.Branches.ListAsync(root)).Single(b => b.Name == "wip");

        // Back at exactly the same commit, so the commits that were only on it are reachable
        // again. This is what lets the confirmation say "undoable" rather than "permanent".
        Assert.Equal(tip, restored.Sha);
    }

    [Fact]
    public async Task Undoing_a_branch_delete_still_works_after_something_else_commits()
    {
        // The case VerifiesHead exists for. Recreating a ref at a remembered sha is correct
        // whatever HEAD has done since, and in this app an agent committing in the same
        // worktree between the delete and the undo is the expected case, not a rare one.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "branch", "doomed");
        Assert.True((await workspace.Branches.DeleteAsync(root, "doomed")).Success);

        await File.WriteAllTextAsync(Path.Combine(root, "Agent.txt"), "an agent was here\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "the agent's commit");

        var undone = await workspace.Undo.UndoAsync(root);

        Assert.True(undone.Success, undone.Message);
        Assert.Contains(await workspace.Branches.ListAsync(root), b => b.Name == "doomed");
    }

    [Fact]
    public async Task Sets_and_clears_an_upstream()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await AddFakeRemoteAsync(root);

        Assert.True((await workspace.Branches.SetUpstreamAsync(root, "main", "origin/main")).Success);
        Assert.Equal("origin/main", (await workspace.Branches.ListAsync(root)).Single(b => b.Name == "main").Upstream);

        Assert.True((await workspace.Branches.SetUpstreamAsync(root, "main", "")).Success);
        Assert.Null((await workspace.Branches.ListAsync(root)).Single(b => b.Name == "main").Upstream);
    }

    // -----------------------------------------------------------------------
    // Stash
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_stash_is_shared_by_every_worktree_in_the_repository()
    {
        // The fact the whole stash design rests on. `refs/stash` lives in the common git
        // directory, so a stash made in one worktree is visible — and renumbers positions —
        // in all of them. Every other git client can treat stash@{n} as an identity; this
        // one cannot.
        var (main, linked) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(main, "A.txt"), "edited in main\n");
        Assert.True((await workspace.Stashes.PushAsync(main, "from main")).Success);

        var seenFromLinked = await workspace.Stashes.ListAsync(linked);

        Assert.Single(seenFromLinked);
        Assert.Equal("from main", seenFromLinked[0].Message);
        Assert.Equal("main", seenFromLinked[0].Branch);
    }

    [Fact]
    public async Task A_stash_made_elsewhere_renumbers_the_entry_a_button_was_pointing_at()
    {
        var (main, linked) = await NewRepoWithWorktreeAsync();
        var workspace = await NewWorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(main, "A.txt"), "first\n");
        await workspace.Stashes.PushAsync(main, "the one the user picked");

        var picked = (await workspace.Stashes.ListAsync(main)).Single();
        Assert.Equal(0, picked.Index);

        // The other worktree stashes. Everything shifts down by one.
        await File.WriteAllTextAsync(Path.Combine(linked, "A.txt"), "second\n");
        await workspace.Stashes.PushAsync(linked, "an agent's stash");

        var after = await workspace.Stashes.ListAsync(main);
        Assert.Equal("an agent's stash", after[0].Message);
        Assert.Equal("the one the user picked", after[1].Message);

        // Acting on the remembered index now would drop the agent's stash. The sha check
        // is what refuses instead.
        var mutation = await workspace.Stashes.DropAsync(main, picked.Index, picked.Sha);

        Assert.False(mutation.Success);
        Assert.Contains("changed since", mutation.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was dropped.
        Assert.Equal(2, (await workspace.Stashes.ListAsync(main)).Count);
    }

    [Fact]
    public async Task Pushes_and_pops_a_stash_round_trip()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "work in progress\n");

        Assert.True((await workspace.Stashes.PushAsync(root, "wip")).Success);
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));

        var entry = (await workspace.Stashes.ListAsync(root)).Single();
        Assert.True((await workspace.Stashes.PopAsync(root, entry.Index, entry.Sha)).Success);

        Assert.Equal("work in progress\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
        Assert.Empty(await workspace.Stashes.ListAsync(root));
    }

    [Fact]
    public async Task Stashing_untracked_files_is_opt_in()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        // A tracked edit as well, so both stashes below have something to take either way
        // and the only thing under test is what happens to the untracked file.
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "edited\n");
        await File.WriteAllTextAsync(Path.Combine(root, "New.txt"), "brand new\n");

        // Without the flag git leaves the untracked file alone — which is why it is a flag
        // rather than the default: an untracked file vanishing from the tree looks exactly
        // like the app having deleted it.
        Assert.True((await workspace.Stashes.PushAsync(root, "no untracked")).Success);
        Assert.True(File.Exists(Path.Combine(root, "New.txt")));
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "edited again\n");

        Assert.True((await workspace.Stashes.PushAsync(root, "with untracked", includeUntracked: true)).Success);
        Assert.False(File.Exists(Path.Combine(root, "New.txt")));

        var entry = (await workspace.Stashes.ListAsync(root))[0];
        Assert.True((await workspace.Stashes.PopAsync(root, entry.Index, entry.Sha)).Success);
        Assert.True(File.Exists(Path.Combine(root, "New.txt")));
    }

    [Fact]
    public async Task Applying_a_stash_leaves_it_in_the_list()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "work in progress\n");
        await workspace.Stashes.PushAsync(root, "wip");

        var entry = (await workspace.Stashes.ListAsync(root)).Single();
        Assert.True((await workspace.Stashes.ApplyAsync(root, entry.Index, entry.Sha)).Success);

        Assert.Equal("work in progress\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
        Assert.Single(await workspace.Stashes.ListAsync(root));
    }

    [Fact]
    public async Task Dropping_a_stash_is_undoable()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "work worth keeping\n");
        await workspace.Stashes.PushAsync(root, "precious");

        var entry = (await workspace.Stashes.ListAsync(root)).Single();
        Assert.True((await workspace.Stashes.DropAsync(root, entry.Index, entry.Sha)).Success);
        Assert.Empty(await workspace.Stashes.ListAsync(root));

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);

        // Back, and still holding the same work — a dropped stash is unreferenced rather
        // than gone, which is why its confirmation may promise recovery.
        var restored = (await workspace.Stashes.ListAsync(root)).Single();
        Assert.Equal(entry.Sha, restored.Sha);

        Assert.True((await workspace.Stashes.PopAsync(root, restored.Index, restored.Sha)).Success);
        Assert.Equal("work worth keeping\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
    }

    [Fact]
    public async Task Stashing_during_a_merge_is_refused_by_the_guard_rather_than_by_git()
    {
        // git's own answer here is "error: could not write index", which says nothing about
        // the merge that caused it. The guard gets there first and names it.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(root, "C.txt"), "side\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "side");

        await RunAsync(root, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "C.txt"), "main\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "main");

        // Expected to fail: that is the conflict being set up.
        await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "side");

        var mutation = await workspace.Stashes.PushAsync(root, "during a merge");

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.OperationInProgress, mutation.Failure);
        Assert.Contains("merge", mutation.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Tags
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Creates_lightweight_and_annotated_tags_at_the_same_commit()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        Assert.True((await workspace.Tags.CreateAsync(root, "nightly")).Success);
        Assert.True((await workspace.Tags.CreateAsync(root, "v1.0", "the first release")).Success);

        var tags = await workspace.Tags.ListAsync(root);

        var lightweight = tags.Single(t => t.Name == "nightly");
        var annotated = tags.Single(t => t.Name == "v1.0");

        Assert.False(lightweight.IsAnnotated);
        Assert.True(annotated.IsAnnotated);
        Assert.Equal("the first release", annotated.Subject);

        var head = (await Git.RunAsync(root, default, "rev-parse", "HEAD")).TrimEnd('\n', '\r');

        // Both must report the commit. An annotated tag's ref points at a tag object, and
        // reporting that sha would give the user a hash matching nothing in the history.
        Assert.Equal(head, lightweight.Sha);
        Assert.Equal(head, annotated.Sha);
    }

    [Fact]
    public async Task Deleting_an_annotated_tag_restores_it_annotated()
    {
        // The trap: recreating from the commit sha would silently produce a lightweight tag,
        // losing the message and the tagger. Restoring the ref's own object keeps both.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await workspace.Tags.CreateAsync(root, "v1.0", "the first release");

        Assert.True((await workspace.Tags.DeleteAsync(root, "v1.0")).Success);
        Assert.Empty(await workspace.Tags.ListAsync(root));

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);

        var restored = (await workspace.Tags.ListAsync(root)).Single();

        Assert.True(restored.IsAnnotated);
        Assert.Equal("the first release", restored.Subject);
    }
}
