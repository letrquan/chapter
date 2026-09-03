using System.Diagnostics;
using Chapter.Core.Diagnostics;

namespace Chapter.Core.Git;

/// <summary>
/// The single path through which the app mutates a repository.
///
/// Everything a write needs and a read does not lives here: the right environment, the
/// retry through an agent's <c>index.lock</c>, a classified failure rather than an exit
/// code, a line in the operation log, and a window during which the watcher knows the
/// change was ours. Routing one mutation around this class loses all five at once, so
/// there is deliberately no other way to run a mutating command.
/// </summary>
public sealed class GitWriter(GitCli git, OperationLog log)
{
    /// <summary>
    /// How long to wait between attempts when another process holds the lock.
    ///
    /// Tuned for the case this app exists for: an agent running <c>git add</c> in the
    /// worktree the user is reviewing. That finishes in well under a second, so the five
    /// attempts below cover it without making a genuinely stuck lock take long to report.
    /// </summary>
    private static readonly TimeSpan[] LockBackoff =
    [
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(750),
    ];

    public OperationLog Log { get; } = log;

    /// <summary>
    /// Supplies a scope marking writes as the app's own, so the watcher does not report
    /// them back as if an agent had made them. Set by whoever owns the watcher; when null,
    /// mutations still work and simply produce the redundant refresh.
    /// </summary>
    public Func<string, IDisposable>? SelfWriteScope { get; set; }

    /// <summary>
    /// Consulted before every mutation. Returning a blocked guard stops the command from
    /// running at all, which is the difference between "you cannot start a merge during a
    /// rebase" and git's version of that sentence arriving after the fact.
    ///
    /// It is handed the <see cref="WriteKind"/> because the answer depends on it: the same
    /// repository state that forbids starting a merge is the state <c>git add</c> exists to
    /// clear.
    /// </summary>
    public Func<string, WriteKind, CancellationToken, Task<WriteGuard>>? Guard { get; set; }

    /// <summary>
    /// Called after every mutation that actually reached git, successful or not.
    ///
    /// This is what keeps the <see cref="Guard"/>'s cached answer honest. The guard reads
    /// repository state, that state is cached to keep per-hunk staging from spawning four
    /// git processes per click, and a commit or a <c>merge --abort</c> is precisely what
    /// makes the cached reading wrong. Invalidating from the caller instead was the first
    /// attempt and it leaks: every future call site has to remember, and the one that
    /// forgets gets a guard answering from a state the repository left minutes ago.
    ///
    /// Failure is not an exception — a merge that stopped on conflicts changed the
    /// repository as much as one that succeeded.
    /// </summary>
    public Action<string>? Mutated { get; set; }

    /// <summary>Runs a local mutation that stages, discards or commits.</summary>
    public Task<GitMutation> RunAsync(
        string worktreePath, string operation, CancellationToken ct, params string[] args) =>
        RunAsync(worktreePath, operation, WriteKind.WorkingTree, GitIntent.Write, ct, args);

    /// <summary>Runs a local mutation of a stated kind.</summary>
    public Task<GitMutation> RunAsync(
        string worktreePath, string operation, WriteKind kind, CancellationToken ct, params string[] args) =>
        RunAsync(worktreePath, operation, kind, GitIntent.Write, ct, args);

