using System.Text;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The write foundations exercised against real repositories.
///
/// Every repository here is created and destroyed by the test. Mutations are never run
/// against the validation repos the read-side tests use: those are real work, and a test
/// that resets one of them is a test that loses it.
/// </summary>
public class WriteFoundationsTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    /// <summary>Creates a throwaway repository with one commit.</summary>
    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-wf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        return root;
    }

    /// <summary>
    /// Fixture setup goes through the write path deliberately: if a plain `git add` cannot
    /// be run this way, every test below is testing the wrong thing.
    /// </summary>
    private static async Task<GitResult> RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
        return result;
    }

    /// <summary>
    /// A workspace with the repository already opened, which is what admits its worktrees
    /// to the set the app will write to.
    /// </summary>
    private static async Task<WorkspaceService> NewWorkspaceAsync(string root, OperationLog log)
    {
        var workspace = new WorkspaceService(Git, log);
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

    [Fact]
    public async Task A_mutation_reports_success_and_lands_in_the_log()
    {
        var root = await NewRepoAsync();
        var log = new OperationLog();
        var workspace = await NewWorkspaceAsync(root, log);

        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "two\n");

        var staged = await workspace.Writer.RunAsync(root, "stage B.txt", default, "add", "B.txt");

        Assert.True(staged.Success, staged.Message);
        Assert.Equal(GitFailure.None, staged.Failure);

        var entry = Assert.Single(log.Recent());
        Assert.Equal("stage B.txt", entry.Operation);
        Assert.Contains("add B.txt", entry.CommandLine);
        Assert.True(entry.Success);
    }

    [Fact]
    public async Task A_failed_mutation_is_classified_rather_than_reported_as_an_exit_code()
    {
        var root = await NewRepoAsync();
        var log = new OperationLog();
        var workspace = await NewWorkspaceAsync(root, log);

        // Nothing staged. Git exits non-zero and says so on stdout, not stderr.
        var mutation = await workspace.Writer.RunAsync(root, "commit", default, "commit", "-m", "nothing");

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.NothingToDo, mutation.Failure);
        Assert.False(string.IsNullOrWhiteSpace(mutation.Message));

        var entry = Assert.Single(log.Recent());
        Assert.Equal("NothingToDo", entry.Failure);
    }

    [Fact]
    public async Task Starting_a_new_operation_is_refused_mid_merge_without_running_git()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        // The state git itself would leave behind after a conflicted merge. Written
        // directly so the test does not depend on how a particular git version words its
        // conflict output.
        var head = (await RunAsync(root, "rev-parse", "HEAD")).Trimmed;
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "MERGE_HEAD"), head + "\n");

        var state = await workspace.GetRepositoryStateAsync(root);
        Assert.Equal(RepositoryOperation.Merge, state.Operation);
        Assert.False(state.CanWrite(WriteKind.StartsOperation).Allowed);

        var mutation = await workspace.Writer.RunAsync(
            root, "check out feature", WriteKind.StartsOperation, default, "checkout", "-b", "feature");

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.OperationInProgress, mutation.Failure);

        // Attempts of zero is the claim that git never ran. That is the whole point of the
        // guard: the user hears "you are mid-merge" instead of git's version, later.
        Assert.Equal(0, mutation.Attempts);
    }

    [Fact]
    public async Task Staging_a_resolved_file_is_allowed_mid_merge()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        await CreateConflictedMergeAsync(root);

        // Resolving the conflict by hand, then staging it — which is how a merge is
        // finished. A guard that blocks every write while an operation is in progress
        // blocks exactly this, leaving the conflict impossible to resolve through the app.
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "resolved\n");

        var staged = await workspace.Writer.RunAsync(root, "stage A.txt", default, "add", "A.txt");

        Assert.True(staged.Success, staged.Message);
        Assert.False((await workspace.GetRepositoryStateAsync(root)).HasConflicts);
    }

    [Fact]
    public async Task The_command_that_ends_an_operation_is_not_blocked_by_the_guard()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        await CreateConflictedMergeAsync(root);
        Assert.Equal(RepositoryOperation.Merge, (await workspace.GetRepositoryStateAsync(root)).Operation);

        // The guard exists to notice exactly the state `merge --abort` is there to clear,
        // so blocking it would leave no way out of a merge.
        var abort = await workspace.Writer.RunAsync(
            root, "abort merge", WriteKind.ResolvesOperation, default, "merge", "--abort");

        Assert.True(abort.Success, abort.Message);
        Assert.Equal(RepositoryOperation.None, (await workspace.GetRepositoryStateAsync(root)).Operation);
    }

    [Fact]
    public async Task A_state_that_could_not_be_read_blocks_starting_an_operation()
    {
        // A guard that fails open is not a guard: "could not ask git" must not be
        // indistinguishable from "nothing is going on".
        var notARepo = Path.Combine(Path.GetTempPath(), "chapter-wf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(notARepo);
        _created.Add(notARepo);

        var state = await new WorkspaceService(Git, new OperationLog()).GetRepositoryStateAsync(notARepo);

        Assert.True(state.ProbeFailed);
        Assert.False(state.CanWrite(WriteKind.StartsOperation).Allowed);

        // Working-tree writes stay allowed — they are legal in every state, so refusing
        // them on a failed probe would be caution with no safety behind it.
        Assert.True(state.CanWrite(WriteKind.WorkingTree).Allowed);
    }

    /// <summary>
    /// Leaves the repository stopped on a conflicted merge of A.txt, which is the state
    /// several tests below need and none of them should be building by hand.
    /// </summary>
    private static async Task CreateConflictedMergeAsync(string root)
    {
        await RunAsync(root, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "feature\n");
        await RunAsync(root, "commit", "-am", "feature edit");

        await RunAsync(root, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "main\n");
        await RunAsync(root, "commit", "-am", "main edit");

        // Expected to fail: the conflict is the point.
        var merge = await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "feature");
        Assert.False(merge.Success, "the fixture merge was supposed to conflict");
    }

    [Fact]
    public async Task Undo_takes_back_a_commit_and_leaves_the_working_tree_alone()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var before = await workspace.Undo.CaptureHeadAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "two\n");
        await workspace.Writer.RunAsync(root, "stage", default, "add", "B.txt");
        var commit = await workspace.Writer.RunAsync(root, "commit", default, "commit", "-m", "add B");
        Assert.True(commit.Success, commit.Message);

        await workspace.Undo.RecordCommitAsync(root, before, "add B");
        Assert.Equal("commit \"add B\"", workspace.Undo.Peek(root)?.Label);

        var undo = await workspace.Undo.UndoAsync(root);
        Assert.True(undo.Success, undo.Message);

        // HEAD is back.
        Assert.Equal(before, await workspace.Undo.CaptureHeadAsync(root));

        // And the work is not lost: reset --soft leaves the file staged exactly as it was
        // the instant before the commit. An undo that discarded it would be a trap.
        Assert.True(File.Exists(Path.Combine(root, "B.txt")));

        var staged = await Git.TryRunAsync(root, default, "diff", "--cached", "--name-only");
        Assert.Contains("B.txt", staged.StandardOutput);

        // The stack is empty now, so a second undo has nothing to take back.
        Assert.Null(workspace.Undo.Peek(root));
    }

    [Fact]
    public async Task Undo_refuses_when_something_else_has_committed_since()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var before = await workspace.Undo.CaptureHeadAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "two\n");
        await RunAsync(root, "add", "B.txt");
        await RunAsync(root, "commit", "-m", "add B");
        await workspace.Undo.RecordCommitAsync(root, before, "add B");

        // The agent working in this worktree commits between the app's mutation and the
        // user pressing undo. Resetting to the recorded sha would throw that away silently.
        await File.WriteAllTextAsync(Path.Combine(root, "C.txt"), "three\n");
        await RunAsync(root, "add", "C.txt");
        await RunAsync(root, "commit", "-m", "agent commit");
        var agentHead = (await RunAsync(root, "rev-parse", "HEAD")).Trimmed;

        var undo = await workspace.Undo.UndoAsync(root);

        Assert.False(undo.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, undo.Failure);
        Assert.Equal(agentHead, (await RunAsync(root, "rev-parse", "HEAD")).Trimmed);

        // The undo point stays put, so it is still there once the user has looked at what
        // the agent did.
        Assert.NotNull(workspace.Undo.Peek(root));
    }

    [Fact]
    public async Task The_reflog_is_readable_even_when_the_undo_stack_is_empty()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var entries = await workspace.Undo.ReadReflogAsync(root);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Subject.Contains("initial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conflicted_merge_is_reported_with_the_paths_that_conflict()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        await CreateConflictedMergeAsync(root);

        var state = await workspace.GetRepositoryStateAsync(root);

        Assert.Equal(RepositoryOperation.Merge, state.Operation);
        Assert.True(state.HasConflicts);
        Assert.Equal(["A.txt"], state.ConflictedPaths);
        Assert.Equal("main", state.Branch);
        Assert.False(state.CanWrite(WriteKind.StartsOperation).Allowed);

        // And the file list has to say so too, or the one file that matters looks ordinary.
        var changes = await workspace.GetChangesAsync(
            new Worktree { Path = root }, DiffScope.Uncommitted);

        Assert.Contains(changes.Files, f => f.Path == "A.txt" && f.IsConflicted);
    }

    /// <summary>
    /// Asks git what it sees in its own environment, through an alias that shells out.
    /// There is no other way to observe the child's environment from here, and the child's
    /// environment is the entire subject of the test below.
    ///
    /// The tests using this mutate the process environment, which xUnit shares across test
    /// classes running in parallel. It is safe only because the values they set are the
    /// ones the read path sets anyway, and they restore in a finally. Anything set here
    /// that the code does not already set for reads would need the parallelism disabling.
    /// </summary>
    private static async Task<string> ReadChildEnvironmentAsync(string root, GitIntent intent, string variable)
    {
        var result = await Git.ExecuteAsync(
            root, intent, default,
            "-c", $"alias.chapterenv=!echo [${variable}]", "chapterenv");

        return result.Trimmed;
    }

    [Fact]
    public async Task The_write_environment_does_not_inherit_lock_suppression_from_the_parent()
    {
        var root = await NewRepoAsync();

        // ProcessStartInfo.Environment starts as a copy of this process's environment, so
        // "we never set it for writes" is not the same as "it is unset for writes". Agent
        // harnesses and other git GUIs export exactly this variable, and inheriting it puts
        // every mutation back under the restriction the read/write split exists to lift —
        // silently, and only for the users who launch the app that way.
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");

        try
        {
            Assert.Equal("[0]", await ReadChildEnvironmentAsync(root, GitIntent.Read, "GIT_OPTIONAL_LOCKS"));
            Assert.Equal("[]", await ReadChildEnvironmentAsync(root, GitIntent.Write, "GIT_OPTIONAL_LOCKS"));

            // The same defect on the credential side: Phase 5 lifts the prompt ban by
            // removing this, and removing something that was never set would have been a
            // no-op against an inherited value.
            var git = new GitCli { AllowCredentialPrompts = true };
            var permitted = await git.ExecuteAsync(
                root, GitIntent.Network, default,
                "-c", "alias.chapterenv=!echo [$GCM_INTERACTIVE]", "chapterenv");

            Assert.Equal("[]", permitted.Trimmed);
            Assert.Equal("[never]", await ReadChildEnvironmentAsync(root, GitIntent.Write, "GCM_INTERACTIVE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", null);
            Environment.SetEnvironmentVariable("GCM_INTERACTIVE", null);
        }
    }

    [Fact]
    public async Task The_reflog_timestamp_is_when_head_moved_not_when_the_commit_was_made()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        // A commit made long ago, then a checkout of it made just now. %cI — the obvious
        // choice, and the wrong one — reports the committer date of the commit an entry
        // points at, so this checkout would be dated 2020.
        Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "2020-01-01T00:00:00+00:00");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", "2020-01-01T00:00:00+00:00");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "two\n");
            await RunAsync(root, "add", "-A");
            await RunAsync(root, "commit", "-m", "an old commit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", null);
            Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", null);
        }

        await RunAsync(root, "checkout", "--detach", "HEAD");

        var entries = await workspace.Undo.ReadReflogAsync(root);
        var checkout = entries[0];

        Assert.Contains("checkout", checkout.Subject, StringComparison.Ordinal);
        Assert.NotNull(checkout.Timestamp);
        Assert.True(
            checkout.Timestamp!.Value.Year > 2020,
            $"the checkout happened now, but the reflog reports {checkout.Timestamp}");

        // And the selector still has to be the form git accepts back as a revision.
        Assert.Equal("HEAD@{0}", checkout.Selector);
        Assert.Equal("HEAD@{1}", entries[1].Selector);
    }

    [Fact]
    public async Task Undo_refuses_when_it_cannot_verify_where_head_was()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var before = await workspace.Undo.CaptureHeadAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "two\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "add B");

        // What a probe failure at record time leaves behind: an undo point with nothing to
        // check the current HEAD against. Skipping the check there — the tempting reading
        // of "no expectation recorded" — turns the guard that protects the agent's work
        // into one that silently is not there.
        workspace.Undo.Record(new UndoPoint
        {
            Id = "unverifiable",
            Label = "commit \"add B\"",
            WorktreePath = root,
            Timestamp = DateTimeOffset.Now,
            InverseCommand = ["reset", "--soft", before!],
            HeadSha = before,
            ExpectedHeadSha = null,
        });

        var undo = await workspace.Undo.UndoAsync(root);

        Assert.False(undo.Success);
        Assert.Contains("never recorded", undo.Message, StringComparison.Ordinal);

        // And nothing moved.
        Assert.NotEqual(before, await workspace.Undo.CaptureHeadAsync(root));
    }

    [Fact]
    public async Task A_save_will_not_write_into_the_git_directory()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        // Staying inside the worktree is not enough: .git is inside it, and a hook there is
        // arbitrary code the next time anyone runs git in this repository.
        var hook = await workspace.SaveFileAsync(root, ".git/hooks/pre-commit", "#!/bin/sh\necho pwned\n");

        Assert.False(hook.Success);
        Assert.False(File.Exists(Path.Combine(root, ".git", "hooks", "pre-commit")));

        var config = await workspace.SaveFileAsync(root, ".git/config", "[core]\n\tpager = calc.exe\n");

        Assert.False(config.Success);
        Assert.DoesNotContain("calc.exe", await File.ReadAllTextAsync(Path.Combine(root, ".git", "config")));
    }

    [Fact]
    public async Task A_save_will_not_write_to_a_worktree_the_app_never_opened()
    {
        var root = await NewRepoAsync();

        // No GetWorktreesAsync, so nothing has admitted this path. Without the check, the
        // worktree parameter makes any directory on the machine a write root.
        var workspace = new WorkspaceService(Git, new OperationLog());

        var result = await workspace.SaveFileAsync(root, "A.txt", "changed\n");

        Assert.False(result.Success);
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
    }

    [Fact]
    public async Task A_save_will_not_replace_a_binary_file_with_text()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x00, 0x1A];
        await File.WriteAllBytesAsync(Path.Combine(root, "logo.png"), png);

        var result = await workspace.SaveFileAsync(root, "logo.png", "whatever was in the editor");

        Assert.False(result.Success);
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(root, "logo.png")));
    }

    [Fact]
    public async Task A_save_will_not_resurrect_a_file_that_is_not_there()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        // A file an agent deleted reads back as empty and editable-looking. Saving the
        // untouched buffer would recreate it as a zero-byte file and report success.
        var result = await workspace.SaveFileAsync(root, "deleted-by-the-agent.cs", "");

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(root, "deleted-by-the-agent.cs")));
    }

    [Fact]
    public async Task A_save_will_not_reformat_a_file_it_cannot_reproduce()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        // Latin-1: decodes with U+FFFD substitutions, so writing the text back would put
        // EF BF BD over a byte the user never touched.
        byte[] latin1 = [(byte)'/', (byte)'/', (byte)' ', 0xE9, (byte)'\n'];
        await File.WriteAllBytesAsync(Path.Combine(root, "legacy.cs"), latin1);

        var result = await workspace.SaveFileAsync(root, "legacy.cs", "// e\n");

        Assert.False(result.Success);
        Assert.Equal(latin1, await File.ReadAllBytesAsync(Path.Combine(root, "legacy.cs")));

        // The read side has to agree, or the editor offers a save the writer will refuse.
        var payload = await workspace.GetFileContentAsync(root, "legacy.cs", DiffScope.Uncommitted);
        Assert.False(payload.IsEditable);
    }

    [Fact]
    public async Task An_ordinary_edit_still_saves()
    {
        var root = await NewRepoAsync();
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var payload = await workspace.GetFileContentAsync(root, "A.txt", DiffScope.Uncommitted);
        Assert.True(payload.IsEditable);

        var result = await workspace.SaveFileAsync(root, "A.txt", "one\ntwo\n");

        Assert.True(result.Success, result.Error);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
        Assert.Empty(Directory.GetFiles(root, "*" + WorkingTreeWriter.TempSuffix));
    }

    [Fact]
    public async Task Repository_state_survives_a_repository_with_no_commits()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-wf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        var workspace = await NewWorkspaceAsync(root, new OperationLog());

        var state = await workspace.GetRepositoryStateAsync(root);

        Assert.Equal("main", state.Branch);
        Assert.True(state.IsUnborn);
        Assert.False(state.IsDetached);
        Assert.True(state.CanWrite(WriteKind.StartsOperation).Allowed);
    }
}

