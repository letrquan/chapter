using System.Text;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The pure half of the write foundations: everything that turns git's output into
/// something the UI can act on, tested without touching a repository.
/// </summary>
public class FailureClassificationTests
{
    [Theory]
    [InlineData("https://user:secret@example.com/repo.git", "https://***@example.com/repo.git")]
    [InlineData("git:secret@example.com:team/repo.git", "***@example.com:team/repo.git")]
    [InlineData("https://user:p@ss@example.com/repo.git", "https://***@example.com/repo.git")]
    public void Redacts_embedded_credentials_from_git_text(string input, string expected) =>
        Assert.Equal(expected, GitCli.RedactText(input));

    [Fact]
    public void Leaves_email_addresses_and_urls_without_userinfo_alone()
    {
        const string text = "contact test@example.com at https://example.com/repo.git";

        Assert.Equal(text, GitCli.RedactText(text));
    }

    [Theory]
    [InlineData("fatal: Unable to create '/repo/.git/index.lock': File exists.", GitFailure.Locked)]
    [InlineData("Another git process seems to be running in this repository", GitFailure.Locked)]
    [InlineData("error: cannot lock ref 'refs/heads/main'", GitFailure.Locked)]
    [InlineData("fatal: Authentication failed for 'https://github.com/x/y.git/'", GitFailure.AuthenticationRequired)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled",
        GitFailure.AuthenticationRequired)]
    [InlineData("CONFLICT (content): Merge conflict in src/Program.cs", GitFailure.Conflict)]
    [InlineData("fatal: You have not concluded your merge (MERGE_HEAD exists).", GitFailure.OperationInProgress)]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:",
        GitFailure.WouldLoseChanges)]
    [InlineData("nothing to commit, working tree clean", GitFailure.NothingToDo)]
    [InlineData("error: failed to push some refs to 'origin'", GitFailure.Rejected)]
    [InlineData("error: pathspec 'nope.cs' did not match any file(s) known to git", GitFailure.NotFound)]
    // A missing executable is a named absence; the wrapper is what makes it safe to match.
    [InlineData("Failed to start 'gh': The system cannot find the file specified",
        GitFailure.NotFound)]
    [InlineData("Failed to start 'git': No such file or directory", GitFailure.NotFound)]
    // And an ordinary working-tree failure that merely ends in the same words is not one.
    // Matching them loosely offered "git could not find what was named" for a checkout that
    // half-ran, which points the user at the wrong recovery.
    [InlineData("error: unable to unlink old 'src/x.cs': No such file or directory",
        GitFailure.Unknown)]
    [InlineData("fatal: something nobody has seen before", GitFailure.Unknown)]
    public void Classifies_the_failures_the_ui_has_to_act_on(string stderr, GitFailure expected) =>
        Assert.Equal(expected, GitFailureClassifier.Classify(stderr));

    [Fact]
    public void A_failure_with_no_output_is_unknown_rather_than_no_failure()
    {
        // The classifier is only ever consulted on a non-zero exit, so silence means "git
        // failed and did not say why" — which is not the same as "git did not fail", and
        // reporting None here would let a silent failure through as a success.
        Assert.Equal(GitFailure.Unknown, GitFailureClassifier.Classify(""));

        // None belongs to the mutation, which gets it by never asking.
        var succeeded = new GitMutation
        {
            Operation = "commit",
            WorktreePath = @"C:\repo",
            CommandLine = "git commit -m x",
            ExitCode = 0,
        };

        Assert.Equal(GitFailure.None, succeeded.Failure);
    }

    [Theory]
    [InlineData("error: unable to create file Cargo.lock: Permission denied")]
    [InlineData("error: unable to create file yarn.lock: Permission denied")]
    [InlineData("error: unable to create file packages.lock.json: Permission denied")]
    public void A_lockfile_from_another_ecosystem_is_not_index_lock_contention(string stderr)
    {
        // Matching "unable to create" near ".lock" anywhere in the output also matches a
        // checkout failing over some other tool's lockfile — neither transient nor anything
        // to do with git's index. Classifying it as contention retries a partially applied
        // checkout five times and then replaces the real cause with a wrong one, because
        // Message prefers the app's Detail over git's stderr.
        Assert.NotEqual(GitFailure.Locked, GitFailureClassifier.Classify(stderr));
    }

    [Fact]
    public void Lock_contention_outranks_a_co_occurring_message()
    {
        // Git prints its lock advice alongside whatever else went wrong. Only the lock is
        // worth retrying, so it has to win.
        const string stderr = """
            fatal: Unable to create '/repo/.git/index.lock': File exists.

            Another git process seems to be running in this repository, e.g.
            an editor opened by 'git commit'.
            """;

        Assert.Equal(GitFailure.Locked, GitFailureClassifier.Classify(stderr));
    }
}

