using System.Collections.Concurrent;

namespace Chapter.Core.Git;

/// <summary>Where a ref pointed, and when.</summary>
public sealed record ReflogEntry
{
    public required string Sha { get; init; }

    /// <summary>The selector git accepts to reach it, e.g. <c>HEAD@{2}</c>.</summary>
    public required string Selector { get; init; }

    /// <summary>What moved it: "commit: fix the parser", "rebase (finish)".</summary>
    public required string Subject { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>
/// A mutation that can be taken back, with the exact command that takes it back.
/// </summary>
public sealed record UndoPoint
{
    public required string Id { get; init; }

    /// <summary>What was done, phrased for a button: "commit \"fix the parser\"".</summary>
    public required string Label { get; init; }

    public required string WorktreePath { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The git command that reverses it, already argument-split.</summary>
    public required IReadOnlyList<string> InverseCommand { get; init; }

    /// <summary>HEAD before the mutation — where undo puts it back.</summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// HEAD immediately after the mutation.
    ///
    /// Undo refuses when the current HEAD does not match this. That check is not
    /// theoretical here: the agent working in this worktree can commit between the app's
    /// mutation and the user pressing undo, and a <c>reset</c> to the recorded sha would
    /// throw the agent's commit away without saying so.
    /// </summary>
    public string? ExpectedHeadSha { get; init; }

    public string? Branch { get; init; }

    /// <summary>
    /// Whether HEAD moving since invalidates this point.
    ///
    /// True for anything whose inverse names a commit — undoing a commit with
    /// <c>reset --soft</c> to a remembered sha throws away whatever landed on top of it,
    /// which is the case this whole check exists for.
    ///
    /// False for the inverses that name a *ref* rather than a commit: renaming a branch
    /// back, or recreating a deleted one at its old tip, does the same correct thing
    /// whatever HEAD has done in the meantime. Leaving the check on for those would refuse
    /// a safe undo whenever the agent in that worktree happened to commit first — and in
    /// this app that is the expected case, not the unusual one.
    /// </summary>
    public bool VerifiesHead { get; init; } = true;

    /// <summary>Whether reversing this loses work that is not recoverable from the reflog.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Extra caution to show before running it, when there is any.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Keeps mutations reversible.
///
/// Almost every git mutation is recoverable — the reflog holds the old tip, ORIG_HEAD holds
/// the last big move, and a stash holds the working tree — but only if something recorded
/// what the old state was before the new one replaced it. That recording has to happen at
/// the moment of the mutation, which is why this exists in Phase 0 rather than being added
/// once there are more mutations to undo: retrofitting it means going back through every
/// call site that already shipped.
/// </summary>
public sealed class UndoService(GitCli git, GitWriter writer)
{
    /// <summary>
    /// How far back undo goes per worktree. Deep enough to cover a work session, shallow
    /// enough that the stack cannot become the thing holding the memory.
    /// </summary>
    private const int MaxDepth = 50;

    private readonly ConcurrentDictionary<string, List<UndoPoint>> _stacks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serialises undo per worktree.
    ///
    /// Without it, two bridge calls can be in flight at once — the window's message handler
    /// is <c>async void</c>, so nothing stops them — and both pass the HEAD check against
    /// the same value before either has run. The second <c>reset --soft</c> then moves HEAD
    /// a further commit back, undoing something the user never asked about.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a worktree's undo stack changes, so the UI can re-label its button.</summary>
    public event Action<string>? StackChanged;

    /// <summary>
    /// Notifies subscribers without letting one of them fail the mutation that caused the
    /// change. By the time this runs the git command has already happened; throwing here
    /// would report a completed commit as a failure.
    /// </summary>
    private void RaiseStackChanged(string worktreePath)
    {
        try
        {
            StackChanged?.Invoke(worktreePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Undo stack subscriber failed: {ex.Message}");
        }
    }

    /// <summary>Where HEAD is, captured before a mutation so the inverse can name it.</summary>
    public async Task<string?> CaptureHeadAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
            .ConfigureAwait(false);

        // No commits yet is a real state, not a failure: the inverse of the first commit
        // is deleting the ref rather than resetting to something.
        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    public UndoPoint? Peek(string worktreePath)
    {
        if (!_stacks.TryGetValue(worktreePath, out var stack)) return null;

        // Checked inside the lock: a stack that was non-empty a moment ago can be emptied
        // by a concurrent undo before the read.
        lock (stack) return stack.Count > 0 ? stack[^1] : null;
    }

    public void Record(UndoPoint point)
    {
        var stack = _stacks.GetOrAdd(point.WorktreePath, _ => []);
        lock (stack)
        {
            stack.Add(point);
            if (stack.Count > MaxDepth) stack.RemoveRange(0, stack.Count - MaxDepth);
        }

        RaiseStackChanged(point.WorktreePath);
    }

    /// <summary>
    /// Records the undo for a commit that has just been made.
    ///
    /// <c>reset --soft</c> is the inverse rather than <c>--mixed</c> or <c>--hard</c>
    /// because it moves the branch and touches nothing else: the index and the working tree
    /// come back exactly as they were the instant before the commit, which is what "undo"
    /// has to mean for it to be safe to offer.
    /// </summary>
    public async Task RecordCommitAsync(
        string worktreePath, string? previousHead, string subject, CancellationToken ct = default)
    {
        var newHead = await CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);

        RecordCommit(worktreePath, previousHead, newHead, "commit", subject);
    }

    /// <summary>
    /// Records a commit made by an operation whose label is not simply <c>commit</c> —
    /// cherry-pick and revert are the two examples. The point is only added when HEAD
    /// actually moved; a command that reports success without creating a commit must not
    /// leave an undo button that would reset an unrelated tip.
    /// </summary>
    public async Task<bool> RecordCommitOperationAsync(
        string worktreePath,
        string? previousHead,
        string operation,
        string subject,
        CancellationToken ct = default)
    {
        var newHead = await CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);
        if (newHead is null || string.Equals(previousHead, newHead, StringComparison.OrdinalIgnoreCase))
            return false;

        RecordCommit(worktreePath, previousHead, newHead, operation, subject);
        return true;
    }

    /// <summary>
    /// Records the inverse of a history rewrite such as an interactive rebase.
    ///
    /// Unlike a normal commit undo, restoring a rewritten history also has to restore the
    /// old tree. A soft reset would leave the rebased tree in the index and working tree,
    /// which is not the state the user asked to get back to. `--keep` makes that exact reset
    /// conditional: if somebody has edited an overlapping file since the rebase, Git refuses
    /// rather than throwing that work away. The rewritten commits remain reachable through
    /// the reflog, so this is an undoable history move rather than a permanent discard.
    /// </summary>
    public async Task<bool> RecordHistoryRewriteAsync(
        string worktreePath,
        string? previousHead,
        string operation,
        string subject,
        CancellationToken ct = default,
        bool isDestructive = false,
        string? warning = null,
        string? expectedNewHead = null)
    {
        var newHead = await CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);
        if (previousHead is null || newHead is null ||
            string.Equals(previousHead, newHead, StringComparison.OrdinalIgnoreCase))
            return false;

        if (expectedNewHead is not null &&
            !string.Equals(expectedNewHead, newHead, StringComparison.OrdinalIgnoreCase))
            return false;

        var label = string.IsNullOrWhiteSpace(subject) ||
                    string.Equals(subject, operation, StringComparison.OrdinalIgnoreCase)
            ? operation
            : $"{operation} \"{Shorten(subject)}\"";

        Record(new UndoPoint
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = label,
            WorktreePath = worktreePath,
            Timestamp = DateTimeOffset.Now,
            InverseCommand = ["reset", "--keep", previousHead],
            HeadSha = previousHead,
            ExpectedHeadSha = newHead,
            IsDestructive = isDestructive,
            Warning = warning ?? "This restores the pre-rebase history and tree. Git refuses if somebody has changed an overlapping file; the rewritten commits remain in the reflog.",
        });
        return true;
    }

    private void RecordCommit(
        string worktreePath,
        string? previousHead,
        string? newHead,
        string operation,
        string subject)
    {

        // A root commit has no previous tip to reset to, so the inverse is to remove the
        // branch's tip altogether. That leaves the index and working tree untouched, which
        // is the same guarantee reset --soft gives everywhere else.
        IReadOnlyList<string> inverse = previousHead is null
            ? ["update-ref", "-d", "HEAD"]
            : ["reset", "--soft", previousHead];

        Record(new UndoPoint
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = $"{operation} \"{Shorten(subject)}\"",
            WorktreePath = worktreePath,
            Timestamp = DateTimeOffset.Now,
            InverseCommand = inverse,
            HeadSha = previousHead,
            ExpectedHeadSha = newHead,
            IsDestructive = false,
        });
    }

