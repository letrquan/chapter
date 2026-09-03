using System.Collections.Concurrent;

namespace Chapter.Core.Git;

/// <summary>How a linked worktree's branch is brought into the repository's main worktree.</summary>
public enum WorktreeAcceptStrategy
{
    Merge,
    CherryPick,
}

/// <summary>
/// The result of accepting one agent worktree.
///
/// Integration and cleanup are separate mutations. A clean merge followed by a locked
/// worktree removal refusal is still a successful acceptance.
/// </summary>
public sealed record WorktreeAcceptance
{
    public required string SourceWorktreePath { get; init; }
    public required string TargetWorktreePath { get; init; }
    /// <summary>Source branch tip captured before integration.</summary>
    public string SourceHead { get; init; } = "";
    /// <summary>Main worktree tip captured before integration.</summary>
    public string TargetHead { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public WorktreeAcceptStrategy Strategy { get; init; }
    public required GitMutation Integration { get; init; }
    public GitMutation? Removal { get; init; }
    public bool RemoveRequested { get; init; }

    public bool Success => Integration.Success;
    public bool Removed => Removal?.Success == true;

    public string Message
    {
        get
        {
            if (!Integration.Success) return Integration.Message;

            if (RemoveRequested && Removal is not null && !Removal.Success)
                return $"{Integration.Message} The worktree was not removed: {Removal.Message}";

            if (Removed)
            {
                var name = SourceBranch.Length > 0 ? SourceBranch : "the source worktree";
                return $"{Integration.Message}; removed {name}";
            }

            return Integration.Message;
        }
    }
}

/// <summary>
/// Integrates a clean linked worktree into the repository's main worktree.
///
/// The source is required to be clean. Uncommitted bytes have no commit to merge or
/// cherry-pick, and silently accepting only the branch tip would claim more was accepted
/// than actually was. The live comparison remains the place to inspect those bytes.
/// </summary>
public sealed class WorktreeAcceptanceService(
    GitCli git,
    GitWriter writer,
    UndoService undo,
    WorktreeService worktrees)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts a source worktree into the repository main worktree. The target is always the
    /// main worktree resolved from the repository, never the worktree named by the caller.
    /// </summary>
    public Task<WorktreeAcceptance> AcceptAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        WorktreeAcceptStrategy strategy = WorktreeAcceptStrategy.Merge,
        bool removeWorktree = false,
        bool noFastForward = false,
        string expectedSourceHead = "",
        string expectedTargetHead = "",
        CancellationToken ct = default)
    {
        var key = anyPathInRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        return RunSerializedAsync(gate, anyPathInRepo, sourceWorktreePath, strategy,
            removeWorktree, noFastForward, expectedSourceHead, expectedTargetHead, ct);
    }

    /// <summary>Named alias for hosts that prefer the roadmap's verb.</summary>
    public Task<WorktreeAcceptance> AcceptWorktreeAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        WorktreeAcceptStrategy strategy = WorktreeAcceptStrategy.Merge,
        bool removeWorktree = false,
        bool noFastForward = false,
        string expectedSourceHead = "",
        string expectedTargetHead = "",
        CancellationToken ct = default) =>
        AcceptAsync(anyPathInRepo, sourceWorktreePath, strategy, removeWorktree, noFastForward,
            expectedSourceHead, expectedTargetHead, ct);

    /// <summary>
    /// Removes the source directory only if it is still the exact snapshot that was
    /// accepted. Agents can commit again between integration and cleanup; deleting that
    /// worktree without this check would hide the newer commit even though it was never
    /// brought into main.
    /// </summary>
    public async Task<GitMutation> RemoveAcceptedWorktreeAsync(
        WorktreeAcceptance acceptance, CancellationToken ct = default)
    {
        const string operation = "remove accepted worktree";
        var source = acceptance.SourceWorktreePath;
        var target = acceptance.TargetWorktreePath;

        if (!acceptance.Success || source.Length == 0 || target.Length == 0)
            return MutationRefused(target, operation, GitFailure.WouldLoseChanges,
                "the worktree was not accepted, so it cannot be removed as part of this action");

        IReadOnlyList<Worktree> listed;
        try
        {
            listed = await worktrees.ListAsync(target, ct).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return MutationRefused(target, operation, GitFailure.NotFound,
                ex.StandardError.Trim().Length > 0 ? ex.StandardError.Trim() : "the repository could not be read");
        }

        var sourceEntry = listed.FirstOrDefault(worktree =>
            string.Equals(
                worktree.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

        if (sourceEntry is null)
            return MutationRefused(target, operation, GitFailure.NotFound,
                "the accepted source worktree is no longer registered");

        if (!string.Equals(sourceEntry.Branch, acceptance.SourceBranch, StringComparison.Ordinal))
            return MutationRefused(target, operation, GitFailure.WouldLoseChanges,
                "the source worktree changed branches after acceptance; it was left in place");

        var currentHead = await ReadHeadAsync(sourceEntry.Path, ct).ConfigureAwait(false);
        if (currentHead is null || !string.Equals(currentHead, acceptance.SourceHead,
                StringComparison.OrdinalIgnoreCase))
            return MutationRefused(target, operation, GitFailure.WouldLoseChanges,
                "the source branch gained new work after acceptance; it was left in place");

        var status = await git.TryRunAsync(
            sourceEntry.Path, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all")
            .ConfigureAwait(false);
        if (!status.Success)
            return MutationRefused(target, operation, GitFailure.Unknown,
                "the accepted source worktree status could not be read safely");

        if (status.StandardOutput.Trim('\0', '\r', '\n', ' ', '\t').Length > 0)
            return MutationRefused(target, operation, GitFailure.WouldLoseChanges,
                "the source worktree has new uncommitted changes; it was left in place");

        return await worktrees.RemoveAsync(target, sourceEntry.Path, force: false, ct)
            .ConfigureAwait(false);
    }

    private async Task<WorktreeAcceptance> RunSerializedAsync(
        SemaphoreSlim gate,
        string anyPathInRepo,
        string sourceWorktreePath,
        WorktreeAcceptStrategy strategy,
        bool removeWorktree,
        bool noFastForward,
        string expectedSourceHead,
        string expectedTargetHead,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await AcceptCoreAsync(anyPathInRepo, sourceWorktreePath, strategy,
                removeWorktree, noFastForward, expectedSourceHead, expectedTargetHead, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorktreeAcceptance> AcceptCoreAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        WorktreeAcceptStrategy strategy,
        bool removeWorktree,
        bool noFastForward,
        string expectedSourceHead,
        string expectedTargetHead,
        CancellationToken ct)
    {
        var source = sourceWorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (source.Length == 0)
            return Refused(sourceWorktreePath, "accept worktree", GitFailure.NotFound,
                "a source worktree is required", strategy, removeWorktree);

        IReadOnlyList<Worktree> listed;
        try
        {
            listed = await worktrees.ListAsync(anyPathInRepo, ct).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return Refused(anyPathInRepo, "accept worktree", GitFailure.NotFound,
                ex.StandardError.Trim().Length > 0 ? ex.StandardError.Trim() : "the repository could not be read",
                strategy, removeWorktree);
        }

        var target = listed.FirstOrDefault(w => w.IsMain);
        var sourceEntry = listed.FirstOrDefault(w =>
            string.Equals(w.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), source,
                StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return Refused(anyPathInRepo, "accept worktree", GitFailure.NotFound,
                "this repository has no main worktree", strategy, removeWorktree);

        var targetPath = target.Path;
        if (sourceEntry is null)
            return Refused(targetPath, "accept worktree", GitFailure.NotFound,
                "that source worktree is not part of this repository", strategy, removeWorktree,
                source, targetPath);

        if (string.Equals(sourceEntry.Path, targetPath, StringComparison.OrdinalIgnoreCase))
            return Refused(targetPath, "accept worktree", GitFailure.NotFound,
                "the main worktree cannot accept itself", strategy, removeWorktree,
                sourceEntry.Path, targetPath);

        if (!target.IsUsable || target.IsBare)
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the main worktree has no usable working directory", strategy, removeWorktree,
                sourceEntry.Path, targetPath);

        if (!sourceEntry.IsUsable || sourceEntry.IsBare)
            return Refused(targetPath, "accept worktree", GitFailure.NotFound,
                "the source worktree has no usable working directory", strategy, removeWorktree,
                sourceEntry.Path, targetPath);

        if (string.IsNullOrWhiteSpace(sourceEntry.Branch))
            return Refused(targetPath, "accept worktree", GitFailure.NotFound,
                "the source worktree is detached and has no branch to accept", strategy, removeWorktree,
                sourceEntry.Path, targetPath);

        var sourceState = await new RepositoryStateReader(git).ReadAsync(sourceEntry.Path, ct)
            .ConfigureAwait(false);
        if (sourceState.ProbeFailed)
            return Refused(targetPath, "accept worktree", GitFailure.Unknown,
                "the source repository state could not be read safely", strategy, removeWorktree,
                sourceEntry.Path, targetPath, sourceEntry.Branch);

        if (sourceState.IsOperationInProgress || sourceState.HasConflicts)
            return Refused(targetPath, "accept worktree", GitFailure.OperationInProgress,
                $"the source worktree has {sourceState.Description}; finish or abort it first",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var sourceStatus = await git.TryRunAsync(
            sourceEntry.Path, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all")
            .ConfigureAwait(false);
        if (!sourceStatus.Success)
            return Refused(targetPath, "accept worktree", GitFailure.Unknown,
                "the source worktree status could not be read safely", strategy, removeWorktree,
                sourceEntry.Path, targetPath, sourceEntry.Branch);

        // Porcelain-v2 emits one NUL-terminated record per dirty path. Ignored files are not
        // included intentionally: they are not part of the branch and removal is explicit.
        if (sourceStatus.StandardOutput.Trim('\0', '\r', '\n', ' ', '\t').Length > 0)
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the source worktree has uncommitted changes; commit or stash them first",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var sourceHead = await ReadHeadAsync(sourceEntry.Path, ct).ConfigureAwait(false);
        if (sourceHead is null)
            return Refused(targetPath, "accept worktree", GitFailure.NotFound,
                "the source branch has no commit yet", strategy, removeWorktree,
                sourceEntry.Path, targetPath, sourceEntry.Branch);

        if (expectedSourceHead.Length > 0 && !string.Equals(
                expectedSourceHead, sourceHead, StringComparison.OrdinalIgnoreCase))
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the source worktree changed since it was selected; refresh and try again",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var targetHead = await ReadHeadAsync(targetPath, ct).ConfigureAwait(false);
        if (targetHead is null)
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the main worktree has no commit yet; create its first commit before accepting another branch",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        if (expectedTargetHead.Length > 0 && !string.Equals(
                expectedTargetHead, targetHead, StringComparison.OrdinalIgnoreCase))
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the main worktree changed since this action was opened; refresh and try again",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        // Resolve the branch to the exact tip just read. If another process moved it while
        // status was being inspected, refuse rather than accepting a different agent result.
        var branchTip = await git.TryRunAsync(
            targetPath, ct, "show-ref", "--verify", "--hash", $"refs/heads/{sourceEntry.Branch}")
            .ConfigureAwait(false);
        if (!branchTip.Success || !string.Equals(branchTip.Trimmed, sourceHead, StringComparison.OrdinalIgnoreCase))
            return Refused(targetPath, "accept worktree", GitFailure.WouldLoseChanges,
                "the source branch moved while it was being read; refresh the worktree list and try again",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var operation = strategy is WorktreeAcceptStrategy.Merge
            ? $"accept {sourceEntry.Branch} by merge"
            : $"accept {sourceEntry.Branch} by cherry-pick";

        // Re-read the target immediately before computing the commit set. A colleague can
        // commit on main while the source status is being inspected.
        var currentTargetHead = await ReadHeadAsync(targetPath, ct).ConfigureAwait(false);
        if (!string.Equals(currentTargetHead, targetHead, StringComparison.OrdinalIgnoreCase))
            return Refused(targetPath, operation, GitFailure.WouldLoseChanges,
                "the main worktree changed while this acceptance was being prepared; refresh and try again",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var baseResult = await git.TryRunAsync(
            targetPath, ct, "merge-base", targetHead, sourceHead).ConfigureAwait(false);

        // Exit code 1 with no diagnostic is Git's normal answer for unrelated histories;
        // keep that case available to cherry-pick. Any diagnostic, or a malformed successful
        // answer, means the commit set could not be established safely and must not be guessed.
        if (!baseResult.Success && baseResult.StandardError.Trim().Length > 0)
            return Refused(targetPath, operation, GitFailure.Unknown,
                "the branches' common history could not be read safely",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        var mergeBase = baseResult.Success && IsObjectId(baseResult.Trimmed)
            ? baseResult.Trimmed
            : null;

        if (baseResult.Success && mergeBase is null)
            return Refused(targetPath, operation, GitFailure.Unknown,
                "git returned an invalid common-history object",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        if (strategy is WorktreeAcceptStrategy.Merge && mergeBase is null)
            return Refused(targetPath, operation, GitFailure.NotFound,
                "the two branches do not share a history; merge them manually with an explicit unrelated-history choice",
                strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

        IReadOnlyList<string> commits = [];
        if (strategy is WorktreeAcceptStrategy.CherryPick)
        {
            var range = mergeBase is null ? sourceHead : $"{mergeBase}..{sourceHead}";
            var listedCommits = await git.TryRunAsync(
                targetPath, ct, "rev-list", "--reverse", range).ConfigureAwait(false);
            if (!listedCommits.Success)
                return Refused(targetPath, operation, GitFailure.NotFound,
                    "the source branch's commits could not be read", strategy, removeWorktree,
                    sourceEntry.Path, targetPath, sourceEntry.Branch);

            var commitLines = listedCommits.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();
            if (commitLines.Any(line => !IsObjectId(line)))
                return Refused(targetPath, operation, GitFailure.Unknown,
                    "git returned an invalid source commit list",
                    strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

            commits = commitLines;

            var merges = await git.TryRunAsync(targetPath, ct, "rev-list", "--merges", range)
                .ConfigureAwait(false);
            if (!merges.Success)
                return Refused(targetPath, operation, GitFailure.Unknown,
                    "the source branch's history could not be checked safely",
                    strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

            var mergeLines = merges.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();
            if (mergeLines.Any(line => !IsObjectId(line)))
                return Refused(targetPath, operation, GitFailure.Unknown,
                    "git returned an invalid merge history",
                    strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

            if (mergeLines.Length > 0)
                return Refused(targetPath, operation, GitFailure.NotFound,
                    "the source branch contains merge commits; use merge mode so their parentage is preserved",
                    strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);
        }

        GitMutation integration;
        if (strategy is WorktreeAcceptStrategy.Merge)
        {
            var args = new List<string> { "merge", "--no-edit" };
            if (noFastForward) args.Add("--no-ff");
            args.Add(sourceHead);

            integration = await writer
                .RunAsync(targetPath, operation, WriteKind.StartsOperation, ct, [.. args])
                .ConfigureAwait(false);
        }
        else if (commits.Count == 0)
        {
            integration = NoOp(targetPath, operation,
                "the source branch has no commits that are new to the main worktree");
        }
        else
        {
            if (commits.Count > 600)
                return Refused(targetPath, operation, GitFailure.WouldLoseChanges,
                    "the source branch has too many commits for one safe cherry-pick; use merge mode",
                    strategy, removeWorktree, sourceEntry.Path, targetPath, sourceEntry.Branch);

            integration = await CherryPickAsync(targetPath, operation, commits, ct)
                .ConfigureAwait(false);
        }

        if (!integration.Success)
        {
            return new WorktreeAcceptance
            {
                SourceWorktreePath = sourceEntry.Path,
                TargetWorktreePath = targetPath,
                SourceHead = sourceHead,
                TargetHead = targetHead,
                SourceBranch = sourceEntry.Branch,
                Strategy = strategy,
                Integration = integration,
                RemoveRequested = removeWorktree,
            };
        }

        // A clean integration is one undo point even when cherry-pick applied several commits.
        await undo.RecordCommitOperationAsync(
            targetPath, targetHead, "accept", sourceEntry.Branch, ct).ConfigureAwait(false);

        GitMutation? removal = null;
        if (removeWorktree)
        {
            var accepted = new WorktreeAcceptance
            {
                SourceWorktreePath = sourceEntry.Path,
                TargetWorktreePath = targetPath,
                SourceHead = sourceHead,
                TargetHead = targetHead,
                SourceBranch = sourceEntry.Branch,
                Strategy = strategy,
                Integration = integration,
                RemoveRequested = true,
            };
            removal = await RemoveAcceptedWorktreeAsync(accepted, ct).ConfigureAwait(false);
        }

        return new WorktreeAcceptance
        {
            SourceWorktreePath = sourceEntry.Path,
            TargetWorktreePath = targetPath,
            SourceHead = sourceHead,
            TargetHead = targetHead,
            SourceBranch = sourceEntry.Branch,
            Strategy = strategy,
            Integration = integration,
            Removal = removal,
            RemoveRequested = removeWorktree,
        };
    }

    private async Task<GitMutation> CherryPickAsync(
        string targetPath,
        string operation,
        IReadOnlyList<string> commits,
        CancellationToken ct)
    {
        // One invocation makes the whole acceptance one Git sequencer operation. If a later
        // commit conflicts, `cherry-pick --abort` returns the target to its pre-acceptance
        // tip instead of leaving earlier commits applied with no undo point. Agent branches
        // are normally short; reject an invocation that would exceed Windows' argument
        // budget rather than silently splitting the operation into non-atomic chunks.
        var args = new List<string> { "cherry-pick", "--no-edit" };
        args.AddRange(commits);
        var result = await writer
            .RunAsync(targetPath, operation, WriteKind.StartsOperation, ct, [.. args])
            .ConfigureAwait(false);

        return result with { Operation = operation };
    }

    private async Task<string?> ReadHeadAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
            worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD").ConfigureAwait(false);
        return result.Success && IsObjectId(result.Trimmed) ? result.Trimmed : null;
    }

    private static bool IsObjectId(string value) =>
        value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);

    private static GitMutation NoOp(string worktreePath, string operation, string detail) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = 0,
        Failure = GitFailure.None,
        Detail = detail,
        Attempts = 0,
    };

    private static WorktreeAcceptance Refused(
        string targetPath,
        string operation,
        GitFailure failure,
        string reason,
        WorktreeAcceptStrategy strategy,
        bool removeRequested,
        string sourcePath = "",
        string? resolvedTarget = null,
        string sourceBranch = "")
    {
        var mutation = MutationRefused(resolvedTarget ?? targetPath, operation, failure, reason);

        return new WorktreeAcceptance
        {
            SourceWorktreePath = sourcePath,
            TargetWorktreePath = resolvedTarget ?? targetPath,
            SourceBranch = sourceBranch,
            Strategy = strategy,
            Integration = mutation,
            RemoveRequested = removeRequested,
        };
    }

    private static GitMutation MutationRefused(
        string worktreePath, string operation, GitFailure failure, string reason) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = failure,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    };
}
