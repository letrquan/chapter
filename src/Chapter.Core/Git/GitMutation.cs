namespace Chapter.Core.Git;

/// <summary>
/// Why a mutation failed, in the terms the UI has to act on.
///
/// Every read in this app treats a non-zero exit as "no data" and moves on. A write cannot:
/// the difference between "another process holds the lock, try again" and "the remote
/// rejected your push" decides what the user is offered next, and neither is discoverable
/// from an exit code — git returns 1 or 128 for nearly everything.
/// </summary>
public enum GitFailure
{
    None,

    /// <summary>Another process holds <c>index.lock</c> or a ref lock. Usually transient.</summary>
    Locked,

    /// <summary>A merge, rebase, cherry-pick or revert is under way and forbids this.</summary>
    OperationInProgress,

    /// <summary>Credentials were needed and could not be supplied.</summary>
    AuthenticationRequired,

    /// <summary>The command stopped on conflicts that a human has to resolve.</summary>
    Conflict,

    /// <summary>Refused because it would lose work — an unmerged path, a dirty tree.</summary>
    WouldLoseChanges,

    /// <summary>
    /// The ref is checked out in another worktree, so this one may not touch it.
    ///
    /// Its own member rather than a flavour of <see cref="WouldLoseChanges"/> because the
    /// useful answer is different: nothing is at risk and there is nothing to force — the
    /// branch is simply already open somewhere, and the app can offer to go there. Every
    /// other git GUI meets this rarely; this one is built around having several worktrees
    /// open at once, so it is an ordinary outcome rather than an edge case.
    /// </summary>
    CheckedOutElsewhere,

    /// <summary>The remote refused the update, typically a non-fast-forward push.</summary>
    Rejected,

    /// <summary>Nothing to do: nothing staged, already up to date, no such change.</summary>
    NothingToDo,

    /// <summary>A ref, path or object the command names does not exist.</summary>
    NotFound,

    /// <summary>Git failed for a reason not worth a dedicated branch in the UI.</summary>
    Unknown,
}

/// <summary>
/// The outcome of one mutating git command: what was attempted, what git said, and what
/// the app should do about it.
/// </summary>
public sealed record GitMutation
{
    /// <summary>What the user asked for, in their words — "commit", "discard Program.cs".</summary>
    public required string Operation { get; init; }

    /// <summary>The worktree the command ran in.</summary>
    public required string WorktreePath { get; init; }

    /// <summary>The command as run, for the operation log and for bug reports.</summary>
    public required string CommandLine { get; init; }

    public required int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";

    public GitFailure Failure { get; init; } = GitFailure.None;

    /// <summary>
    /// What the app worked out beyond git's own words — which process holds the lock,
    /// which operation is in progress. Null when there is nothing to add.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>How many times the command ran. Above one means lock contention.</summary>
    public int Attempts { get; init; } = 1;

    public long ElapsedMs { get; init; }

    public bool Success => ExitCode == 0;

    /// <summary>
    /// A sentence to show the user. Prefers what the app deduced, falls back to git's first
    /// line of stderr, and never returns an empty string — a failure with no explanation is
    /// the one thing worse than a wrong one.
    /// </summary>
    public string Message
    {
        get
        {
            if (Success) return $"{Operation} succeeded";
            if (!string.IsNullOrWhiteSpace(Detail)) return Detail!;

            var firstLine = FirstMeaningfulLine(StandardError);
            if (firstLine is not null) return firstLine;

            var firstOut = FirstMeaningfulLine(StandardOutput);
            if (firstOut is not null) return firstOut;

            return $"{Operation} failed with exit code {ExitCode}";
        }
    }

    /// <summary>
    /// Git's most useful line, which is not always the first: it prefixes advice with
    /// "hint:" and progress with "remote:", and a leading blank line is common.
    /// </summary>
    private static string? FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? fallback = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            fallback ??= line;
            if (line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Note:", StringComparison.OrdinalIgnoreCase)) continue;

            return StripPrefix(line);
        }

        return fallback is null ? null : StripPrefix(fallback);
    }

    private static readonly string[] SeverityPrefixes = ["fatal: ", "error: ", "warning: "];

    /// <summary>Git's severity prefixes read as noise once the app has its own framing.</summary>
    private static string StripPrefix(string line)
    {
        foreach (var prefix in SeverityPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }

        return line;
    }
}

/// <summary>
/// Maps git's stderr onto <see cref="GitFailure"/>.
///
/// Matching on message text is unpleasant and unavoidable — git's exit codes carry almost
/// no information, and porcelain commands have no machine-readable error channel. The
/// strings below are the ones git actually emits; each is matched loosely enough to survive
/// the surrounding path or ref name changing, and no more loosely than that.
/// </summary>
public static class GitFailureClassifier
{
    public static GitFailure Classify(string stderr, string stdout = "")
    {
        var text = stderr + "\n" + stdout;

        // Order matters. Lock contention is checked first because it is the only failure
        // here that is worth retrying, and its message can co-occur with others.
        if (IsLocked(text)) return GitFailure.Locked;
        if (IsAuthentication(text)) return GitFailure.AuthenticationRequired;
        if (IsRejected(text)) return GitFailure.Rejected;

        // Ahead of Conflict and WouldLoseChanges, both of which it would otherwise fall
        // into: git phrases the refusal as a checkout problem, and the two commands that
        // hit it say different things about the same situation.
        if (IsCheckedOutElsewhere(text)) return GitFailure.CheckedOutElsewhere;

        if (IsConflict(text)) return GitFailure.Conflict;
        if (IsOperationInProgress(text)) return GitFailure.OperationInProgress;
        if (IsWouldLoseChanges(text)) return GitFailure.WouldLoseChanges;
        if (IsNothingToDo(text)) return GitFailure.NothingToDo;
        if (IsNotFound(text)) return GitFailure.NotFound;

        return GitFailure.Unknown;
    }

