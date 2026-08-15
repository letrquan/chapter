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
    {
        if (Guard is not null)
        {
            var guard = await Guard(worktreePath, kind, ct).ConfigureAwait(false);
            if (!guard.Allowed) return Refused(worktreePath, operation, args, guard.Reason);
        }

        using var scope = SelfWriteScope?.Invoke(worktreePath);

        var stopwatch = Stopwatch.StartNew();
        GitResult result;
        var attempt = 0;

        while (true)
        {
            attempt++;
            result = await git.ExecuteAsync(worktreePath, intent, ct, args).ConfigureAwait(false);

            if (result.Success) break;

            var failure = GitFailureClassifier.Classify(result.StandardError, result.StandardOutput);
            if (failure is not GitFailure.Locked || attempt > LockBackoff.Length) break;

            await Task.Delay(LockBackoff[attempt - 1], ct).ConfigureAwait(false);
        }

        stopwatch.Stop();

        var mutation = await BuildAsync(
            worktreePath, operation, result, attempt, stopwatch.ElapsedMilliseconds, ct).ConfigureAwait(false);

        Record(mutation);
        return mutation;
    }

    private async Task<GitMutation> BuildAsync(
        string worktreePath, string operation, GitResult result, int attempts, long elapsedMs, CancellationToken ct)
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
    private GitMutation Refused(string worktreePath, string operation, string[] args, string? reason)
    {
        var mutation = new GitMutation
        {
            Operation = operation,
            WorktreePath = worktreePath,
            CommandLine = $"git {string.Join(' ', args)}",
            ExitCode = -1,
            // The guard blocks for three different reasons and the classification has to
            // follow, or "there are unresolved conflicts" is reported to the UI as an
            // operation in progress and offered the wrong way out.
            Failure = ClassifyRefusal(reason),
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