public class MutationMessageTests
{
    private static GitMutation Failed(string stderr = "", string stdout = "", string? detail = null) => new()
    {
        Operation = "commit",
        WorktreePath = @"C:\repo",
        CommandLine = "git commit -m x",
        ExitCode = 1,
        StandardError = stderr,
        StandardOutput = stdout,
        Detail = detail,
    };

    [Fact]
    public void Prefers_what_the_app_deduced_over_what_git_said()
    {
        var mutation = Failed(
            stderr: "fatal: Unable to create '/repo/.git/index.lock': File exists.",
            detail: "Could not commit: another process is using this repository — index.lock held by git (pid 42)");

        Assert.Contains("pid 42", mutation.Message);
    }

    [Fact]
    public void Strips_gits_severity_prefix()
    {
        Assert.Equal("something went wrong", Failed(stderr: "fatal: something went wrong").Message);
    }

    [Fact]
    public void Skips_hint_lines_in_favour_of_the_real_error()
    {
        // Git leads with advice more often than with the cause, and the advice is useless
        // without it.
        const string stderr = """
            hint: Updates were rejected because the tip of your current branch is behind
            error: failed to push some refs to 'origin'
            """;

        Assert.Equal("failed to push some refs to 'origin'", Failed(stderr: stderr).Message);
    }

    [Fact]
    public void Falls_back_to_stdout_when_stderr_is_empty()
    {
        // `git commit` reports "nothing to commit" on stdout, not stderr.
        Assert.Equal("nothing to commit, working tree clean",
            Failed(stdout: "nothing to commit, working tree clean").Message);
    }

    [Fact]
    public void Never_returns_an_empty_message()
    {
        // A failure with no explanation is the one thing worse than a wrong one.
        var message = Failed().Message;

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("commit", message);
    }
}

public class LockPathTests
{
    [Fact]
    public void Extracts_the_lock_path_from_gits_own_message()
    {
        const string stderr = "fatal: Unable to create 'D:/repo/.git/index.lock': File exists.";

        var path = GitLock.PathFromStderr(stderr);

        Assert.NotNull(path);
        Assert.EndsWith("index.lock", path);
    }

    [Fact]
    public void Returns_null_when_the_message_names_no_lock()
    {
        Assert.Null(GitLock.PathFromStderr("fatal: not a git repository"));
    }

    [Fact]
    public void Describes_a_lock_file_that_is_not_there_as_released()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chapter-no-such-" + Guid.NewGuid().ToString("N") + ".lock");

        var info = GitLock.Describe(missing);

        Assert.False(info.Exists);
        Assert.False(info.LooksStale);
        Assert.Contains("released", info.Summary);
    }
}

public class RepositoryOperationDetectionTests
{
    private static string NewGitDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "chapter-gitdir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Reports_none_for_a_quiet_repository()
    {
        var dir = NewGitDir();
        try
        {
            Assert.Equal(RepositoryOperation.None, RepositoryStateReader.DetectOperation(dir).Operation);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Detects_a_conflicted_merge()
    {
        var dir = NewGitDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "MERGE_HEAD"), "abc123\n");

            Assert.Equal(RepositoryOperation.Merge, RepositoryStateReader.DetectOperation(dir).Operation);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rebase_outranks_the_cherry_pick_marker_it_sets()
    {
        // A rebase runs through the sequencer, so a stopped one has CHERRY_PICK_HEAD set as
        // well as its own directory. Checking cherry-pick first would report every stopped
        // rebase as one, and offer `cherry-pick --continue` for something that needs
        // `rebase --continue`.
        var dir = NewGitDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "rebase-merge"));
            File.WriteAllText(Path.Combine(dir, "rebase-merge", "interactive"), "");
            File.WriteAllText(Path.Combine(dir, "rebase-merge", "msgnum"), "3\n");
            File.WriteAllText(Path.Combine(dir, "rebase-merge", "end"), "7\n");
            File.WriteAllText(Path.Combine(dir, "CHERRY_PICK_HEAD"), "abc123\n");