    /// <summary>
    /// Reverses the most recent mutation in a worktree.
    ///
    /// Refuses rather than guesses in the three cases that matter: nothing recorded, no
    /// recorded expectation to check against, and HEAD having moved since. All three would
    /// otherwise be silent — the first as a no-op, the others as a reset over somebody
    /// else's commit.
    /// </summary>
    public async Task<GitMutation> UndoAsync(string worktreePath, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(worktreePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await UndoOnceAsync(worktreePath, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GitMutation> UndoOnceAsync(string worktreePath, CancellationToken ct)
    {
        var point = Peek(worktreePath);
        if (point is null) return NothingToUndo(worktreePath);

        // Skipped only where the inverse names a ref rather than a commit, which makes it
        // correct regardless of where HEAD is. Everything else is checked.
        if (point.VerifiesHead)
        {
            // Refused rather than skipped when there is nothing to compare against. A null
            // expectation means the probe failed when the point was recorded, which is
            // indistinguishable from "no commits yet" — and treating it as permission to
            // reset unconditionally turns the one check that protects the agent's work into
            // a check that silently is not there.
            var current = await CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);

            if (point.ExpectedHeadSha is null)
                return CannotVerify(worktreePath, point);

            if (!string.Equals(current, point.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
                return HeadMoved(worktreePath, point);
        }

        // WorkingTree, not StartsOperation: both recorded inverses only move this branch's
        // own pointer, and they stay legal — and wanted — while an agent has a merge in
        // progress that the user had no part in starting.
        var mutation = await writer
            .RunAsync(worktreePath, $"undo {point.Label}", WriteKind.WorkingTree, ct, [.. point.InverseCommand])
            .ConfigureAwait(false);

        // Only a mutation that actually ran pops the stack. Leaving a failed one in place
        // means the user can fix whatever blocked it and press undo again.
        if (mutation.Success) Pop(worktreePath, point);

        return mutation;
    }

    /// <summary>
    /// Removes a point only if it is still the top one.
    ///
    /// Popping the top blindly discards whatever was recorded while the inverse was
    /// running, and leaves the commit just reversed still offered as undoable — so a second
    /// press would reset past a commit that no longer exists.
    /// </summary>
    private void Pop(string worktreePath, UndoPoint point)
    {
        if (!_stacks.TryGetValue(worktreePath, out var stack)) return;

        lock (stack)
        {
            var index = stack.LastIndexOf(point);
            if (index < 0) return;

            stack.RemoveAt(index);
        }

        RaiseStackChanged(worktreePath);
    }

    public void Forget(string worktreePath)
    {
        if (_gates.TryRemove(worktreePath, out var gate)) gate.Dispose();
        if (_stacks.TryRemove(worktreePath, out _)) RaiseStackChanged(worktreePath);
    }

    /// <summary>
    /// Recent movements of HEAD, straight from the reflog.
    ///
    /// This is the floor under the undo stack. The stack only knows about mutations this
    /// app made in this session; the reflog knows about everything, including what the
    /// agent did, and survives a restart. When the stack is empty this is what the UI can
    /// still offer.
    /// </summary>
    /// <param name="limit">How many entries to read, most recent first.</param>
    public async Task<IReadOnlyList<ReflogEntry>> ReadReflogAsync(
        string worktreePath, int limit = 25, CancellationToken ct = default)
    {
        // %cI is the wrong field and was the first thing tried: it is the committer date of
        // the commit an entry points *at*, not when the entry was written. Every reflog
        // entry that does not create a commit — checkout, reset, merge --ff, rebase
        // (finish) — would carry the target commit's age, which for a checkout of an old
        // commit is years out. Git has no %g-family date placeholder; the entry time is
        // only reachable by formatting the selector itself with --date.
        var result = await git.TryRunAsync(
                worktreePath, ct,
                "reflog", "show", "--no-abbrev", $"--max-count={Math.Max(1, limit)}",
                "--date=iso-strict", "--format=%H%x1f%gd%x1f%gs")
            .ConfigureAwait(false);

        // A repository with no commits has no reflog, and git says so with a non-zero exit
        // rather than an empty list.
        return result.Success ? ParseReflog(result.StandardOutput) : [];
    }

    /// <summary>
    /// The <c>%x1f</c> in the format string above: ASCII unit separator. Chosen because a
    /// reflog subject routinely contains spaces, colons and quotes, and every one of them
    /// appears in real commit messages — this one does not.
    /// </summary>
    internal const char FieldSeparator = '\u001f';

    /// <summary>
    /// Parses the reflog format above.
    ///
    /// Under <c>--date=iso-strict</c> the selector field arrives as
    /// <c>HEAD@{2026-08-14T22:50:09+07:00}</c> rather than <c>HEAD@{0}</c> — the date
    /// replaces the ordinal, it does not accompany it. The ordinal form is what git accepts
    /// back as a revision, so it is reconstructed from position: entry <c>i</c> of HEAD's
    /// reflog is <c>HEAD@{i}</c>, by definition.
    /// </summary>
    internal static IReadOnlyList<ReflogEntry> ParseReflog(string output)
    {
        var entries = new List<ReflogEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split(FieldSeparator);
            if (fields.Length < 3) continue;

            entries.Add(new ReflogEntry
            {
                Sha = fields[0],
                Selector = $"HEAD@{{{entries.Count}}}",
                Subject = fields[2],
                Timestamp = ParseSelectorDate(fields[1]),
            });
        }

        return entries;
    }

    /// <summary>
    /// Pulls the timestamp out of a date-formatted reflog selector.
    /// Invariant culture because the format is ISO 8601 and git does not localise it —
    /// parsing it under the user's culture makes the result depend on their locale.
    /// </summary>
    private static DateTimeOffset? ParseSelectorDate(string selector)
    {
        var open = selector.IndexOf('{');
        var close = selector.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        var text = selector[(open + 1)..close];

        return DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when)
            ? when
            : null;
    }


    private static GitMutation NothingToUndo(string worktreePath) => new()
    {
        Operation = "undo",
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.NothingToDo,
        Detail = "There is nothing to undo in this worktree",
        Attempts = 0,
    };

    private static GitMutation CannotVerify(string worktreePath, UndoPoint point) => new()
    {
        Operation = "undo",
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.WouldLoseChanges,
        Detail = $"Cannot undo {point.Label}: where HEAD was at the time was never recorded, " +
                 "so there is no way to tell whether anything has committed since.",
        Attempts = 0,
    };

    private static GitMutation HeadMoved(string worktreePath, UndoPoint point) => new()
    {
        Operation = "undo",
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.WouldLoseChanges,
        Detail = $"Cannot undo {point.Label}: something else has committed in this worktree since. " +
                 "Undoing now would discard that work.",
        Attempts = 0,
    };

    private static string Shorten(string subject)
    {
        var line = subject.Split('\n')[0].Trim();
        return line.Length <= 50 ? line : line[..47] + "…";
    }
}