    /// <summary>
    /// Lock contention, and only lock contention — this is the one failure the writer
    /// retries, so a false positive costs five pointless attempts and replaces the real
    /// error with a wrong one.
    ///
    /// The quoted-path requirement is what keeps it honest. Searching the whole output for
    /// "unable to create" near ".lock" also matches
    /// <c>error: unable to create file Cargo.lock: Permission denied</c> — a checkout
    /// failing over a lockfile from some other ecosystem, which is neither transient nor
    /// anything to do with git's index. Git's own lock message quotes the path and says
    /// "File exists"; that one does neither.
    /// </summary>
    private static bool IsLocked(string text) =>
        Has(text, "Another git process seems to be running") ||
        Has(text, "cannot lock ref") ||
        Has(text, "could not lock config file") ||
        (Has(text, "File exists") && GitLock.PathFromStderr(text) is not null);

    private static bool IsAuthentication(string text) =>
        Has(text, "Authentication failed") ||
        Has(text, "could not read Username") ||
        Has(text, "could not read Password") ||
        Has(text, "terminal prompts disabled") ||
        Has(text, "Permission denied (publickey") ||
        Has(text, "Host key verification failed") ||
        Has(text, "Invalid username or token") ||
        Has(text, "Support for password authentication was removed");

    private static bool IsRejected(string text) =>
        Has(text, "[rejected]") ||
        Has(text, "Updates were rejected") ||
        Has(text, "non-fast-forward") ||
        Has(text, "failed to push some refs") ||
        Has(text, "protected branch hook declined") ||
        Has(text, "pre-receive hook declined");

    private static bool IsConflict(string text) =>
        Has(text, "CONFLICT (") ||
        Has(text, "Automatic merge failed") ||
        Has(text, "fix conflicts and then commit") ||
        Has(text, "after resolving the conflicts") ||
        Has(text, "could not apply") ||
        Has(text, "needs merge");

    private static bool IsOperationInProgress(string text) =>
        Has(text, "MERGE_HEAD exists") ||
        Has(text, "You have not concluded your merge") ||
        Has(text, "CHERRY_PICK_HEAD exists") ||
        Has(text, "You have not concluded your cherry-pick") ||
        Has(text, "REVERT_HEAD exists") ||
        Has(text, "a cherry-pick or revert is already in progress") ||
        Has(text, "It seems that there is already a rebase") ||
        Has(text, "in the middle of a rebase") ||
        Has(text, "middle of a merge") ||
        Has(text, "You are in the middle of");

    /// <summary>
    /// A branch that is checked out in another worktree.
    ///
    /// The two commands that hit this word it differently — <c>switch</c> says
    /// "'x' is already used by worktree at '…'" and <c>branch -d</c> says "cannot delete
    /// branch 'x' used by worktree at '…'" — but both contain the phrase below, which is
    /// also specific enough not to appear anywhere else.
    /// </summary>
    private static bool IsCheckedOutElsewhere(string text) => Has(text, "used by worktree at");

    private static bool IsWouldLoseChanges(string text) =>
        Has(text, "Your local changes to the following files would be overwritten") ||
        Has(text, "would be overwritten by") ||
        Has(text, "refusing to lose untracked file") ||
        Has(text, "The following untracked working tree files would be overwritten") ||
        Has(text, "not uptodate") ||
        // Deleting a branch whose commits are on no other branch. Squarely "would lose
        // work": the commits become unreachable, and the way forward is `branch -D`.
        Has(text, "is not fully merged") ||
        Has(text, "Please commit your changes or stash them");

    private static bool IsNothingToDo(string text) =>
        Has(text, "nothing to commit") ||
        Has(text, "no changes added to commit") ||
        Has(text, "nothing added to commit") ||
        Has(text, "Everything up-to-date") ||
        Has(text, "Already up to date") ||
        Has(text, "no local changes to save") ||
        Has(text, "nothing to stash");

    private static bool IsNotFound(string text) =>
        Has(text, "did not match any file") ||
        (Has(text, "pathspec") && Has(text, "did not match")) ||
        Has(text, "unknown revision or path not in the working tree") ||
        Has(text, "not a valid object name") ||
        Has(text, "no such branch") ||
        Has(text, "couldn't find remote ref");

    private static bool Has(string text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A short, plain description of a failure kind, used when git's own words are worse
    /// than nothing — which they routinely are for lock contention and auth.
    /// </summary>
    public static string Describe(GitFailure failure, string operation) => failure switch
    {
        GitFailure.None => $"{operation} succeeded",
        GitFailure.Locked => $"Could not {operation}: another process is using this repository",
        GitFailure.OperationInProgress => $"Could not {operation}: an operation is already in progress",
        GitFailure.AuthenticationRequired => $"Could not {operation}: the remote needs credentials",
        GitFailure.Conflict => $"{operation} stopped on conflicts that need resolving",
        GitFailure.WouldLoseChanges => $"Could not {operation}: it would overwrite uncommitted changes",
        GitFailure.CheckedOutElsewhere => $"Could not {operation}: that branch is checked out in another worktree",
        GitFailure.Rejected => $"Could not {operation}: the remote rejected the update",
        GitFailure.NothingToDo => $"Nothing to {operation}",
        GitFailure.NotFound => $"Could not {operation}: git could not find what was named",
        _ => $"Could not {operation}",
    };
}