            var (operation, step, total) = RepositoryStateReader.DetectOperation(dir);

            Assert.Equal(RepositoryOperation.RebaseInteractive, operation);
            Assert.Equal(3, step);
            Assert.Equal(7, total);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tells_an_am_session_apart_from_an_apply_backend_rebase()
    {
        // Both live in rebase-apply; only the marker says which, and they need different
        // continue commands.
        var dir = NewGitDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "rebase-apply"));
            File.WriteAllText(Path.Combine(dir, "rebase-apply", "applying"), "");

            Assert.Equal(RepositoryOperation.ApplyMailbox, RepositoryStateReader.DetectOperation(dir).Operation);

            File.WriteAllText(Path.Combine(dir, "rebase-apply", "rebasing"), "");

            Assert.Equal(RepositoryOperation.Rebase, RepositoryStateReader.DetectOperation(dir).Operation);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_in_progress_operation_blocks_starting_another_one_and_nothing_else()
    {
        var midRebase = new RepositoryState
        {
            WorktreePath = @"C:\repo",
            Operation = RepositoryOperation.Rebase,
        };

        Assert.False(midRebase.CanWrite(WriteKind.StartsOperation).Allowed);

        // Staging and committing stay legal. During a merge they are how it gets finished,
        // and a guard that blocks them makes the conflict unresolvable through the app.
        Assert.True(midRebase.CanWrite(WriteKind.WorkingTree).Allowed);

        // Nothing may block the way out of a state.
        Assert.True(midRebase.CanWrite(WriteKind.ResolvesOperation).Allowed);
    }

    [Fact]
    public void Unresolved_conflicts_block_starting_an_operation_but_not_resolving_them()
    {
        var conflicted = new RepositoryState
        {
            WorktreePath = @"C:\repo",
            ConflictedPaths = ["src/A.cs"],
        };

        Assert.False(conflicted.CanWrite(WriteKind.StartsOperation).Allowed);

        // `git add src/A.cs` is the command that clears this state. Refusing it is
        // refusing the only exit.
        Assert.True(conflicted.CanWrite(WriteKind.WorkingTree).Allowed);
    }

    [Fact]
    public void A_state_that_could_not_be_read_is_not_treated_as_a_clean_one()
    {
        // Failing open here means a mutation runs in a repository that really is mid-rebase
        // because a transient status failure made it look idle.
        var unknown = new RepositoryState { WorktreePath = @"C:\repo", ProbeFailed = true };

        Assert.False(unknown.CanWrite(WriteKind.StartsOperation).Allowed);
        Assert.True(unknown.CanWrite(WriteKind.ResolvesOperation).Allowed);
    }

    [Fact]
    public void Bisect_does_not_block_anything()
    {
        // A navigation state, not a blocked one: committing during one is unusual but
        // legal, and refusing would be the app inventing a rule git does not have.
        var bisecting = new RepositoryState
        {
            WorktreePath = @"C:\repo",
            Operation = RepositoryOperation.Bisect,
        };

        Assert.True(bisecting.CanWrite(WriteKind.StartsOperation).Allowed);
    }
}

public class UnmergedEntryParsingTests
{
    [Fact]
    public void Unmerged_entries_are_reported_rather_than_skipped()
    {
        // "u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>" — three stage hashes
        // make it longer than an ordinary entry, and the path sits after ten fields.
        // These used to be dropped, which left a conflicted file invisible in exactly the
        // state where it is the only file that matters.
        var output = "u UU N... 100644 100644 100644 100644 h1 h2 h3 src/Conflicted.cs\0"
                   + "1 .M N... 100644 100644 100644 abc abc src/Ordinary.cs\0";

        var state = DiffService.ParseWorkingState(output);

        Assert.Equal(["src/Conflicted.cs"], state.Unmerged);

        // A conflicted file is also uncommitted, or the branch view would show it as clean.
        Assert.Contains("src/Conflicted.cs", state.Dirty);
        Assert.Contains("src/Ordinary.cs", state.Dirty);
    }