    /// <summary>
    /// Runs a mutation under an explicit intent. <see cref="GitIntent.Network"/> is the
    /// reason this overload exists: fetch and push need the credential environment, and
    /// they are not otherwise different from any other write.
    /// </summary>
    public async Task<GitMutation> RunAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        CancellationToken ct, params string[] args)
        => await RunCoreAsync(worktreePath, operation, kind, intent, null, null, git.GitPath,
            repositoryLease: true, ct, args)
            .ConfigureAwait(false);

    /// <summary>
    /// Runs a guarded mutation with per-process environment overrides.
    ///
    /// This is intentionally a narrow escape hatch rather than a public alternate write
    /// path: the guard, lock retry, watcher scope and operation log remain exactly the same.
    /// Interactive rebase uses it to point Git at a temporary sequence-editor helper.
    /// </summary>
    public Task<GitMutation> RunWithEnvironmentAsync(
        string worktreePath,
        string operation,
        WriteKind kind,
        GitIntent intent,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct,
        params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, null, environment, git.GitPath,
            repositoryLease: true, ct, args);

    /// <summary>
    /// Runs a mutation while forwarding git's output as it arrives.
    ///
    /// The ordinary path remains the default so local mutations do not pay for a streaming
    /// reader. Remote operations use this overload to surface transfer progress while still
    /// retaining the writer's guard, lock retry, self-write and operation-log guarantees.
    /// </summary>
    public Task<GitMutation> RunStreamingAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        Action<GitOutputChunk>? onOutput, CancellationToken ct, params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, onOutput, null, git.GitPath,
            repositoryLease: true, ct, args);

    /// <summary>
    /// Runs a streaming mutation that does not take the repository lease.
    ///
    /// For the two network commands that cannot change this worktree: a fetch writes
    /// remote-tracking refs and a push writes nothing locally at all. Both legitimately run
    /// for minutes, and the lease is deliberately short — two seconds and then a refusal —
    /// so holding it across a transfer turned every stage, discard or commit made during a
    /// push into "another Chapter instance is writing this repository", with one window open
    /// and nothing at risk. Overlapping *remote* operations are still refused, by
    /// RemoteService's own per-worktree reservation. A pull is not in this set: it merges or
    /// rebases into the working tree, which is exactly what the lease is for.
    /// </summary>
    public Task<GitMutation> RunUnleasedStreamingAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        Action<GitOutputChunk>? onOutput, CancellationToken ct, params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, onOutput, null, git.GitPath,
            repositoryLease: false, ct, args);

    /// <summary>
    /// Runs a companion CLI through the same guarded mutation path as git. GitHub CLI is
    /// intentionally not folded into <see cref="GitCli.GitPath"/>: it has its own command
    /// vocabulary, but still mutates the checkout for actions such as <c>pr checkout</c>.
    /// </summary>
    public Task<GitMutation> RunExternalAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        string executable, CancellationToken ct, params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, null, null, executable,
            repositoryLease: true, ct, args);

    /// <summary>External-command overload with explicit environment hardening.</summary>
    public Task<GitMutation> RunExternalAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        string executable, IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct, params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, null, environment, executable,
            repositoryLease: true, ct, args);

    /// <summary>Streaming counterpart for a long-running companion command.</summary>
    public Task<GitMutation> RunExternalStreamingAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        string executable, Action<GitOutputChunk>? onOutput, CancellationToken ct,
        params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, onOutput, null, executable,
            repositoryLease: true, ct, args);

    /// <summary>Streaming external-command overload with explicit environment hardening.</summary>
    public Task<GitMutation> RunExternalStreamingAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        string executable, Action<GitOutputChunk>? onOutput,
        IReadOnlyDictionary<string, string?> environment, CancellationToken ct,
        params string[] args) =>
        RunCoreAsync(worktreePath, operation, kind, intent, onOutput, environment, executable,
            repositoryLease: true, ct, args);

    /// <summary>
    /// Runs a mutation while a caller-owned repository lease is held. Composite operations
    /// such as discard need to delete working-tree paths and update the index as one unit;
    /// taking the non-reentrant lease again would either deadlock or leave a race between
    /// those two halves.
    /// </summary>
    internal async Task<GitMutation> RunUnderLeaseAsync(
        RepositoryWriteLease lease,
        string worktreePath,
        string operation,
        WriteKind kind,
        GitIntent intent,
        CancellationToken ct,
        params string[] args)
    {
        _ = lease; // Ownership is held by the caller for the duration of this task.
        if (Guard is not null)
        {
            var guard = await Guard(worktreePath, kind, ct).ConfigureAwait(false);
            if (!guard.Allowed)
                return Refused(worktreePath, operation, args, guard.Reason, git.GitPath);
        }

        return await ExecuteAndClassifyAsync(
            worktreePath, operation, kind, intent, onOutput: null, environment: null,
            executable: git.GitPath, ct, args).ConfigureAwait(false);
    }

    private async Task<GitMutation> RunCoreAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        Action<GitOutputChunk>? onOutput,
        IReadOnlyDictionary<string, string?>? environment,
        string executable,
        bool repositoryLease,
        CancellationToken ct,
        params string[] args)
    {
        if (!repositoryLease)
        {
            if (Guard is not null)
            {
                var open = await Guard(worktreePath, kind, ct).ConfigureAwait(false);
                if (!open.Allowed)
                    return Refused(worktreePath, operation, args, open.Reason, executable);
            }

            return await ExecuteAndClassifyAsync(
                worktreePath, operation, kind, intent, onOutput, environment,
                executable, ct, args).ConfigureAwait(false);
        }

        // Git's index.lock protects one low-level write, not two Chapter windows both
        // deciding from the same stale branch/stash snapshot. Hold a short repository-wide
        // lease around the complete command, including its result classification, so our
        // own instances serialize before Git gets a chance to race them.
        var attempt = await RepositoryWriteLock.TryAcquireAsync(git, worktreePath, ct)
            .ConfigureAwait(false);
        if (attempt.Lease is null)
        {
            return Refused(
                worktreePath,
                operation,
                args,
                attempt.BusyInThisProcess
                    ? "this window is already writing to this repository — wait for that to finish"
                    : "another Chapter instance is writing this repository — try again",
                executable,
                GitFailure.Locked);
        }

        using (attempt.Lease)
        {
            // Re-read the guard after waiting. Another Chapter window may have changed the
            // operation state while this call was queued; checking before the lease would
            // let a stale "allowed" answer start a rebase/checkout after that change.
            if (Guard is not null)
            {
                var guard = await Guard(worktreePath, kind, ct).ConfigureAwait(false);
                if (!guard.Allowed)
                    return Refused(worktreePath, operation, args, guard.Reason, executable);
            }

            return await ExecuteAndClassifyAsync(
                worktreePath, operation, kind, intent, onOutput, environment,
                executable, ct, args).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the command and turns its result into a mutation: retry on lock contention,
    /// failure classification, watcher scope, operation log. Says nothing about the
    /// repository lease — one caller holds it, one was handed it, and one runs without it.
    /// </summary>
    private async Task<GitMutation> ExecuteAndClassifyAsync(
        string worktreePath, string operation, WriteKind kind, GitIntent intent,
        Action<GitOutputChunk>? onOutput,
        IReadOnlyDictionary<string, string?>? environment,
        string executable,
        CancellationToken ct,
        params string[] args)
    {

        using var scope = SelfWriteScope?.Invoke(worktreePath);

        var stopwatch = Stopwatch.StartNew();
        GitResult result;
        var attempt = 0;

        try
        {
            while (true)
            {
                attempt++;
                // Spelled out rather than nested in a ternary. A trailing .ConfigureAwait
                // binds to the branch it sits on, so the git arms — the ones almost every
                // mutation takes — were awaited with context capture while only the external
                // arms were not. That is a continuation onto the WPF dispatcher while this
                // task holds both the per-repository semaphore and the cross-process lease.
                var isGit = string.Equals(executable, git.GitPath, StringComparison.OrdinalIgnoreCase);

                if (onOutput is null)
                {
                    result = isGit
                        ? await git.ExecuteWithEnvironmentAsync(worktreePath, intent, environment, ct, args)
                            .ConfigureAwait(false)
                        : await git.ExecuteExternalAsync(executable, worktreePath, intent, environment, ct, args)
                            .ConfigureAwait(false);
                }
                else
                {
                    result = isGit
                        ? await git.ExecuteStreamingAsync(worktreePath, intent, onOutput, ct, args)
                            .ConfigureAwait(false)
                        : await git.ExecuteExternalStreamingAsync(
                            executable, worktreePath, intent, onOutput, environment, ct, args)
                            .ConfigureAwait(false);
                }

                if (result.Success) break;

                var failure = GitFailureClassifier.Classify(result.StandardError, result.StandardOutput);
                if (failure is not GitFailure.Locked || attempt > LockBackoff.Length) break;

                await Task.Delay(LockBackoff[attempt - 1], ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && onOutput is not null)
        {
            stopwatch.Stop();
            NotifyMutated(worktreePath);

            var cancelled = new GitMutation
            {
                Operation = operation,
                WorktreePath = worktreePath,
                CommandLine = GitCli.DescribeCommand(executable, args),
                ExitCode = -1,
                Failure = GitFailure.Cancelled,
                Detail = $"{operation} cancelled",
                Attempts = attempt,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };

            Record(cancelled);
            return cancelled;
        }

        stopwatch.Stop();

        // Before the mutation is built, because BuildAsync asks git about the lock holder and
        // any state read on the way there must not come from the pre-mutation cache.
        NotifyMutated(worktreePath);

        var mutation = await BuildAsync(
            worktreePath, operation, result, attempt, stopwatch.ElapsedMilliseconds, executable, ct).ConfigureAwait(false);

        Record(mutation);
        return mutation;
    }

    /// <summary>
    /// Tells the owner the repository moved, without letting a subscriber turn a completed
    /// mutation into a reported failure — by this point git has already done the work.
    /// </summary>
    private void NotifyMutated(string worktreePath)
    {
        try
        {
            Mutated?.Invoke(worktreePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mutation subscriber failed: {ex.Message}");
        }
    }

    private async Task<GitMutation> BuildAsync(
        string worktreePath, string operation, GitResult result, int attempts, long elapsedMs,
        string executable, CancellationToken ct)
    {
        if (result.Success)
        {
            return new GitMutation
            {
                Operation = operation,
                WorktreePath = worktreePath,
                CommandLine = result.CommandLine,
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                Attempts = attempts,
                ElapsedMs = elapsedMs,
            };
        }

        var failure = GitFailureClassifier.Classify(result.StandardError, result.StandardOutput);
        var detail = failure is GitFailure.Locked
            ? await DescribeLockAsync(worktreePath, operation, result.StandardError, ct).ConfigureAwait(false)
            : null;

        return new GitMutation
        {
            Operation = operation,
            WorktreePath = worktreePath,
            CommandLine = result.CommandLine,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Failure = failure,
            // Anything the app can say better than git says it. For everything else the
            // detail stays null so git's own words come through unaltered — a wrong
            // paraphrase is worse than a terse original.
            Detail = detail,
            Attempts = attempts,
            ElapsedMs = elapsedMs,
        };
    }

    /// <summary>
    /// Turns "Unable to create '…/index.lock': File exists" into a sentence naming the
    /// process responsible, which is the one thing the user can act on.
    /// </summary>
    private async Task<string> DescribeLockAsync(
        string worktreePath, string operation, string stderr, CancellationToken ct)
    {
        var lockPath = GitLock.PathFromStderr(stderr);

        if (lockPath is null)
        {
            var gitDir = await ResolveGitDirAsync(worktreePath, ct).ConfigureAwait(false);
            if (gitDir is not null) lockPath = GitLock.IndexLockPath(gitDir);
        }

        var baseMessage = GitFailureClassifier.Describe(GitFailure.Locked, operation);
        if (lockPath is null) return baseMessage;

        var info = GitLock.Describe(lockPath);
        var name = Path.GetFileName(lockPath);

        return $"{baseMessage} — {name} {info.Summary}";
    }

    private async Task<string?> ResolveGitDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0 ? RepoPaths.ToPlatform(result.Trimmed) : null;
    }

    /// <summary>
    /// A mutation the guard stopped. It never ran, so there is no exit code from git —
    /// but it still belongs in the log, because "why did nothing happen" is exactly the
    /// question the log exists to answer.
    /// </summary>
    private GitMutation Refused(
        string worktreePath,
        string operation,
        string[] args,
        string? reason,
        string executable,
        GitFailure? failure = null)
    {
        var mutation = new GitMutation
        {
            Operation = operation,
            WorktreePath = worktreePath,
            CommandLine = GitCli.DescribeCommand(executable, args),
            ExitCode = -1,
            // The guard blocks for three different reasons and the classification has to
            // follow, or "there are unresolved conflicts" is reported to the UI as an
            // operation in progress and offered the wrong way out.
            Failure = failure ?? ClassifyRefusal(reason),
            Detail = reason is null ? $"Could not {operation}" : $"Could not {operation}: {reason}",
            Attempts = 0,
        };

        Record(mutation);
        return mutation;
    }

    private static GitFailure ClassifyRefusal(string? reason) => reason switch
    {
        null => GitFailure.Unknown,
        var r when r.Contains("conflict", StringComparison.OrdinalIgnoreCase) => GitFailure.Conflict,
        var r when r.Contains("could not be read", StringComparison.OrdinalIgnoreCase) => GitFailure.Unknown,
        _ => GitFailure.OperationInProgress,
    };

    private void Record(GitMutation mutation) => Log.Append(new OperationLogEntry
    {
        Timestamp = DateTimeOffset.Now,
        Operation = mutation.Operation,
        WorktreePath = mutation.WorktreePath,
        CommandLine = mutation.CommandLine,
        ExitCode = mutation.ExitCode,
        ElapsedMs = mutation.ElapsedMs,
        Attempts = mutation.Attempts,
        Failure = mutation.Failure is GitFailure.None ? null : mutation.Failure.ToString(),
        Detail = mutation.Success ? null : mutation.Message,
    });
}
