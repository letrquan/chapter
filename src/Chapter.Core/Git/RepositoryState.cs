namespace Chapter.Core.Git;

/// <summary>
/// A multi-step git operation that has stopped part-way through and is waiting for the
/// user.
/// </summary>
public enum RepositoryOperation
{
    None,
    Merge,
    Rebase,
    RebaseInteractive,

    /// <summary>An <c>am</c> session — <c>git am</c>, not a rebase, despite sharing a directory.</summary>
    ApplyMailbox,

    CherryPick,
    Revert,
    Bisect,
}

/// <summary>
/// Whether a mutation is allowed right now, and why not when it is not.
/// </summary>
public sealed record WriteGuard(bool Allowed, string? Reason)
{
    public static readonly WriteGuard Ok = new(true, null);

    public static WriteGuard Blocked(string reason) => new(false, reason);
}

/// <summary>
/// What a mutation is trying to do, which is what decides whether the repository's state
/// forbids it.
///
/// A single "may I write" question cannot be answered correctly, and answering it anyway
/// was a real bug: blocking everything during a merge blocks <c>git add</c> on a resolved
/// file, which is precisely the command that ends the merge. The state does not forbid
/// writing — it forbids *starting something else*.
/// </summary>
public enum WriteKind
{
    /// <summary>
    /// Stages, discards, or commits. Legal at any time, including mid-merge, where it is
    /// how the merge gets resolved and concluded.
    /// </summary>
    WorkingTree,

    /// <summary>
    /// Begins a new multi-step operation — merge, rebase, cherry-pick, checkout. This is
    /// the one the repository's state can forbid.
    /// </summary>
    StartsOperation,

    /// <summary>
    /// Continues, skips or aborts the operation already in progress. Never blocked: the
    /// guard exists to notice the state these commands are there to clear.
    /// </summary>
    ResolvesOperation,
}

/// <summary>
/// What state a worktree's repository is in, beyond the file list.
///
/// Half the write operations in the roadmap are illegal while one of these is active, and
/// git's refusal is late and cryptic — "fatal: You have not concluded your merge
/// (MERGE_HEAD exists)" arrives after the user has typed a commit message. Reading the
/// state up front lets the UI say so first.
/// </summary>
public sealed record RepositoryState
{
    public required string WorktreePath { get; init; }

    public RepositoryOperation Operation { get; init; } = RepositoryOperation.None;

    /// <summary>Short branch name, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    public bool IsDetached { get; init; }

    /// <summary>True before the first commit, where HEAD points at a branch that has no tip.</summary>
    public bool IsUnborn { get; init; }

    /// <summary>Which step of a rebase is being replayed, when git tracks that.</summary>
    public int? Step { get; init; }

    public int? TotalSteps { get; init; }

    /// <summary>Paths with unresolved conflicts, sorted for a stable UI ordering.</summary>
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];

    /// <summary>
    /// True when a probe failed and this state is a guess rather than a reading.
    ///
    /// Without it, "could not ask git" is indistinguishable from "nothing is going on", and
    /// the guard would wave through a mutation in a repository that really is mid-rebase
    /// because a transient <c>status</c> failure made it look clean. A guard that fails
    /// open is not a guard.
    /// </summary>
    public bool ProbeFailed { get; init; }

    public bool HasConflicts => ConflictedPaths.Count > 0;

    public bool IsOperationInProgress => Operation is not RepositoryOperation.None;

    /// <summary>A phrase for the UI: "rebase in progress (3/7)".</summary>
    public string Description
    {
        get
        {
            if (!IsOperationInProgress) return HasConflicts ? "unresolved conflicts" : "clean";

            var name = Operation switch
            {
                RepositoryOperation.Merge => "merge",
                RepositoryOperation.Rebase => "rebase",
                RepositoryOperation.RebaseInteractive => "interactive rebase",
                RepositoryOperation.ApplyMailbox => "patch application",
                RepositoryOperation.CherryPick => "cherry-pick",
                RepositoryOperation.Revert => "revert",
                RepositoryOperation.Bisect => "bisect",
                _ => "operation",
            };

            var progress = Step is not null && TotalSteps is not null ? $" ({Step}/{TotalSteps})" : "";
            return $"{name} in progress{progress}";
        }
    }

    /// <summary>
    /// Whether a mutation of this kind may proceed.
    ///
    /// Bisect is deliberately not a blocker even for a new operation: it is a navigation
    /// state, and committing during one is unusual but legal. Refusing would be the app
    /// inventing a rule git does not have.
    /// </summary>
    public WriteGuard CanWrite(WriteKind kind = WriteKind.WorkingTree)
    {
        // Nothing may block the way out of a state. If this returned anything else, a
        // conflicted merge would have no exit through the app at all.
        if (kind is WriteKind.ResolvesOperation) return WriteGuard.Ok;

        // Staging, discarding and committing are legal whatever is in progress — during a
        // merge they are how it gets finished. Git refuses the individual cases it needs to
        // (committing with unmerged paths), and its refusal there is specific and correct,
        // which is more than a blanket pre-check can be.
        if (kind is WriteKind.WorkingTree) return WriteGuard.Ok;

        if (ProbeFailed)
            return WriteGuard.Blocked("the repository's state could not be read, so this is not safe to start");

        if (Operation is not (RepositoryOperation.None or RepositoryOperation.Bisect))
            return WriteGuard.Blocked($"a {Description} — finish or abort it first");

        return HasConflicts
            ? WriteGuard.Blocked($"{ConflictedPaths.Count} file(s) still have unresolved conflicts")
            : WriteGuard.Ok;
    }
}