    [Fact]
    public void Unmerged_paths_containing_spaces_survive_field_splitting()
    {
        var output = "u UU N... 100644 100644 100644 100644 h1 h2 h3 src/My Folder/A File.cs\0";

        Assert.Equal(["src/My Folder/A File.cs"], DiffService.ParseWorkingState(output).Unmerged);
    }

    [Fact]
    public void The_apps_own_scratch_file_is_not_reported_as_an_untracked_file()
    {
        // The atomic write puts it beside its target, inside the tracked worktree, for the
        // moment between write and rename. Listing it shows a phantom untracked file — and
        // invites an agent's `git add -A` to commit it.
        var output = $"? src/Program.cs{WorkingTreeWriter.TempSuffix}\0"
                   + "? src/Real.cs\0";

        Assert.Equal(["src/Real.cs"], DiffService.ParseWorkingState(output).Untracked);
    }
}

public class TextFormatTests
{
    [Theory]
    [InlineData("a\nb\n", LineEnding.Lf)]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf)]
    [InlineData("a\r\nb\n", LineEnding.Mixed)]
    [InlineData("no newline at all", LineEnding.Lf)]
    public void Detects_the_files_newline(string text, LineEnding expected) =>
        Assert.Equal(expected, FileContent.DetectLineEnding(text));

    [Fact]
    public void Converting_to_crlf_does_not_double_the_carriage_returns()
    {
        // Replacing "\n" with "\r\n" without normalising first turns "\r\n" into "\r\r\n",
        // which is what makes a naive save corrupt every line of a Windows file.
        var format = new TextFormat(FileEncoding.Utf8, LineEnding.CrLf);

        Assert.Equal("a\r\nb\r\n", format.ApplyLineEndings("a\r\nb\n"));
    }

    [Fact]
    public void Mixed_line_endings_are_left_exactly_as_they_arrived()
    {
        // A file that already disagrees with itself has no convention to preserve, and
        // picking one for it would rewrite half its lines.
        var format = new TextFormat(FileEncoding.Utf8, LineEnding.Mixed);

        Assert.Equal("a\r\nb\nc", format.ApplyLineEndings("a\r\nb\nc"));
    }

    [Fact]
    public void Round_trips_a_utf8_bom()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello\n")).ToArray();

        var content = FileContent.FromBytes(original);
        Assert.Equal(FileEncoding.Utf8Bom, content.Format.Encoding);
        Assert.Equal("hello\n", content.Text);

        // GetBytes alone would silently drop the mark, and git would report the first line
        // of an unedited file as changed.
        Assert.Equal(original, content.Format.Encode(content.Text));
    }

    [Fact]
    public void A_file_that_is_not_valid_utf8_is_marked_unable_to_round_trip()
    {
        // Latin-1 source: 0xE9 is é, and an invalid UTF-8 sequence. The decoder replaces it
        // with U+FFFD, and encoding that back writes EF BF BD over a byte the user never
        // touched — so the file has to be shown and refused for editing, not silently
        // corrupted on save.
        byte[] latin1 = [(byte)'/', (byte)'/', (byte)' ', 0xE9, (byte)'\n'];

        var content = FileContent.FromBytes(latin1);

        Assert.False(content.IsBinary);
        Assert.False(content.CanRoundTrip);
        Assert.NotEqual(latin1, content.Format.Encode(content.Text));
    }

    [Fact]
    public void Valid_utf8_round_trips_and_says_so()
    {
        var bytes = Encoding.UTF8.GetBytes("// é\n");

        var content = FileContent.FromBytes(bytes);

        Assert.True(content.CanRoundTrip);
        Assert.Equal(bytes, content.Format.Encode(content.Text));
    }

    [Fact]
    public void Mixed_line_endings_cannot_round_trip_through_an_editor()
    {
        // The editor normalises the model to one EOL, so whatever comes back has lost the
        // distinction. Leaving the text alone on save — the "we don't know, so don't
        // touch it" branch — is then exactly what rewrites every line that disagreed.
        var content = FileContent.FromBytes(Encoding.UTF8.GetBytes("a\r\nb\nc\r\n"));

        Assert.Equal(LineEnding.Mixed, content.Format.LineEnding);
        Assert.False(content.CanRoundTrip);
    }

    [Fact]
    public void Round_trips_utf16_without_mistaking_it_for_binary()
    {
        // UTF-16 text is full of NUL bytes, so a binary probe running ahead of the BOM
        // check classifies every UTF-16 source file as binary.
        var original = new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetBytes("class A { }\n");
        var withBom = new byte[] { 0xFF, 0xFE }.Concat(original).ToArray();

        var content = FileContent.FromBytes(withBom);

        Assert.False(content.IsBinary);
        Assert.Equal(FileEncoding.Utf16Le, content.Format.Encoding);
        Assert.Equal("class A { }\n", content.Text);
    }
}

