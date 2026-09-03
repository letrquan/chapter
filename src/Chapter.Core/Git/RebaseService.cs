using System.Collections.Concurrent;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>The command written on one line of an interactive rebase todo list.</summary>
public enum RebaseAction
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop,
}

/// <summary>
/// One commit in an interactive rebase plan.
///
/// The sha is the identity captured when the plan was read. It is deliberately not a short
/// hash or a positional index: another process can add commits while the plan is on screen,
/// and applying an index to that new list would rewrite a different commit.
/// </summary>
public sealed record RebaseTodoEntry
{
    public required string Sha { get; init; }
    public required string Subject { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = [];
    public RebaseAction Action { get; init; } = RebaseAction.Pick;

    /// <summary>
    /// Optional replacement message for a reword or squash action. Empty keeps Git's
    /// existing message and lets the caller edit it later at a stopped step.
    /// </summary>
    public string Message { get; init; } = "";

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>A snapshot of the commits and tip an interactive rebase plan was built from.</summary>
public sealed record RebasePlan
{
    public required string WorktreePath { get; init; }

    /// <summary>Empty means the plan starts at the repository root.</summary>
    public string Upstream { get; init; } = "";

    public required string Head { get; init; }
    public string? Branch { get; init; }
    public required IReadOnlyList<RebaseTodoEntry> Entries { get; init; }

    public bool IsRoot => Upstream.Length == 0;
    public bool ContainsMerges => Entries.Any(entry => entry.Parents.Count > 1);

    /// <summary>Whether the plan has at least one commit that can be replayed.</summary>
    public bool HasCommits => Entries.Count > 0;

    /// <summary>A reason the UI can show before the start button, or null when suitable.</summary>
    public string? UnavailableReason => ContainsMerges
        ? "This plan contains merge commits. Rebase them with a merge-aware history tool first."
        : !HasCommits
            ? "There are no commits after that base to rebase."
            : Branch is null
                ? "Interactive rebase needs a branch; HEAD is detached."
                : null;
}

/// <summary>
/// The state Git leaves while an interactive (or ordinary) rebase is paused.
///
/// This is intentionally separate from <see cref="RepositoryState"/>. The latter answers
/// the cross-cutting guard question; this shape carries the todo and stopped commit needed
/// by a continue/skip/abort surface.
/// </summary>
public sealed record RebaseState
{
    public required string WorktreePath { get; init; }
    public RepositoryOperation Operation { get; init; } = RepositoryOperation.None;
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public string? OriginalHead { get; init; }
    public string? CurrentCommit { get; init; }
    public string? CurrentSubject { get; init; }
    public RebaseAction? CurrentAction { get; init; }
    public int? Step { get; init; }
    public int? TotalSteps { get; init; }
    public IReadOnlyList<RebaseTodoEntry> Remaining { get; init; } = [];
    public IReadOnlyList<RebaseTodoEntry> Completed { get; init; } = [];
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public bool CanContinue { get; init; }
    public bool CanSkip { get; init; }
    public bool CanAbort { get; init; }
    public bool IsPaused => Operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive;
}

/// <summary>
/// Plans and runs guarded interactive rebases.
///
/// Git still owns the replay algorithm. Chapter supplies the todo file through a temporary
/// sequence editor, so hooks, merge machinery and reflogs behave exactly as they do from a
/// terminal. The command remains behind <see cref="GitWriter"/>; no direct process launch is
/// used here, which keeps lock retry, state guards, watcher tagging and the operation log in
/// one place.
/// </summary>
public sealed class RebaseService(GitCli git, GitWriter writer, UndoService undo) : IDisposable
{
    private const char Separator = '\u001f';
    private const string PlanFormat = "%H%x1f%P%x1f%s%x1f%an%x1f%ae%x1f%aI";

    private readonly RepositoryStateReader _stateReader = new(git);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RebaseSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the commits after <paramref name="upstream"/> in oldest-first order.
    /// An empty upstream means all commits from the root through HEAD.
    /// </summary>
    public Task<RebasePlan> GetPlanAsync(
        string worktreePath, string upstream = "", CancellationToken ct = default) =>
        ReadPlanAsync(worktreePath, upstream ?? "", ct);

    /// <summary>Alias used by hosts that call the read a preview rather than a plan.</summary>
    public Task<RebasePlan> PreviewAsync(
        string worktreePath, string upstream = "", CancellationToken ct = default) =>
        GetPlanAsync(worktreePath, upstream, ct);

    /// <summary>
    /// Starts an interactive rebase from a plan captured by <see cref="GetPlanAsync"/>.
    ///
    /// <paramref name="expectedHead"/> is optional for direct callers, but the bridge sends
    /// it. When present, it closes the race between opening the planner and pressing Start.
    /// </summary>
    public Task<GitMutation> StartAsync(
        string worktreePath,
        string upstream,
        IReadOnlyList<RebaseTodoEntry> entries,
        string expectedHead = "",
        CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath,
            () => StartCoreAsync(worktreePath, upstream ?? "", entries ?? [], expectedHead ?? "", ct), ct);

    /// <summary>Convenience overload for starting directly from a returned plan.</summary>
    public Task<GitMutation> StartAsync(
        string worktreePath, RebasePlan plan, CancellationToken ct = default) =>
        StartAsync(worktreePath, plan.Upstream, plan.Entries, plan.Head, ct);

    /// <summary>Explicit name for callers that want to distinguish this from ordinary rebase.</summary>
    public Task<GitMutation> StartInteractiveAsync(
        string worktreePath,
        string upstream,
        IReadOnlyList<RebaseTodoEntry> entries,
        string expectedHead = "",
        CancellationToken ct = default) =>
        StartAsync(worktreePath, upstream, entries, expectedHead, ct);

    /// <summary>Continues a paused rebase, optionally supplying the message for a reword step.</summary>
    public Task<GitMutation> ContinueAsync(
        string worktreePath, string message = "", CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath,
            () => ResolveCoreAsync(worktreePath, "continue", message ?? "", ct), ct);

    /// <summary>Skips the stopped commit in a paused rebase.</summary>
    public Task<GitMutation> SkipAsync(
        string worktreePath, CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath,
            () => ResolveCoreAsync(worktreePath, "skip", "", ct), ct);

    /// <summary>Aborts a paused rebase and restores the original branch tip.</summary>
    public Task<GitMutation> AbortAsync(
        string worktreePath, CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath,
            () => ResolveCoreAsync(worktreePath, "abort", "", ct), ct);

    /// <summary>Reads the detailed state Git stores beside the worktree's git directory.</summary>
    public async Task<RebaseState> GetStateAsync(
        string worktreePath, CancellationToken ct = default)
    {
        var repository = await _stateReader.ReadAsync(worktreePath, ct).ConfigureAwait(false);
        var gitDir = await ResolveGitDirAsync(worktreePath, ct).ConfigureAwait(false);

        if (gitDir is null || repository.Operation is not (RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive))
        {
            return new RebaseState
            {
                WorktreePath = worktreePath,
                Operation = repository.Operation,
                Branch = repository.Branch,
                Step = repository.Step,
                TotalSteps = repository.TotalSteps,
                ConflictedPaths = repository.ConflictedPaths,
                CanContinue = false,
                CanSkip = false,
                CanAbort = false,
            };
        }

        // Git chooses the merge backend for interactive rebases on modern versions, but an
        // older configuration can still select the apply backend. Both carry the same
        // sequencer files; only the directory name differs.
        var directoryName = Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
            ? "rebase-merge"
            : "rebase-apply";
        var directory = Path.Combine(gitDir, directoryName);
        var remaining = await ReadTodoFileAsync(
                worktreePath, Path.Combine(directory, "git-rebase-todo"), ct)
            .ConfigureAwait(false);
        var completed = await ReadTodoFileAsync(
                worktreePath, Path.Combine(directory, "done"), ct)
            .ConfigureAwait(false);
        var stopped = ReadText(Path.Combine(directory, "stopped-sha"));
        var current = stopped ?? completed.LastOrDefault()?.Sha;
        var currentAction = FindCurrentStepAction(current, completed);
        var currentSubject = current is null
            ? null
            : (await ReadSubjectAsync(worktreePath, current, ct).ConfigureAwait(false));

        return new RebaseState
        {
            WorktreePath = worktreePath,
            Operation = repository.Operation,
            Branch = ReadBranchName(Path.Combine(directory, "head-name")) ?? repository.Branch,
            Upstream = ReadText(Path.Combine(directory, "onto")),
            OriginalHead = ReadText(Path.Combine(directory, "orig-head")),
            CurrentCommit = current,
            CurrentSubject = currentSubject,
            CurrentAction = currentAction,
            Step = repository.Step,
            TotalSteps = repository.TotalSteps,
            Remaining = remaining,
            Completed = completed,
            ConflictedPaths = repository.ConflictedPaths,
            CanContinue = !repository.HasConflicts,
            CanSkip = true,
            CanAbort = true,
        };
    }

    /// <summary>Alias for callers that use the shorter state name.</summary>
    public Task<RebaseState> StateAsync(string worktreePath, CancellationToken ct = default) =>
        GetStateAsync(worktreePath, ct);

    private async Task<RebasePlan> ReadPlanAsync(
        string worktreePath, string upstream, CancellationToken ct)
    {
        var head = await ReadHeadAsync(worktreePath, ct).ConfigureAwait(false);
        if (head is null)
            throw new InvalidOperationException("This repository has no commit to rebase.");

        if (upstream.Length > 0)
        {
            EnsureObjectId(upstream, "Rebase base");
            var ancestor = await git.TryRunAsync(
                    worktreePath, ct, "merge-base", "--is-ancestor", upstream, head)
                .ConfigureAwait(false);
            if (!ancestor.Success)
                throw new InvalidOperationException(
                    $"Rebase base '{upstream}' is not reachable from this worktree's HEAD.");
        }

        var branch = await ReadBranchAsync(worktreePath, ct).ConfigureAwait(false);
        var args = new List<string>
        {
            "log", "--reverse", "--topo-order", $"--format={PlanFormat}",
        };

        if (upstream.Length == 0) args.Add(head);
        else args.Add($"{upstream}..{head}");

        var result = await git.TryRunAsync(worktreePath, ct, [.. args]).ConfigureAwait(false);
        if (!result.Success)
            throw new GitException(result.CommandLine, result.ExitCode, result.StandardError);

        var entries = ParsePlan(result.StandardOutput);

        return new RebasePlan
        {
            WorktreePath = worktreePath,
            Upstream = upstream,
            Head = head,
            Branch = branch,
            Entries = entries,
        };
    }

    private async Task<GitMutation> StartCoreAsync(
        string worktreePath,
        string upstream,
        IReadOnlyList<RebaseTodoEntry> requested,
        string expectedHead,
        CancellationToken ct)
    {
        var operation = "interactive rebase";

        RebasePlan plan;
        try
        {
            plan = await ReadPlanAsync(worktreePath, upstream, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Refused(worktreePath, operation, GitFailure.NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Refused(worktreePath, operation, GitFailure.NotFound, ex.Message);
        }

        if (expectedHead.Length > 0)
        {
            try { EnsureObjectId(expectedHead, "Expected HEAD"); }
            catch (ArgumentException ex)
            {
                return Refused(worktreePath, operation, GitFailure.NotFound, ex.Message);
            }

            if (!string.Equals(expectedHead, plan.Head, StringComparison.OrdinalIgnoreCase))
            {
                return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                    "HEAD changed since this plan was read. Read the plan again before starting.");
            }
        }

        // A branch is required for the pointer move to be honest. Rebase can operate on
        // detached HEAD, but the result would have nowhere obvious to land in a review
        // cockpit and is too easy to mistake for a branch rewrite.
        if (string.IsNullOrEmpty(plan.Branch))
        {
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "interactive rebase needs a branch; HEAD is detached");
        }

        if (plan.ContainsMerges)
        {
            return Refused(worktreePath, operation, GitFailure.Unknown,
                "the selected range contains merge commits; merge-aware rebasing is not supported here");
        }

        if (!plan.HasCommits)
            return Refused(worktreePath, operation, GitFailure.NothingToDo,
                "there are no commits after that base");

        var repository = await _stateReader.ReadAsync(worktreePath, ct).ConfigureAwait(false);
        if (repository.ProbeFailed)
            return Refused(worktreePath, operation, GitFailure.Unknown,
                "the repository's state could not be read safely");
        if (repository.Operation is not (RepositoryOperation.None or RepositoryOperation.Bisect))
            return Refused(worktreePath, operation, GitFailure.OperationInProgress,
                $"a {repository.Description} — finish or abort it first");
        if (repository.HasConflicts)
            return Refused(worktreePath, operation, GitFailure.Conflict,
                $"{repository.ConflictedPaths.Count} file(s) still have unresolved conflicts");

        var status = await git.TryRunAsync(
                worktreePath, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all")
            .ConfigureAwait(false);
        if (!status.Success)
            return Refused(worktreePath, operation, GitFailure.Unknown,
                "the working tree could not be read safely");
        if (status.StandardOutput.Length > 0)
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "the working tree is not clean; commit or stash its changes first");

        var entries = NormalizeEntries(plan, requested, out var validationError);
        if (validationError is not null)
            return Refused(worktreePath, operation, GitFailure.NotFound, validationError);

        // A previous invocation can have been cancelled after Git finished but before its
        // cleanup ran. The repository state above is authoritative; once it says no rebase
        // is active, discard that orphaned editor session before creating a new one.
        if (_sessions.TryRemove(worktreePath, out var staleSession))
            staleSession.Dispose();

        var todo = string.Join("\n", entries.Select(ToTodoLine)) + "\n";
        var editors = await EditorFiles.CreateAsync(todo, entries, ct).ConfigureAwait(false);

        var args = new List<string> { "rebase", "--interactive", "--no-autosquash", "--empty=drop" };
        if (upstream.Length == 0)
        {
            args.Add("--root");
        }
        else
        {
            args.Add("--onto");
            args.Add(upstream);
            args.Add(upstream);
        }

        var previousHead = plan.Head;
        var session = new RebaseSession(previousHead, plan.Branch, editors);
        _sessions[worktreePath] = session;

        try
        {
            var mutation = await writer.RunWithEnvironmentAsync(
                    worktreePath, operation, WriteKind.StartsOperation, GitIntent.Write,
                    editors.Environment, ct, [.. args])
                .ConfigureAwait(false);

            await FinishOrKeepSessionAsync(worktreePath, operation)
                .ConfigureAwait(false);
            return mutation;
        }
        catch
        {
            // A process-start or cancellation failure did not necessarily leave a rebase
            // marker. The state probe below is the authority, so only discard the in-memory
            // session when Git is no longer in a rebase.
            var state = await _stateReader.ReadAsync(worktreePath, CancellationToken.None)
                .ConfigureAwait(false);
            if (state.Operation is not (RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
                && _sessions.TryRemove(worktreePath, out var abandoned))
                abandoned.Dispose();
            throw;
        }
    }

    private async Task<GitMutation> ResolveCoreAsync(
        string worktreePath, string verb, string message, CancellationToken ct)
    {
        var repository = await _stateReader.ReadAsync(worktreePath, ct).ConfigureAwait(false);
        if (repository.Operation is not (RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive))
        {
            return Refused(worktreePath, $"rebase {verb}", GitFailure.OperationInProgress,
                "no rebase is in progress");
        }

        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        var session = _sessions.TryGetValue(worktreePath, out var existing) ? existing : null;
        if (session is null)
        {
            // A rebase resumed after an application restart has no in-memory queue. Do not
            // write the caller's message here: an edit stop does not invoke an editor, so an
            // override created for it would survive and be consumed by a later reword or
            // squash prompt. Queue it only after we know which kind of stop we are resolving.
            session = new RebaseSession(state.OriginalHead ?? "", state.Branch,
                await EditorFiles.CreateAsync(todo: "", entries: [], ct)
                    .ConfigureAwait(false));
            _sessions[worktreePath] = session;
        }

        var currentAction = state.CurrentAction;

        // Skip and abort never consume a commit-message editor prompt. Discard any
        // replacement queued for the stopped step so it cannot be applied to a later one.
        if (verb is "skip" or "abort") session.Editors.ClearPendingOverride();

        // Keep an override while the same step is being retried (for example, a reword
        // whose conflicts have not yet been staged), but discard it once Git has advanced.
        // This prevents a message supplied for an earlier stop from being consumed by a
        // later editor prompt after an application restart or a partial continue.
        session.Editors.PrepareForStep(state.CurrentCommit);

        // An `edit` stop is different from a `reword` prompt: Git does not invoke an editor
        // when `rebase --continue` is run. If the planner supplied a replacement message,
        // amend the stopped commit first so the requested edit is not silently ignored.
        if (verb is "continue" && message.Length > 0 &&
            currentAction is RebaseAction.Edit)
        {
            var amended = await writer.RunAsync(
                    worktreePath, "rebase edit", WriteKind.WorkingTree, ct,
                    "commit", "--amend", "--cleanup=whitespace", "-m", message)
                .ConfigureAwait(false);
            if (!amended.Success) return amended;
        }
        else if (message.Length > 0 && currentAction is RebaseAction.Reword or RebaseAction.Squash)
        {
            session.Editors.QueueOverride(message, state.CurrentCommit);
        }

        var mutation = await writer.RunWithEnvironmentAsync(
                worktreePath, $"rebase {verb}", WriteKind.ResolvesOperation, GitIntent.Write,
                session.Editors.Environment, ct, ["rebase", $"--{verb}"])
            .ConfigureAwait(false);

        if (verb is "abort")
        {
            // A lock or another transient failure must not throw away the editor session:
            // the user should be able to retry Abort without losing the paused operation's
            // in-memory message queue. Remove it only once Git has actually left rebase.
            var afterAbort = await _stateReader.ReadAsync(worktreePath, CancellationToken.None)
                .ConfigureAwait(false);
            if (mutation.Success || afterAbort.Operation is not
                    (RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive))
            {
                if (_sessions.TryRemove(worktreePath, out var aborted)) aborted.Dispose();
            }
        }
        else
        {
            await FinishOrKeepSessionAsync(worktreePath, "interactive rebase")
                .ConfigureAwait(false);
        }

        return mutation;
    }

    private async Task FinishOrKeepSessionAsync(
        string worktreePath, string operation)
    {
        // Cleanup is part of a completed mutation, not part of the cancellable git call.
        // If the bridge token is cancelled just after Git exits, using it here would leak
        // the temporary editors and lose the undo point for a history rewrite that already
        // happened.
        var state = await _stateReader.ReadAsync(worktreePath, CancellationToken.None)
            .ConfigureAwait(false);
        if (state.Operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
        {
            // `edit` can be a successful Git exit while the sequencer remains paused. Keep
            // the session so ContinueAsync can record one undo point only at the real finish.
            return;
        }

        if (_sessions.TryRemove(worktreePath, out var session))
        {
            session.Dispose();
            await undo.RecordHistoryRewriteAsync(
                    worktreePath, session.PreviousHead, operation, "interactive rebase",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<T> RunSerializedAsync<T>(
        string worktreePath, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(worktreePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static IReadOnlyList<RebaseTodoEntry> NormalizeEntries(
        RebasePlan plan,
        IReadOnlyList<RebaseTodoEntry> requested,
        out string? error)
    {
        error = null;
        var source = plan.Entries.ToDictionary(e => e.Sha, StringComparer.OrdinalIgnoreCase);
        var entries = requested.Count == 0
            ? plan.Entries.Select(e => e with { Action = RebaseAction.Pick }).ToArray()
            : requested.ToArray();

        if (entries.Length != source.Count)
        {
            error = "The rebase plan is incomplete. Keep every commit and choose Drop explicitly for one you do not want.";
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!HistoryService.IsObjectId(entry.Sha))
            {
                error = $"Rebase commit '{entry.Sha}' is not a full object id.";
                return [];
            }

            if (!seen.Add(entry.Sha))
            {
                error = $"Rebase commit '{entry.ShortSha}' appears more than once.";
                return [];
            }

            if (!source.TryGetValue(entry.Sha, out _))
            {
                error = $"Rebase commit '{entry.Sha}' is not in the selected range.";
                return [];
            }

            // Metadata comes from the current plan, never from the browser. This keeps a
            // stale or edited subject from becoming a malformed todo line.
            if (entry.Action is < RebaseAction.Pick or > RebaseAction.Drop)
            {
                error = $"Unknown rebase action for '{entry.ShortSha}'.";
                return [];
            }

        }

        var kept = false;
        foreach (var entry in entries)
        {
            if (entry.Action is RebaseAction.Drop) continue;

            if (!kept && entry.Action is RebaseAction.Squash or RebaseAction.Fixup)
            {
                error = "The first kept commit cannot be squashed or fixed up; choose Pick, Reword or Edit first.";
                return [];
            }

            kept = true;
        }

        // The loop above intentionally validates before rebuilding, so the returned entries
        // need a second pass to apply the canonical metadata.
        var canonical = entries.Select(entry =>
        {
            var original = source[entry.Sha];
            return entry with { Subject = original.Subject, Parents = original.Parents };
        }).ToArray();

        return canonical;
    }

    private static RebaseAction? FindCurrentStepAction(
        string? currentCommit,
        IReadOnlyList<RebaseTodoEntry> completed)
    {
        if (currentCommit is null) return null;
        var current = completed.LastOrDefault(entry =>
            string.Equals(entry.Sha, currentCommit, StringComparison.OrdinalIgnoreCase) ||
            entry.Sha.StartsWith(currentCommit, StringComparison.OrdinalIgnoreCase) ||
            currentCommit.StartsWith(entry.Sha, StringComparison.OrdinalIgnoreCase));
        return current?.Action;
    }

    private static string ToTodoLine(RebaseTodoEntry entry)
    {
        var action = entry.Action switch
        {
            RebaseAction.Pick => "pick",
            RebaseAction.Reword => "reword",
            RebaseAction.Edit => "edit",
            RebaseAction.Squash => "squash",
            RebaseAction.Fixup => "fixup",
            RebaseAction.Drop => "drop",
            _ => "pick",
        };

        var subject = entry.Subject.Replace('\r', ' ').Replace('\n', ' ');
        return $"{action} {entry.Sha} {subject}".TrimEnd();
    }

    private static IReadOnlyList<RebaseTodoEntry> ParsePlan(string output)
    {
        var entries = new List<RebaseTodoEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split(Separator, 6);
            if (fields.Length < 3 || !HistoryService.IsObjectId(fields[0])) continue;

            entries.Add(new RebaseTodoEntry
            {
                Sha = fields[0],
                Parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Subject = fields[2],
            });
        }

        return entries;
    }

    private static IReadOnlyList<RebaseTodoEntry> ParseTodoFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var entries = new List<RebaseTodoEntry>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !IsObjectIdToken(parts[1])) continue;

                if (!Enum.TryParse<RebaseAction>(parts[0], ignoreCase: true, out var action))
                    continue;

                entries.Add(new RebaseTodoEntry
                {
                    Action = action,
                    Sha = parts[1],
                    Subject = parts.Length > 2 ? parts[2] : "",
                });
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<RebaseTodoEntry>> ReadTodoFileAsync(
        string worktreePath, string path, CancellationToken ct)
    {
        var parsed = ParseTodoFile(path);
        if (parsed.Count == 0) return parsed;

        // Git can abbreviate todo ids when `core.abbrev` is configured. Resolve only those
        // short tokens; full ids are already canonical and do not need another process.
        var resolved = await Task.WhenAll(parsed.Select(async entry =>
        {
            if (HistoryService.IsObjectId(entry.Sha)) return entry;

            var result = await git.TryRunAsync(
                    worktreePath, ct, "rev-parse", "--verify", $"{entry.Sha}^{{commit}}")
                .ConfigureAwait(false);
            return result.Success && HistoryService.IsObjectId(result.Trimmed)
                ? entry with { Sha = result.Trimmed }
                : entry;
        })).ConfigureAwait(false);

        return resolved;
    }

    private async Task<string?> ResolveGitDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct, "rev-parse", "--absolute-git-dir")
            .ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0
            ? RepoPaths.ToPlatform(result.Trimmed)
            : null;
    }

    private async Task<string?> ReadHeadAsync(
        string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
            .ConfigureAwait(false);
        return result.Success && HistoryService.IsObjectId(result.Trimmed)
            ? result.Trimmed
            : null;
    }

    private async Task<string?> ReadBranchAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct, "symbolic-ref", "--quiet", "--short", "HEAD")
            .ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    private async Task<string?> ReadSubjectAsync(
        string worktreePath, string sha, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct, "show", "-s", "--format=%s", sha)
            .ConfigureAwait(false);
        return result.Success ? result.Trimmed : null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadBranchName(string path)
    {
        var value = ReadText(path);
        if (value is null) return null;
        const string prefix = "refs/heads/";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static void EnsureObjectId(string value, string label)
    {
        if (!HistoryService.IsObjectId(value))
            throw new ArgumentException($"{label} must be a full git object id.", nameof(value));
    }

    /// <summary>
    /// Todo files written by Git normally contain full ids, but repositories can opt into
    /// abbreviated instructions. State is read-only diagnostic data, so accepting a safe
    /// hexadecimal abbreviation is preferable to silently hiding the stopped step.
    /// </summary>
    private static bool IsObjectIdToken(string value) =>
        HistoryService.IsObjectId(value) ||
        // Git permits abbreviated instructions (and can be configured below the usual
        // seven-character display width). Four hex characters is its minimum unambiguous
        // abbreviation; rev-parse below decides whether a particular token resolves.
        value.Length is >= 4 and <= 64 && value.All(IsAsciiHex);

    private static bool IsAsciiHex(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static GitMutation Refused(
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

    private sealed class RebaseSession(
        string previousHead, string? branch, EditorFiles editors) : IDisposable
    {
        public string PreviousHead { get; } = previousHead;
        public string? Branch { get; } = branch;
        public EditorFiles Editors { get; } = editors;

        public void Dispose() => Editors.Dispose();
    }

    /// <summary>
    /// Temporary editors used only for the lifetime of one Git invocation.
    ///
    /// Git's normal editor is intentionally disabled by <see cref="GitCli"/>. Rebase needs
    /// two controlled prompts: one to install the todo and one for reword/continue messages.
    /// Keeping both scripts outside the repository avoids untracked files and lets cleanup
    /// happen even when the sequencer stops on a conflict.
    /// </summary>
    private sealed class EditorFiles : IDisposable
    {
        private readonly string _directory;
        private readonly string _overridePath;
        private string? _overrideCommit;

        public required IReadOnlyDictionary<string, string?> Environment { get; init; }

        private EditorFiles(string directory)
        {
            _directory = directory;
            _overridePath = Path.Combine(directory, "override.txt");
        }

        public static async Task<EditorFiles> CreateAsync(
            string todo,
            IReadOnlyList<RebaseTodoEntry> entries,
            CancellationToken ct,
            string message = "")
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "chapter-rebase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var todoPath = Path.Combine(directory, "todo.txt");
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(todoPath, todo, utf8, ct).ConfigureAwait(false);

                var messagesDirectory = Path.Combine(directory, "messages");
                Directory.CreateDirectory(messagesDirectory);
                var queuedMessages = BuildMessageQueue(entries);
                for (var index = 0; index < queuedMessages.Count; index++)
                {
                    var queued = queuedMessages[index];
                    if (queued.Length > 0)
                    {
                        await File.WriteAllTextAsync(
                                Path.Combine(messagesDirectory, $"{index}.txt"), queued, utf8, ct)
                            .ConfigureAwait(false);
                    }
                }

                await File.WriteAllTextAsync(Path.Combine(directory, "index.txt"), "0\n", Encoding.ASCII, ct)
                    .ConfigureAwait(false);

                if (message.Length > 0)
                    await File.WriteAllTextAsync(Path.Combine(directory, "override.txt"), message, utf8, ct)
                        .ConfigureAwait(false);

                var sequence = await WriteScriptAsync(
                        directory, "sequence", target => CopyCommand(todoPath, target))
                    .ConfigureAwait(false);
                var editor = await WriteScriptAsync(
                        directory, "editor", target => MessageQueueCommand(directory, target))
                    .ConfigureAwait(false);

                return new EditorFiles(directory)
                {
                    Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["GIT_SEQUENCE_EDITOR"] = QuoteForEditor(sequence),
                        ["GIT_EDITOR"] = QuoteForEditor(editor),
                    },
                };
            }
            catch
            {
                TryDelete(directory);
                throw;
            }
        }

        public void Dispose() => TryDelete(_directory);

        public void QueueOverride(string message, string? currentCommit)
        {
            try
            {
                File.WriteAllText(_overridePath, message, new UTF8Encoding(false));
                _overrideCommit = currentCommit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Git will report the editor failure if the queue cannot be updated.
            }
        }

        public void PrepareForStep(string? currentCommit)
        {
            if (!File.Exists(_overridePath))
            {
                _overrideCommit = null;
                return;
            }

            if (_overrideCommit is null || currentCommit is null ||
                !string.Equals(_overrideCommit, currentCommit, StringComparison.OrdinalIgnoreCase))
                ClearPendingOverride();
        }

        public void ClearPendingOverride()
        {
            try
            {
                if (File.Exists(_overridePath)) File.Delete(_overridePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleanup is best effort; Git reports an editor failure if the file is locked.
            }

            _overrideCommit = null;
        }

        private static IReadOnlyList<string> BuildMessageQueue(
            IReadOnlyList<RebaseTodoEntry> entries)
        {
            var queue = new List<string>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.Action is RebaseAction.Reword)
                {
                    queue.Add(entry.Message);
                    continue;
                }

                if (entry.Action is not RebaseAction.Squash) continue;

                // Git opens one editor for a contiguous squash/fixup chain. There can be
                // several `squash` rows in that chain, but only one resulting message; use
                // the last non-empty explicit message so an intentional later replacement
                // is not silently ignored (while an empty later field preserves an earlier
                // choice).
                var message = entry.Message;
                index++;
                while (index < entries.Count && entries[index].Action is RebaseAction.Squash or RebaseAction.Fixup)
                {
                    if (entries[index].Action is RebaseAction.Squash && entries[index].Message.Length > 0)
                        message = entries[index].Message;
                    index++;
                }
                index--;
                queue.Add(message);
            }

            return queue;
        }

        private static async Task<string> WriteScriptAsync(
            string directory, string name, Func<string, string> body)
        {
            var windows = OperatingSystem.IsWindows();
            var path = Path.Combine(directory, windows ? name + ".cmd" : name + ".sh");
            var content = windows
                ? "@echo off\n" + body("%~1") + "\nexit /b 0\n"
                : "#!/bin/sh\n" + body("\"$1\"") + "\nexit 0\n";

            await File.WriteAllTextAsync(path, content, Encoding.ASCII).ConfigureAwait(false);
            if (!windows)
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (PlatformNotSupportedException) { }
            }

            return path;
        }

        private static string CopyCommand(string source, string target) => OperatingSystem.IsWindows()
            ? $"copy /Y \"{source}\" \"{target}\" >nul"
            : $"cp -- {ShellQuote(source)} {target}";

        private static string MessageQueueCommand(string directory, string target)
        {
            var messages = Path.Combine(directory, "messages");
            var overridePath = Path.Combine(directory, "override.txt");

            if (OperatingSystem.IsWindows())
            {
                var indexPath = $"\"{Path.Combine(directory, "index.txt")}\"";
                var quotedMessages = $"\"{messages}\\!idx!.txt\"";
                var quotedOverride = $"\"{overridePath}\"";
                var quotedTarget = $"\"{target}\"";
                return "setlocal EnableExtensions EnableDelayedExpansion\r\n" +
                       $"set \"idx=0\" & set /p \"idx=\"< {indexPath}\r\n" +
                       $"if exist {quotedOverride} (copy /Y {quotedOverride} {quotedTarget} >nul & del /Q {quotedOverride}) else if exist {quotedMessages} copy /Y {quotedMessages} {quotedTarget} >nul\r\n" +
                       "set /a idx+=1 >nul\r\n" +
                       $"> {indexPath} echo !idx!\r\n" +
                       "exit /b 0";
            }

            var index = ShellQuote(Path.Combine(directory, "index.txt"));
            var unixMessages = ShellQuote(messages);
            var unixOverride = ShellQuote(overridePath);
            return $"idx=$(cat -- {index} 2>/dev/null || printf '0'); " +
                   $"if [ -f {unixOverride} ]; then cp -- {unixOverride} {target}; rm -f -- {unixOverride}; " +
                   $"elif [ -f {unixMessages}/$idx.txt ]; then cp -- {unixMessages}/$idx.txt {target}; fi; " +
                   $"idx=$((idx + 1)); printf '%s\\n' \"$idx\" > {index}";
        }

        private static string QuoteForEditor(string path) => OperatingSystem.IsWindows()
            ? $"\"{path}\""
            : ShellQuote(path);

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stranded helper is harmless and is preferable to masking Git's result.
            }
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }
}
