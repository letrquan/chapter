using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Staging, discarding and committing, against repositories the test creates and destroys.
///
/// Same rule as <see cref="WriteFoundationsTests"/>: never the validation repos. Everything
/// here stages, resets and commits, and a test that did any of that to real work would lose
/// it.
/// </summary>
public class StagingTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync(bool crlf = false, bool commit = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-stage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");

        // The setting that makes hunk staging hard: the working tree holds CRLF while the
        // index holds LF, so any patch built from what the editor displays fails to apply.
        await RunAsync(root, "config", "core.autocrlf", crlf ? "true" : "false");

        if (commit)
        {
            await WriteAsync(root, "A.txt", "one\ntwo\nthree\n");
            await RunAsync(root, "add", "-A");
            await RunAsync(root, "commit", "-m", "initial");
        }

        return root;
    }

    private static async Task<GitResult> RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
        return result;
    }

    /// <summary>Writes with LF endings regardless of platform, so fixtures are byte-exact.</summary>
    private static Task WriteAsync(string root, string path, string text)
    {
        var absolute = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        return File.WriteAllTextAsync(absolute, text.Replace("\r\n", "\n"));
    }

    private static async Task<WorkspaceService> NewWorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    public void Dispose()
    {
        foreach (var root in _created) Delete(root);
        GC.SuppressFinalize(this);
    }

    private static void Delete(string root)
    {
        try
        {
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
    // Whole-file staging
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Staging_a_file_moves_it_from_the_unstaged_side_to_the_staged_one()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nCHANGED\nthree\n");

        var before = await workspace.GetCommitViewAsync(root);
        Assert.Contains(before.Unstaged, f => f.Path == "A.txt");
        Assert.Empty(before.Staged);
        Assert.False(before.Readiness.CanCommit);
        Assert.Equal("Nothing is staged.", before.Readiness.Reason);

        var staged = await workspace.Staging.StageAsync(root, ["A.txt"]);
        Assert.True(staged.Success, staged.Message);

        var after = await workspace.GetCommitViewAsync(root);

        Assert.Contains(after.Staged, f => f.Path == "A.txt");
        Assert.DoesNotContain(after.Unstaged, f => f.Path == "A.txt");
        Assert.True(after.Readiness.CanCommit);
    }

    [Fact]
    public async Task A_file_staged_and_then_edited_again_appears_on_both_sides()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nSTAGED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);

        // The `MM` state: staged once, then edited again on disk.
        await WriteAsync(root, "A.txt", "one\nSTAGED\nWORKTREE\n");

        var state = await workspace.GetCommitViewAsync(root);

        Assert.Contains(state.Staged, f => f.Path == "A.txt");
        Assert.Contains(state.Unstaged, f => f.Path == "A.txt");
    }

    [Fact]
    public async Task Unstaging_works_before_the_first_commit_exists()
    {
        // `restore --staged` resolves HEAD and there is not one yet: it exits 128 with
        // "could not resolve HEAD" and leaves the file staged. This is the case that needs
        // the `rm --cached` fallback, and it is the ordinary state of a brand-new repository.
        var root = await NewRepoAsync(commit: false);
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "new.txt", "hello\n");
        await RunAsync(root, "add", "new.txt");

        var state = await workspace.GetCommitViewAsync(root);
        Assert.True(state.IsUnborn);
        Assert.Contains(state.Staged, f => f.Path == "new.txt");

        var unstaged = await workspace.Staging.UnstageAsync(root, ["new.txt"]);
        Assert.True(unstaged.Success, unstaged.Message);

        var after = await workspace.GetCommitViewAsync(root);

        Assert.Empty(after.Staged);
        Assert.Contains(after.Unstaged, f => f.Path == "new.txt" && f.Kind == ChangeKind.Untracked);

        // Unstaging must never touch the file itself.
        Assert.True(File.Exists(Path.Combine(root, "new.txt")));
    }

    [Fact]
    public async Task A_file_staged_then_deleted_from_disk_is_still_reported_as_staged()
    {
        // The case that stops the commit view being derived from the review scan: this file
        // is in neither the branch diff nor the working tree, and committing still includes
        // it. Anything that infers "staged" from the changed-file list misses it entirely.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "ghost.txt", "content\n");
        await workspace.Staging.StageAsync(root, ["ghost.txt"]);
        File.Delete(Path.Combine(root, "ghost.txt"));

        var state = await workspace.GetCommitViewAsync(root);

        Assert.Contains(state.Staged, f => f.Path == "ghost.txt" && f.Kind == ChangeKind.Added);
        Assert.True(state.Readiness.CanCommit);
    }

    [Fact]
    public async Task Paths_that_look_like_globs_are_staged_literally()
    {
        // `a[1].txt` is a valid pathspec matching `a1.txt`, and matching nothing here. Passed
        // unguarded it stages nothing and reports success, which is the worst combination.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "a[1].txt", "bracketed\n");

        var staged = await workspace.Staging.StageAsync(root, ["a[1].txt"]);
        Assert.True(staged.Success, staged.Message);

        var state = await workspace.GetCommitViewAsync(root);
        Assert.Contains(state.Staged, f => f.Path == "a[1].txt");
    }

    // -----------------------------------------------------------------------
    // Discarding
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Discarding_unstaged_changes_keeps_what_was_already_staged()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nSTAGED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);
        await WriteAsync(root, "A.txt", "one\nSTAGED\nUNSTAGED\n");

        var discarded = await workspace.Staging.DiscardAsync(root, ["A.txt"], DiscardTarget.Unstaged);
        Assert.True(discarded.Success, discarded.Message);

        // Back to the staged version, not to HEAD.
        var text = await File.ReadAllTextAsync(Path.Combine(root, "A.txt"));
        Assert.Contains("STAGED", text);
        Assert.DoesNotContain("UNSTAGED", text);
    }

    [Fact]
    public async Task Discarding_everything_restores_the_file_from_HEAD()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nSTAGED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);
        await WriteAsync(root, "A.txt", "one\nSTAGED\nUNSTAGED\n");

        var discarded = await workspace.Staging.DiscardAsync(root, ["A.txt"], DiscardTarget.Everything);
        Assert.True(discarded.Success, discarded.Message);

        var text = await File.ReadAllTextAsync(Path.Combine(root, "A.txt"));
        Assert.DoesNotContain("STAGED", text);
        Assert.DoesNotContain("UNSTAGED", text);

        var state = await workspace.GetCommitViewAsync(root);
        Assert.Empty(state.Staged);
        Assert.Empty(state.Unstaged);
    }

    [Fact]
    public async Task Discarding_an_untracked_file_deletes_it_rather_than_failing_to_restore_it()
    {
        // `git restore` has no source for an untracked path and fails with "did not match".
        // Passing one in with the rest would leave the whole discard failed and nothing done.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nEDITED\nthree\n");
        await WriteAsync(root, "junk.txt", "agent scratch\n");

        var discarded = await workspace.Staging.DiscardAsync(
            root, ["A.txt"], DiscardTarget.Everything, ["junk.txt"]);

        Assert.True(discarded.Success, discarded.Message);
        Assert.False(File.Exists(Path.Combine(root, "junk.txt")));

        var text = await File.ReadAllTextAsync(Path.Combine(root, "A.txt"));
        Assert.DoesNotContain("EDITED", text);
    }

    [Fact]
    public async Task Discarding_only_untracked_files_reports_success_without_running_git()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "junk.txt", "scratch\n");

        var discarded = await workspace.Staging.DiscardAsync(
            root, [], DiscardTarget.Everything, ["junk.txt"]);

        Assert.True(discarded.Success, discarded.Message);
        Assert.False(File.Exists(Path.Combine(root, "junk.txt")));
    }

    [Fact]
    public async Task A_discard_cannot_reach_outside_the_worktree()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var outside = Path.Combine(Path.GetTempPath(), $"chapter-victim-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "not yours\n");

        try
        {
            var escape = "../" + Path.GetFileName(outside);
            var discarded = await workspace.Staging.DiscardAsync(root, [], DiscardTarget.Everything, [escape]);

            Assert.False(discarded.Success);
            Assert.True(File.Exists(outside), "a path climbing out of the worktree deleted a real file");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // -----------------------------------------------------------------------
    // Committing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_commit_records_the_message_and_an_undo_point()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nCOMMITTED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);

        var before = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;

        var mutation = await workspace.Commits.CommitAsync(root, new CommitRequest
        {
            Message = "subject line\n\nbody paragraph one\nbody paragraph two",
        });

        Assert.True(mutation.Success, mutation.Message);

        // Multiline messages survive being passed as a single -m argument. They would not
        // survive a shell, and stdin is closed, so this is the property the design rests on.
        var recorded = (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).StandardOutput;
        Assert.Contains("subject line", recorded);
        Assert.Contains("body paragraph two", recorded);

        var after = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        Assert.NotEqual(before, after);

        var undo = workspace.Undo.Peek(root);
        Assert.NotNull(undo);
        Assert.Contains("subject line", undo!.Label);

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);

        Assert.Equal(before, (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed);

        // reset --soft: the commit is gone, its content is still staged.
        var state = await workspace.GetCommitViewAsync(root);
        Assert.Contains(state.Staged, f => f.Path == "A.txt");
    }

    [Fact]
    public async Task Committing_the_first_commit_is_undone_by_removing_the_ref()
    {
        var root = await NewRepoAsync(commit: false);
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "first.txt", "hello\n");
        await workspace.Staging.StageAsync(root, ["first.txt"]);

        var mutation = await workspace.Commits.CommitAsync(root, new CommitRequest { Message = "root commit" });
        Assert.True(mutation.Success, mutation.Message);

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);

        // Back to unborn, with the content still staged.
        var head = await Git.TryRunAsync(root, default, "rev-parse", "--verify", "--quiet", "HEAD");
        Assert.True(!head.Success || head.Trimmed.Length == 0);
    }

    [Fact]
    public async Task Amending_replaces_the_tip_and_undo_puts_the_original_back()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nFIRST\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);
        await workspace.Commits.CommitAsync(root, new CommitRequest { Message = "original subject" });

        var original = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;

        await WriteAsync(root, "B.txt", "extra\n");
        await workspace.Staging.StageAsync(root, ["B.txt"]);

        var amended = await workspace.Commits.CommitAsync(root, new CommitRequest
        {
            Message = "amended subject",
            Amend = true,
        });

        Assert.True(amended.Success, amended.Message);

        var count = (await Git.TryRunAsync(root, default, "rev-list", "--count", "HEAD")).Trimmed;
        Assert.Equal("2", count); // initial + the amended one, not three

        Assert.Equal("amended subject",
            (await Git.TryRunAsync(root, default, "log", "-1", "--format=%s")).Trimmed);

        var undo = workspace.Undo.Peek(root);
        Assert.NotNull(undo);
        Assert.StartsWith("amend", undo!.Label);

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);
        Assert.Equal(original, (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed);
    }

    [Fact]
    public async Task Amending_can_reuse_the_previous_message()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nEDIT\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);
        await workspace.Commits.CommitAsync(root, new CommitRequest { Message = "keep this subject" });

        await WriteAsync(root, "B.txt", "forgot this file\n");
        await workspace.Staging.StageAsync(root, ["B.txt"]);

        var amended = await workspace.Commits.CommitAsync(root, new CommitRequest
        {
            Amend = true,
            ReuseMessage = true,
        });

        Assert.True(amended.Success, amended.Message);
        Assert.Equal("keep this subject",
            (await Git.TryRunAsync(root, default, "log", "-1", "--format=%s")).Trimmed);
    }

    [Fact]
    public async Task Signoff_and_co_author_trailers_reach_the_commit()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nPAIRED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);

        var mutation = await workspace.Commits.CommitAsync(root, new CommitRequest
        {
            Message = "paired change",
            SignOff = true,
            CoAuthors = [new CoAuthor("Ada Lovelace", "ada@example.com")],
        });

        Assert.True(mutation.Success, mutation.Message);

        var body = (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).StandardOutput;
        Assert.Contains("Signed-off-by: Test <test@example.com>", body);
        Assert.Contains("Co-authored-by: Ada Lovelace <ada@example.com>", body);
    }

    [Fact]
    public async Task A_commit_is_refused_while_conflicts_are_unresolved()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        // A genuine conflicted merge, so the state comes from git rather than a fixture's
        // idea of what git would write.
        await RunAsync(root, "checkout", "-b", "other");
        await WriteAsync(root, "A.txt", "one\nTHEIRS\nthree\n");
        await RunAsync(root, "commit", "-am", "theirs");

        await RunAsync(root, "checkout", "main");
        await WriteAsync(root, "A.txt", "one\nOURS\nthree\n");
        await RunAsync(root, "commit", "-am", "ours");

        var merge = await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "other");
        Assert.False(merge.Success, "the fixture was supposed to conflict");

        var state = await workspace.GetCommitViewAsync(root);

        Assert.True(state.Repository.HasConflicts);
        Assert.False(state.Readiness.CanCommit);
        Assert.Contains("unresolved conflicts", state.Readiness.Reason!);
    }

    [Fact]
    public async Task Committing_stays_possible_once_a_merge_is_resolved()
    {
        // The guard must not block the way out of a state. Committing is how a resolved
        // merge concludes, so a blanket "no writes during a merge" would trap the user.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "checkout", "-b", "other");
        await WriteAsync(root, "A.txt", "one\nTHEIRS\nthree\n");
        await RunAsync(root, "commit", "-am", "theirs");

        await RunAsync(root, "checkout", "main");
        await WriteAsync(root, "A.txt", "one\nOURS\nthree\n");
        await RunAsync(root, "commit", "-am", "ours");

        await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "other");

        // Resolve, then stage the resolution — which is `git add` on a conflicted file.
        await WriteAsync(root, "A.txt", "one\nRESOLVED\nthree\n");
        var staged = await workspace.Staging.StageAsync(root, ["A.txt"]);
        Assert.True(staged.Success, staged.Message);

        var state = await workspace.GetCommitViewAsync(root);
        Assert.False(state.Repository.HasConflicts);
        Assert.True(state.Readiness.CanCommit);
        Assert.Contains("conclude", state.Readiness.Note!);

        var mutation = await workspace.Commits.CommitAsync(root, new CommitRequest { Message = "merge other" });
        Assert.True(mutation.Success, mutation.Message);
    }

    [Fact]
    public async Task A_detached_HEAD_is_noted_but_does_not_block_the_commit()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await RunAsync(root, "checkout", "--detach");
        await WriteAsync(root, "A.txt", "one\nDETACHED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);

        var state = await workspace.GetCommitViewAsync(root);

        Assert.True(state.Readiness.CanCommit);
        Assert.Contains("detached", state.Readiness.Note!);
    }

    // -----------------------------------------------------------------------
    // Hunk and line staging
    // -----------------------------------------------------------------------

    /// <summary>
    /// Twenty lines, so that changes at line 2 and line 18 land in separate hunks.
    ///
    /// The distance is the point. With three lines of context either side, git merges any
    /// two changes closer than about seven lines into a single hunk — so a shorter fixture
    /// tests one hunk while appearing to test two.
    /// </summary>
    private static readonly string TwentyLines =
        string.Concat(Enumerable.Range(1, 20).Select(n => $"{n}\n"));

    /// <summary>The same twenty lines with line 2 and line 18 replaced.</summary>
    private static readonly string TwentyLinesEdited = string.Concat(
        Enumerable.Range(1, 20).Select(n => n switch
        {
            2 => "TOP\n",
            18 => "BOTTOM\n",
            _ => $"{n}\n",
        }));

    [Fact]
    public async Task One_hunk_can_be_staged_while_the_other_is_left_alone()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "H.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "H.txt", TwentyLinesEdited);

        var patch = await PatchBuilder.ReadAsync(Git, root, "H.txt", DiffSide.Unstaged);
        Assert.Equal(2, patch.Hunks.Count);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "H.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
        });

        Assert.True(applied.Success, applied.Message);

        // The first change is staged; the second is still only on disk.
        var staged = (await Git.TryRunAsync(root, default, "show", ":H.txt")).StandardOutput;
        Assert.Contains("TOP", staged);
        Assert.DoesNotContain("BOTTOM", staged);

        var working = await File.ReadAllTextAsync(Path.Combine(root, "H.txt"));
        Assert.Contains("TOP", working);
        Assert.Contains("BOTTOM", working);
    }

    [Fact]
    public async Task A_staged_hunk_can_be_unstaged_again()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "H.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "H.txt", TwentyLinesEdited);
        await RunAsync(root, "add", "H.txt");

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "H.txt",
            Side = DiffSide.Staged,
            Hunks = [0],
            Reverse = true,
        });

        Assert.True(applied.Success, applied.Message);

        var staged = (await Git.TryRunAsync(root, default, "show", ":H.txt")).StandardOutput;
        Assert.DoesNotContain("TOP", staged);
        Assert.Contains("BOTTOM", staged);

        // Unstaging must not touch the file on disk.
        var working = await File.ReadAllTextAsync(Path.Combine(root, "H.txt"));
        Assert.Contains("TOP", working);
    }

    [Fact]
    public async Task Individual_lines_of_one_hunk_can_be_staged()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "L.txt", "a\nb\nc\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "abc");

        // Two additions inside one hunk. Only the first is wanted.
        await WriteAsync(root, "L.txt", "a\nFIRST\nb\nSECOND\nc\n");

        var patch = await PatchBuilder.ReadAsync(Git, root, "L.txt", DiffSide.Unstaged);
        var hunk = Assert.Single(patch.Hunks);

        var firstAddition = hunk.Lines
            .Select((line, index) => (line, index))
            .Where(x => x.line.StartsWith('+'))
            .Select(x => x.index)
            .ToArray();

        Assert.Equal(2, firstAddition.Length);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "L.txt",
            Side = DiffSide.Unstaged,
            Lines = [new PatchLineSelection { Hunk = 0, Line = firstAddition[0] }],
        });

        Assert.True(applied.Success, applied.Message);

        var staged = (await Git.TryRunAsync(root, default, "show", ":L.txt")).StandardOutput;
        Assert.Contains("FIRST", staged);
        Assert.DoesNotContain("SECOND", staged);
    }

    [Fact]
    public async Task Hunk_staging_works_in_a_repository_that_converts_line_endings()
    {
        // core.autocrlf=true means the working tree holds CRLF and the index holds LF. A
        // patch built from the editor's buffer would not apply; one built from git's own
        // diff does, and must not rewrite the file's endings on the way through.
        var root = await NewRepoAsync(crlf: true);
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "C.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        // Checked out afresh so the working tree really does hold CRLF.
        File.Delete(Path.Combine(root, "C.txt"));
        await RunAsync(root, "checkout", "--", "C.txt");

        var onDisk = await File.ReadAllTextAsync(Path.Combine(root, "C.txt"));
        Assert.Contains("\r\n", onDisk);

        await File.WriteAllTextAsync(
            Path.Combine(root, "C.txt"),
            // Anchored on the preceding newline so "2" does not also match inside "12".
            onDisk.Replace("\n2\r\n", "\nTOP\r\n").Replace("\n18\r\n", "\nBOTTOM\r\n"));

        var patch = await PatchBuilder.ReadAsync(Git, root, "C.txt", DiffSide.Unstaged);
        Assert.Equal(2, patch.Hunks.Count);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "C.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
        });

        Assert.True(applied.Success, applied.Message);

        // The index stores LF, and only the selected hunk landed.
        var stagedBytes = (await Git.RunBytesAsync(root, default, "show", ":C.txt")).StandardOutput;
        var stagedText = System.Text.Encoding.UTF8.GetString(stagedBytes);
        Assert.Contains("TOP", stagedText);
        Assert.DoesNotContain("BOTTOM", stagedText);
        Assert.DoesNotContain("\r\n", stagedText);

        // The working tree keeps its CRLF throughout.
        Assert.Contains("\r\n", await File.ReadAllTextAsync(Path.Combine(root, "C.txt")));
    }

    [Fact]
    public async Task A_hunk_can_be_discarded_from_the_working_tree()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "D.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "D.txt", TwentyLinesEdited);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "D.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
            Reverse = true,
            ApplyToWorkingTree = true,
        });

        Assert.True(applied.Success, applied.Message);

        var working = await File.ReadAllTextAsync(Path.Combine(root, "D.txt"));
        Assert.DoesNotContain("TOP", working);
        Assert.Contains("BOTTOM", working);
    }

    [Fact]
    public async Task A_hunk_selection_is_refused_when_the_file_changed_underneath_it()
    {
        // The race this app exists to care about. The user is shown a diff, an agent
        // rewrites the file, and the click arrives naming "hunk 0" of a diff that no longer
        // exists. Applying it would stage something nobody approved.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "R.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "R.txt", TwentyLinesEdited);

        var shown = await PatchBuilder.ReadAsync(Git, root, "R.txt", DiffSide.Unstaged);
        Assert.Equal(2, shown.Hunks.Count);

        // The agent gets there first.
        await WriteAsync(root, "R.txt", TwentyLinesEdited.Replace("TOP\n", "SOMETHING ELSE ENTIRELY\n"));

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "R.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
            Fingerprint = shown.Fingerprint,
        });

        Assert.False(applied.Success);
        Assert.Contains("changed since", applied.Message);

        // Nothing was staged.
        var staged = (await Git.TryRunAsync(root, default, "diff", "--cached", "--name-only")).Trimmed;
        Assert.Empty(staged);
    }

    [Fact]
    public async Task A_matching_fingerprint_lets_the_selection_through()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "R.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "R.txt", TwentyLinesEdited);

        var shown = await PatchBuilder.ReadAsync(Git, root, "R.txt", DiffSide.Unstaged);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "R.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
            Fingerprint = shown.Fingerprint,
        });

        Assert.True(applied.Success, applied.Message);
    }

    [Fact]
    public async Task A_later_hunk_can_be_staged_while_an_earlier_one_of_a_different_size_is_skipped()
    {
        // Staging out of order, across hunks of different sizes — every other hunk test
        // here replaces one line with one line, where a skipped hunk shifts nothing.
        //
        // Note on what this does *not* prove: the new-side start in the emitted header is
        // not what git matches on. `git apply` locates a hunk by its old-side start, so
        // several different values there all apply correctly to the same place. This test
        // guards the outcome — the right line changed, its neighbours did not — rather than
        // the header arithmetic, which no test can pin down because git ignores it.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "O.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        // Two extra lines near the top, one replacement near the bottom.
        await WriteAsync(root, "O.txt", string.Concat(
            Enumerable.Range(1, 20).Select(n => n switch
            {
                2 => "2\nINSERTED-A\nINSERTED-B\n",
                18 => "BOTTOM\n",
                _ => $"{n}\n",
            })));

        var patch = await PatchBuilder.ReadAsync(Git, root, "O.txt", DiffSide.Unstaged);
        Assert.Equal(2, patch.Hunks.Count);
        Assert.Equal(2, patch.Hunks[0].AddedLines);
        Assert.Equal(0, patch.Hunks[0].RemovedLines);

        // Skip the insertion, stage only the replacement below it.
        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "O.txt",
            Side = DiffSide.Unstaged,
            Hunks = [1],
        });

        Assert.True(applied.Success, applied.Message);

        var staged = (await Git.TryRunAsync(root, default, "show", ":O.txt")).StandardOutput;

        Assert.Contains("BOTTOM", staged);
        Assert.DoesNotContain("INSERTED-A", staged);
        Assert.DoesNotContain("INSERTED-B", staged);

        // And it landed on the right line rather than near it: line 18 replaced, 17 and 19
        // untouched.
        var lines = staged.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("17", lines[16]);
        Assert.Equal("BOTTOM", lines[17]);
        Assert.Equal("19", lines[18]);
    }

    [Fact]
    public async Task A_newly_added_file_can_be_unstaged_one_hunk_at_a_time()
    {
        // Git writes `@@ -0,0 +1,N @@` for a file that does not exist on the old side, and
        // the empty-range form subtracts one from the start — which for zero produced the
        // literal `--1,0` and a patch git rejected as corrupt. That was every unstage of a
        // newly added file.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "brand-new.txt", "alpha\nbeta\ngamma\n");
        await RunAsync(root, "add", "brand-new.txt");

        var patch = await PatchBuilder.ReadAsync(Git, root, "brand-new.txt", DiffSide.Staged);
        var hunk = Assert.Single(patch.Hunks);
        Assert.Equal(0, hunk.OldStart);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "brand-new.txt",
            Side = DiffSide.Staged,
            Hunks = [0],
            Reverse = true,
        });

        Assert.True(applied.Success, applied.Message);

        var staged = (await Git.TryRunAsync(root, default, "diff", "--cached", "--name-only")).Trimmed;
        Assert.Empty(staged);

        // Unstaging never touches the file itself.
        Assert.True(File.Exists(Path.Combine(root, "brand-new.txt")));
    }

    [Fact]
    public async Task Hunk_staging_survives_a_repository_configured_without_diff_prefixes()
    {
        // `diff.noprefix=true` drops the a/ and b/ that `git apply` strips by default, and
        // `diff.mnemonicPrefix` replaces them with i/ and w/. Either turns every hunk
        // operation into "git diff header lacks filename information" — for that one user,
        // because of a config line they set years ago for readability.
        var root = await NewRepoAsync();
        await RunAsync(root, "config", "diff.noprefix", "true");
        await RunAsync(root, "config", "diff.mnemonicPrefix", "true");

        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "P.txt", TwentyLines);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "twenty lines");

        await WriteAsync(root, "P.txt", TwentyLinesEdited);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "P.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
        });

        Assert.True(applied.Success, applied.Message);

        var staged = (await Git.TryRunAsync(root, default, "show", ":P.txt")).StandardOutput;
        Assert.Contains("TOP", staged);
        Assert.DoesNotContain("BOTTOM", staged);
    }

    [Fact]
    public async Task Discarding_everything_works_before_the_first_commit_exists()
    {
        // `restore --source=HEAD` cannot resolve a HEAD that does not exist yet, and exits
        // 128. The empty tree is what HEAD would mean here: every tracked file is an
        // addition against it, so restoring from it removes them.
        var root = await NewRepoAsync(commit: false);
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "staged.txt", "content\n");
        await RunAsync(root, "add", "staged.txt");

        var discarded = await workspace.Staging.DiscardAsync(root, ["staged.txt"], DiscardTarget.Everything);
        Assert.True(discarded.Success, discarded.Message);

        var state = await workspace.GetCommitViewAsync(root);
        Assert.Empty(state.Staged);
    }

    [Fact]
    public async Task An_amend_is_allowed_with_nothing_staged_but_a_plain_commit_is_not()
    {
        // Rewording the last commit's subject is the commonest reason to amend anything,
        // and it stages nothing by definition. Answering it with "Nothing is staged"
        // refuses the one case the button exists for.
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var view = await workspace.GetCommitViewAsync(root);

        Assert.False(view.Readiness.CanCommit);
        Assert.Equal("Nothing is staged.", view.Readiness.Reason);

        Assert.True(view.AmendReadiness.CanCommit);
        Assert.Contains("rewords", view.AmendReadiness.Note!);

        var amended = await workspace.Commits.CommitAsync(root, new CommitRequest
        {
            Message = "reworded subject",
            Amend = true,
        });

        Assert.True(amended.Success, amended.Message);
        Assert.Equal("reworded subject",
            (await Git.TryRunAsync(root, default, "log", "-1", "--format=%s")).Trimmed);

        // Still one commit: a reword replaces, it does not add.
        Assert.Equal("1", (await Git.TryRunAsync(root, default, "rev-list", "--count", "HEAD")).Trimmed);
    }

    [Fact]
    public async Task There_is_nothing_to_amend_before_the_first_commit()
    {
        var root = await NewRepoAsync(commit: false);
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "first.txt", "hello\n");
        await RunAsync(root, "add", "first.txt");

        var view = await workspace.GetCommitViewAsync(root);

        Assert.True(view.Readiness.CanCommit);
        Assert.False(view.AmendReadiness.CanCommit);
        Assert.Contains("no previous commit", view.AmendReadiness.Reason!);
    }

    [Fact]
    public async Task A_binary_file_is_refused_rather_than_patched()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00, 0x10 };
        await File.WriteAllBytesAsync(Path.Combine(root, "blob.bin"), bytes);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "binary");

        await File.WriteAllBytesAsync(Path.Combine(root, "blob.bin"), [.. bytes, 0x7F, 0x00]);

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "blob.bin",
            Side = DiffSide.Unstaged,
            Hunks = [0],
        });

        Assert.False(applied.Success);
        Assert.Contains("binary", applied.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_patch_against_an_unopened_worktree_is_refused()
    {
        var root = await NewRepoAsync();

        // Deliberately not opened: the bridge takes a worktree path as a parameter, so
        // without this check any directory on the machine is a write target.
        var workspace = new WorkspaceService(Git, new OperationLog());

        var applied = await workspace.ApplyPatchAsync(new PatchRequest
        {
            WorktreePath = root,
            Path = "A.txt",
            Side = DiffSide.Unstaged,
            Hunks = [0],
        });

        Assert.False(applied.Success);
        Assert.Contains("not open", applied.Message);
    }

    // -----------------------------------------------------------------------
    // The diff around the index
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_two_index_sides_show_different_comparisons()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root);

        await WriteAsync(root, "A.txt", "one\nSTAGED\nthree\n");
        await workspace.Staging.StageAsync(root, ["A.txt"]);
        await WriteAsync(root, "A.txt", "one\nSTAGED\nWORKTREE\n");

        var staged = await workspace.GetDiffAsync(root, "A.txt", DiffScope.Uncommitted, DiffSide.Staged);
        Assert.Contains("two", staged.BaseText);        // HEAD
        Assert.Contains("STAGED", staged.WorkingText);  // index
        Assert.DoesNotContain("WORKTREE", staged.WorkingText);

        var unstaged = await workspace.GetDiffAsync(root, "A.txt", DiffScope.Uncommitted, DiffSide.Unstaged);
        Assert.Contains("STAGED", unstaged.BaseText);      // index
        Assert.Contains("WORKTREE", unstaged.WorkingText); // working tree
    }
}