/// <summary>
/// Saving a file back to the working tree. No repository is involved — the writer's whole
/// job is to reproduce the bytes it was given without git's help.
/// </summary>
public class SavePathTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "chapter-save-" + Guid.NewGuid().ToString("N")[..8]);

    public SavePathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Preserves_crlf_line_endings_through_an_edit()
    {
        var path = Path.Combine(_root, "windows.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));

        // Monaco hands back LF, because that is what it normalises to on load. Writing
        // that verbatim would rewrite every line of the file.
        var result = await WorkingTreeWriter.SaveAsync(_root, "windows.txt", "one\r\ntwo\r\nthree\n");

        Assert.True(result.Success, result.Error);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Preserves_a_utf8_byte_order_mark()
    {
        var path = Path.Combine(_root, "bom.txt");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello\n")).ToArray();
        await File.WriteAllBytesAsync(path, original);

        var result = await WorkingTreeWriter.SaveAsync(_root, "bom.txt", "hello there\n");

        Assert.True(result.Success, result.Error);

        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, written.Take(3));
        Assert.Equal(FileEncoding.Utf8Bom, result.Format?.Encoding);
    }

    [Fact]
    public async Task A_new_file_gets_utf8_and_lf_rather_than_the_platform_default()
    {
        // This is a git repository, and CRLF is the choice that needs evidence.
        var result = await WorkingTreeWriter.SaveAsync(_root, "fresh.txt", "line\n");

        Assert.True(result.Success, result.Error);
        Assert.Equal(new byte[] { (byte)'l', (byte)'i', (byte)'n', (byte)'e', (byte)'\n' },
            await File.ReadAllBytesAsync(Path.Combine(_root, "fresh.txt")));
    }

    [Fact]
    public async Task Refuses_a_path_that_escapes_the_worktree()
    {
        // A path arriving over the bridge is not trusted to stay inside the worktree.
        var result = await WorkingTreeWriter.SaveAsync(_root, "../escaped.txt", "nope");

        Assert.False(result.Success);
        Assert.Contains("outside the worktree", result.Error);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
    }

    [Fact]
    public async Task Leaves_no_temporary_file_behind_on_success()
    {
        await WorkingTreeWriter.SaveAsync(_root, "clean.txt", "content\n");

        Assert.Empty(Directory.GetFiles(_root, "*.chapter-tmp"));
    }

    [Fact]
    public async Task Creates_missing_directories_for_a_new_file()
    {
        var result = await WorkingTreeWriter.SaveAsync(_root, "nested/deeper/file.txt", "content\n");

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "nested", "deeper", "file.txt")));
    }
}