/// <summary>
/// Reads <see cref="RepositoryState"/> from a worktree.
///
/// Everything comes from the worktree's own git directory rather than the shared one: for
/// a linked worktree, <c>MERGE_HEAD</c> and friends live under
/// <c>.git/worktrees/&lt;name&gt;/</c>, so reading the common directory would report the
/// main worktree's state for every worktree in the repo — precisely wrong for an app whose
/// whole premise is several worktrees at once.
/// </summary>
public sealed class RepositoryStateReader(GitCli git)
{
    public async Task<RepositoryState> ReadAsync(string worktreePath, CancellationToken ct = default)
    {
        var gitDir = await ResolveGitDirAsync(worktreePath, ct).ConfigureAwait(false);

        var headTask = ReadHeadAsync(worktreePath, ct);
        var statusTask = git.TryRunAsync(
            worktreePath, ct, "status", "--porcelain=v2", "-z", "--untracked-files=no");

        await Task.WhenAll(headTask, statusTask).ConfigureAwait(false);

        var head = await headTask.ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);

        var conflicted = status.Success
            ? DiffService.ParseWorkingState(status.StandardOutput).Unmerged
            : new List<string>();

        // Sorted so the UI's list of conflicts does not reshuffle between reads.
        conflicted.Sort(StringComparer.OrdinalIgnoreCase);

        var progress = gitDir is null
            ? (Operation: RepositoryOperation.None, Step: (int?)null, Total: (int?)null)
            : DetectOperation(gitDir);

        return new RepositoryState
        {
            WorktreePath = worktreePath,
            Operation = progress.Operation,
            Step = progress.Step,
            TotalSteps = progress.Total,
            Branch = head.Branch,
            IsDetached = head.IsDetached,
            IsUnborn = head.IsUnborn,
            ConflictedPaths = conflicted,
            // Either probe failing means the None and the empty list above are defaults,
            // not observations, and the guard has to know the difference.
            ProbeFailed = gitDir is null || !status.Success,
        };
    }

    /// <summary>
    /// Identifies the operation from the marker files git leaves in the git directory.
    ///
    /// Order is not arbitrary. A rebase runs through the sequencer, so an interactive
    /// rebase stopped on a conflict has <c>CHERRY_PICK_HEAD</c> set as well as its own
    /// directory; checking cherry-pick first would report every stopped rebase as one.
    /// </summary>
    internal static (RepositoryOperation Operation, int? Step, int? Total) DetectOperation(string gitDir)
    {
        var mergeDir = Path.Combine(gitDir, "rebase-merge");
        if (Directory.Exists(mergeDir))
        {
            // The `interactive` marker is what separates `rebase -i` from a plain rebase
            // that happens to use the merge backend, which is now the default.
            var interactive = File.Exists(Path.Combine(mergeDir, "interactive"));
            var (step, total) = ReadProgress(mergeDir, "msgnum", "end");

            return (interactive ? RepositoryOperation.RebaseInteractive : RepositoryOperation.Rebase, step, total);
        }

        var applyDir = Path.Combine(gitDir, "rebase-apply");
        if (Directory.Exists(applyDir))
        {
            // The same directory serves `git am` and the apply-backend rebase; only the
            // marker file says which, and calling an `am` a rebase would offer the user
            // `rebase --continue` for something that needs `am --continue`.
            var isRebase = File.Exists(Path.Combine(applyDir, "rebasing"));
            var (step, total) = ReadProgress(applyDir, "next", "last");

            return (isRebase ? RepositoryOperation.Rebase : RepositoryOperation.ApplyMailbox, step, total);
        }

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return (RepositoryOperation.Merge, null, null);
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return (RepositoryOperation.CherryPick, null, null);
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return (RepositoryOperation.Revert, null, null);

        // Bisect comes last: it can be running underneath any of the above, and the others
        // are the ones that block a write.
        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return (RepositoryOperation.Bisect, null, null);

        return (RepositoryOperation.None, null, null);
    }

    /// <summary>Rebase progress, from the two counter files git keeps beside the todo list.</summary>
    private static (int? Step, int? Total) ReadProgress(string directory, string stepFile, string totalFile)
    {
        var step = ReadInt(Path.Combine(directory, stepFile));
        var total = ReadInt(Path.Combine(directory, totalFile));
        return (step, total);
    }

    private static int? ReadInt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Git is mid-write on the file. Reporting no progress beats failing the read.
            return null;
        }
    }

    private sealed record HeadState(string? Branch, bool IsDetached, bool IsUnborn);

    /// <summary>
    /// Resolves HEAD without failing on the two states where it has no commit: an unborn
    /// branch in a fresh repository, and a detached HEAD.
    /// </summary>
    private async Task<HeadState> ReadHeadAsync(string worktreePath, CancellationToken ct)
    {
        var symbolic = await git.TryRunAsync(worktreePath, ct, "symbolic-ref", "--quiet", "--short", "HEAD")
            .ConfigureAwait(false);

        if (!symbolic.Success || symbolic.Trimmed.Length == 0)
            return new HeadState(null, IsDetached: true, IsUnborn: false);

        var branch = symbolic.Trimmed;

        // HEAD naming a branch does not mean the branch exists: that is exactly the state
        // of a repository with no commits yet.
        var tip = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
            .ConfigureAwait(false);

        var unborn = !tip.Success || tip.Trimmed.Length == 0;
        return new HeadState(branch, IsDetached: false, IsUnborn: unborn);
    }

    private async Task<string?> ResolveGitDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        if (!result.Success || result.Trimmed.Length == 0) return null;

        return RepoPaths.ToPlatform(result.Trimmed);
    }
}