public class ReflogParsingTests
{
    private static string Line(params string[] fields) => string.Join(UndoService.FieldSeparator, fields);

    [Fact]
    public void Parses_entries_whose_subject_contains_the_obvious_separators()
    {
        // Reflog subjects are "commit: <message>", and messages contain colons, spaces and
        // quotes constantly. Splitting on any of those would truncate them.
        //
        // The selector field arrives date-formatted because --date=iso-strict is the only
        // way to get the time the entry was written; the date replaces the ordinal rather
        // than accompanying it.
        var output = string.Join('\n',
        [
            Line("aaaa1111", "HEAD@{2026-08-14T10:00:00+01:00}", "commit: fix: don't split on ':' here"),
            Line("bbbb2222", "HEAD@{2026-08-14T09:00:00+01:00}", "rebase (finish): returning to refs/heads/main"),
        ]);

        var entries = UndoService.ParseReflog(output);

        Assert.Equal(2, entries.Count);
        Assert.Equal("commit: fix: don't split on ':' here", entries[0].Subject);
        Assert.Equal("aaaa111", entries[0].ShortSha);

        // The ordinal form is what git accepts back as a revision, so it is reconstructed
        // from position rather than read from a field that no longer carries it.
        Assert.Equal("HEAD@{0}", entries[0].Selector);
        Assert.Equal("HEAD@{1}", entries[1].Selector);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.FromHours(1)),
            entries[0].Timestamp);
    }

    [Fact]
    public void Skips_malformed_lines_rather_than_throwing()
    {
        var output = "not a reflog line\n" + Line("aaaa1111", "HEAD@{2026-08-14T10:00:00+01:00}", "commit: real");

        Assert.Single(UndoService.ParseReflog(output));
    }

    [Fact]
    public void An_unparseable_selector_leaves_the_timestamp_null_rather_than_failing()
    {
        var entries = UndoService.ParseReflog(Line("aaaa1111", "HEAD@{0}", "commit: real"));

        Assert.Null(Assert.Single(entries).Timestamp);
    }
}

public class OperationLogTests
{
    private static OperationLogEntry Entry(string operation) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Operation = operation,
        WorktreePath = @"C:\repo",
        CommandLine = $"git {operation}",
        ExitCode = 0,
    };

    [Fact]
    public void Returns_the_most_recent_first()
    {
        var log = new OperationLog();
        log.Append(Entry("first"));
        log.Append(Entry("second"));

        var recent = log.Recent();

        Assert.Equal("second", recent[0].Operation);
        Assert.Equal("first", recent[1].Operation);
    }

    [Fact]
    public void Keeps_at_most_its_stated_maximum()
    {
        var log = new OperationLog();
        for (var i = 0; i < OperationLog.MaxEntries + 25; i++) log.Append(Entry($"op{i}"));

        Assert.Equal(OperationLog.MaxEntries, log.Recent(int.MaxValue).Count);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_fail_the_operation_being_logged()
    {
        var log = new OperationLog();
        log.Appended += _ => throw new InvalidOperationException("subscriber is broken");

        // The mutation this line describes has already happened. Losing the log entry is
        // survivable; throwing here would report a successful commit as a failure.
        log.Append(Entry("commit"));

        Assert.Single(log.Recent());
    }
}
