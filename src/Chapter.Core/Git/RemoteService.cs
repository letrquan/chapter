using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Chapter.Core.Git;

/// <summary>A configured git remote and the URLs git will use for it.</summary>
public sealed record Remote
{
    public required string Name { get; init; }
    public string FetchUrl { get; init; } = "";
    public string PushUrl { get; init; } = "";
}

/// <summary>The strategy git should use when a pull has to integrate remote work.</summary>
public enum PullStrategy
{
    Merge,
    Rebase,
    FastForwardOnly,
}

/// <summary>How a remote operation is reported over the progress channel.</summary>
public sealed record RemoteProgress
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }
    public required string Operation { get; init; }

    /// <summary><c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</summary>
    public required string State { get; init; }
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Percent { get; init; }
    public GitMutation? Mutation { get; init; }
}

/// <summary>The id returned when a long-running remote operation is accepted.</summary>
public sealed record RemoteOperationStarted(string Id, string WorktreePath, string Operation);

/// <summary>What <c>remote prune --dry-run</c> says it would delete.</summary>
public sealed record RemotePrunePreview
{
    public required string Remote { get; init; }
    public bool Ok { get; init; }
    public IReadOnlyList<string> Refs { get; init; } = [];
    public string Message { get; init; } = "";

    public static RemotePrunePreview Failed(string remote, string message) =>
        new() { Remote = remote, Ok = false, Message = message };
}

/// <summary>One ref a push would update, as the remote reported it to <c>--dry-run</c>.</summary>
public sealed record PushRefUpdate
{
    public required string From { get; init; }
    public required string To { get; init; }
    public bool IsForced { get; init; }
    public bool IsRejected { get; init; }

    /// <summary>A ref the push would delete, which git reports as a success, not a refusal.</summary>
    public bool IsDeleted { get; init; }
    public string Summary { get; init; } = "";
    public string OldSha { get; init; } = "";
    public string NewSha { get; init; } = "";

    /// <summary>Commits the remote has now and would not have afterwards. Forced updates only.</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    /// <summary>
    /// The old tip is not an object in this repository, so what it holds cannot be listed.
    /// Only reachable when the lease was refused: a passing lease means the old tip is the
    /// tracking ref, which is by definition local.
    /// </summary>
    public bool DroppedUnknown { get; init; }
}

/// <summary>What a push would do, asked of the remote before anything is sent.</summary>
public sealed record PushPreview
{
    public bool Ok { get; init; }
    public IReadOnlyList<PushRefUpdate> Updates { get; init; } = [];
    public string Message { get; init; } = "";

    public static PushPreview Failed(string message) => new() { Ok = false, Message = message };
}

/// <summary>
/// Reads configured remotes and runs network operations.
///
/// Network work is deliberately split into two layers. The command methods are ordinary
/// async methods and are easy to exercise in fixture repositories; the <see cref="Start"/>
/// methods detach them from the bridge and report their progress by id, which keeps a slow
/// fetch or push from sitting behind the bridge's request timeout.
/// </summary>
public sealed class RemoteService(GitCli git, GitWriter writer)
{
    private static readonly Regex PercentPattern = new(@"(?<!\d)(\d{1,3})%(?!\d)", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, RunningOperation> _running = new();
    private readonly object _reserveGate = new();

    public event Action<RemoteProgress>? Progress;
    public event Action<RemoteProgress>? Finished;

    /// <summary>Lists remotes in git's configured order.</summary>
    public async Task<IReadOnlyList<Remote>> ListAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "remote", "-v").ConfigureAwait(false);
        return result.Success
            ? [.. Parse(result.StandardOutput).Select(Redact)]
            : [];
    }

    private static Remote Redact(Remote remote) => remote with
    {
        FetchUrl = GitCli.RedactArgument(remote.FetchUrl),
        PushUrl = GitCli.RedactArgument(remote.PushUrl),
    };

    /// <summary>Parses <c>git remote -v</c>, retaining one fetch and one push URL per remote.</summary>
    internal static IReadOnlyList<Remote> Parse(string output)
    {
        var remotes = new Dictionary<string, (string? Fetch, string? Push)>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // The format is "name<TAB>url (fetch)". URLs can contain spaces, so split only
            // at the first whitespace after the name rather than using a broad token split.
            var separator = line.IndexOfAny(['\t', ' ']);
            if (separator <= 0) continue;

            var name = line[..separator];
            var rest = line[(separator + 1)..].Trim();
            var kindStart = rest.LastIndexOf(" (", StringComparison.Ordinal);
            if (kindStart <= 0 || !rest.EndsWith(')')) continue;

            var url = rest[..kindStart].Trim();
            var kind = rest[(kindStart + 2)..^1];
            if (url.Length == 0 || kind is not ("fetch" or "push")) continue;

            if (!remotes.TryGetValue(name, out var current))
            {
                current = (null, null);
                order.Add(name);
            }

            remotes[name] = kind == "fetch"
                ? (url, current.Push)
                : (current.Fetch, url);
        }

        return [.. order.Select(name => new Remote
        {
            Name = name,
            FetchUrl = remotes[name].Fetch ?? remotes[name].Push ?? "",
            PushUrl = remotes[name].Push ?? remotes[name].Fetch ?? "",
        })];
    }

    // ---------------------------------------------------------------------
    // Local remote configuration
    // ---------------------------------------------------------------------

    public Task<GitMutation> AddAsync(
        string worktreePath, string name, string url, CancellationToken ct = default)
    {
        var invalid = ValidateRemote(name, "remote");
        invalid ??= ValidateUrl(url);
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"add remote {name}", invalid));

        return writer.RunAsync(
            worktreePath, $"add remote {name}", WriteKind.WorkingTree, ct,
            ["remote", "add", name, url.Trim()]);
    }

    public Task<GitMutation> RenameAsync(
        string worktreePath, string from, string to, CancellationToken ct = default)
    {
        var invalid = ValidateRemote(from, "remote");
        invalid ??= ValidateRemote(to, "remote");
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"rename remote {from}", invalid));

        return writer.RunAsync(
            worktreePath, $"rename remote {from} to {to}", WriteKind.WorkingTree, ct,
            ["remote", "rename", from, to]);
    }

    public Task<GitMutation> RemoveAsync(
        string worktreePath, string name, CancellationToken ct = default)
    {
        var invalid = ValidateRemote(name, "remote");
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"remove remote {name}", invalid));

        return writer.RunAsync(
            worktreePath, $"remove remote {name}", WriteKind.WorkingTree, ct,
            ["remote", "remove", name]);
    }

    public Task<GitMutation> PruneAsync(
        string worktreePath, string name, CancellationToken ct = default)
    {
        var invalid = ValidateRemote(name, "remote");
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"prune remote {name}", invalid));

        return writer.RunAsync(
            worktreePath, $"prune remote {name}", WriteKind.WorkingTree, ct,
            ["remote", "prune", name]);
    }

    // ---------------------------------------------------------------------
    // Previews
    //
    // Both of these talk to the server, which makes them network commands that happen to
    // write nothing. They run under GitIntent.Network rather than the read path — the read
    // environment forbids a credential prompt, and a preview that can only succeed against
    // a public remote is a preview that fails exactly when the action is worth checking.
    // They stay out of the writer, and so out of the operation log, which records what the
    // app did rather than what it asked about.
    // ---------------------------------------------------------------------

    /// <summary>
    /// What <c>remote prune</c> would delete, from <c>--dry-run</c> rather than from the
    /// app's own idea of which tracking refs are stale.
    /// </summary>
    public async Task<RemotePrunePreview> PreviewPruneAsync(
        string worktreePath, string name, CancellationToken ct = default)
    {
        var invalid = ValidateRemote(name, "remote");
        if (invalid is not null) return RemotePrunePreview.Failed(name, invalid);

        var result = await git.ExecuteAsync(
            worktreePath, GitIntent.Network, ct, "remote", "prune", name.Trim(), "--dry-run")
            .ConfigureAwait(false);

        return result.Success
            ? new RemotePrunePreview { Remote = name, Ok = true, Refs = ParsePrunePreview(result.StandardOutput) }
            : RemotePrunePreview.Failed(name, GitCli.RedactText(result.StandardError).Trim());
    }

    /// <summary>
    /// What a push would do to the remote, asked of the remote itself.
    ///
    /// The value is not the ref names — the caller already knows those — but the old tip.
    /// A forced update names the commits the server currently has and would stop having,
    /// which is the only part of a force push that cannot be undone from this machine.
    /// </summary>
    public async Task<PushPreview> PreviewPushAsync(
        string worktreePath, string remote = "", string branch = "",
        bool forceWithLease = false, CancellationToken ct = default)
    {
        var invalid = OptionalRemote(remote);
        invalid ??= OptionalBranch(branch, "a push branch");
        if (invalid is not null) return PushPreview.Failed(invalid);

        // Identical to the real push apart from --dry-run and the porcelain format: a
        // preview of different arguments is a preview of a different operation.
        List<string> args = ["push", "--porcelain", "--dry-run"];
        if (forceWithLease) args.Add("--force-with-lease");
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote.Trim());
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch.Trim());

        var result = await git.ExecuteAsync(worktreePath, GitIntent.Network, ct, [.. args])
            .ConfigureAwait(false);

        var updates = ParsePushPreview(result.StandardOutput + "\n" + result.StandardError);
        if (updates.Count == 0 && !result.Success)
            return PushPreview.Failed(GitCli.RedactText(result.StandardError).Trim());

        var described = new List<PushRefUpdate>(updates.Count);
        foreach (var update in updates)
        {
            // Only a forced update destroys anything, and only then is it worth naming what.
            // When the lease holds, the old tip is the tracking ref, so it is an object this
            // repository already has; a rejected push may name one it has never seen.
            if (!update.IsForced || update.OldSha.Length == 0 || update.NewSha.Length == 0)
            {
                described.Add(update);
                continue;
            }

            var log = await git.TryRunAsync(
                worktreePath, ct,
                "log", "--format=%h %s", "--no-decorate", "-n", "50",
                update.OldSha, "--not", update.NewSha)
                .ConfigureAwait(false);

            described.Add(update with
            {
                Dropped = log.Success
                    ? log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [],
                DroppedUnknown = !log.Success,
            });
        }

        return new PushPreview { Ok = true, Updates = described };
    }

    /// <summary>Reads <c>* [would prune] origin/name</c>, and nothing else it prints.</summary>
    internal static IReadOnlyList<string> ParsePrunePreview(string output)
    {
        const string marker = "* [would prune] ";
        var refs = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;

            var name = line[(at + marker.Length)..].Trim();
            if (name.Length > 0) refs.Add(name);
        }

        return refs;
    }

    /// <summary>
    /// Reads git's push porcelain: <c>&lt;flag&gt;\t&lt;from&gt;:&lt;to&gt;\t&lt;summary&gt;</c>.
    ///
    /// The flag is the whole of what distinguishes the outcomes that matter — a space for a
    /// fast-forward, <c>+</c> for a forced update, <c>-</c> for a deleted ref, <c>*</c> for a
    /// new one, <c>!</c> for a refusal, <c>=</c> for up to date — and the summary carries the
    /// old and new tips in <c>a..b</c> or <c>a...b</c> form.
    /// </summary>
    internal static IReadOnlyList<PushRefUpdate> ParsePushPreview(string output)
    {
        var updates = new List<PushRefUpdate>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '\t') continue;

            var flag = line[0];
            if (flag is not (' ' or '+' or '-' or '*' or '!' or '=')) continue;

            var rest = line[2..].Split('\t');
            if (rest.Length == 0) continue;

            var refs = rest[0].Split(':');
            var summary = rest.Length > 1 ? rest[1].Trim() : "";
            var (oldSha, newSha) = SplitRange(summary);

            updates.Add(new PushRefUpdate
            {
                From = refs[0],
                To = refs.Length > 1 ? refs[1] : refs[0],
                IsForced = flag == '+',
                // Only '!' is a refusal. '-' is a *successfully deleted* ref, and reading it
                // as a rejection told the user the server had refused a delete that git had
                // just confirmed it would make — checked against git rather than remembered.
                IsRejected = flag == '!',
                IsDeleted = flag == '-',
                Summary = summary,
                OldSha = oldSha,
                NewSha = newSha,
            });
        }

        return updates;
    }

    /// <summary>Splits <c>a..b</c> or <c>a...b</c>; anything else has no range to report.</summary>
    private static (string Old, string New) SplitRange(string summary)
    {
        var token = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var at = token.IndexOf("..", StringComparison.Ordinal);
        if (at <= 0) return ("", "");

        var after = at + 2;
        if (after < token.Length && token[after] == '.') after++;
        if (after >= token.Length) return ("", "");

        return (token[..at], token[after..]);
    }

    // ---------------------------------------------------------------------
    // Network commands (awaitable core)
    // ---------------------------------------------------------------------

    public Task<GitMutation> FetchAsync(
        string worktreePath, string remote = "", bool prune = false,
        bool all = false,
        Action<GitOutputChunk>? onOutput = null, CancellationToken ct = default)
    {
        var invalid = OptionalRemote(remote);
        if (invalid is not null)
            return Task.FromResult(Refused(worktreePath, "fetch", invalid));

        List<string> args = ["fetch", "--progress"];
        if (prune) args.Add("--prune");
        if (all) args.Add("--all");
        else if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote.Trim());

        // Unleased: a fetch writes remote-tracking refs and nothing this worktree's index or
        // files depend on, and it can run for minutes. See RunUnleasedStreamingAsync.
        return writer.RunUnleasedStreamingAsync(
            worktreePath, "fetch", WriteKind.WorkingTree, GitIntent.Network,
            onOutput, ct, [.. args]);
    }

    // Keep the original streaming overload shape available to callers that fetch one named
    // remote; the `all` switch is only needed by the UI's Fetch all footer action.
    public Task<GitMutation> FetchAsync(
        string worktreePath, string remote, bool prune,
        Action<GitOutputChunk>? onOutput, CancellationToken ct = default) =>
        FetchAsync(worktreePath, remote, prune, false, onOutput, ct);

    public Task<GitMutation> PullAsync(
        string worktreePath, PullStrategy strategy, string remote = "", string branch = "",
        Action<GitOutputChunk>? onOutput = null, CancellationToken ct = default)
    {
        var invalid = OptionalRemote(remote);
        invalid ??= OptionalBranch(branch, "a pull branch");
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, "pull", invalid));

        List<string> args = ["pull", "--progress"];
        args.Add(strategy switch
        {
            PullStrategy.Rebase => "--rebase",
            PullStrategy.FastForwardOnly => "--ff-only",
            _ => "--no-rebase",
        });

        // A non-fast-forward merge otherwise asks for a commit message through the editor.
        // Chapter has no terminal editor; use git's generated message instead.
        if (strategy is PullStrategy.Merge) args.Add("--no-edit");

        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote.Trim());
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch.Trim());

        return writer.RunStreamingAsync(
            worktreePath, "pull", WriteKind.StartsOperation, GitIntent.Network,
            onOutput, ct, [.. args]);
    }

    public Task<GitMutation> PushAsync(
        string worktreePath, string remote = "", string branch = "",
        bool forceWithLease = false, bool setUpstream = false,
        Action<GitOutputChunk>? onOutput = null, CancellationToken ct = default)
    {
        var invalid = OptionalRemote(remote);
        invalid ??= OptionalBranch(branch, "a push branch");
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, "push", invalid));

        List<string> args = ["push", "--progress"];
        if (forceWithLease) args.Add("--force-with-lease");
        if (setUpstream) args.Add("--set-upstream");
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote.Trim());
        if (!string.IsNullOrWhiteSpace(branch)) args.Add(branch.Trim());

        // Unleased for the same reason as fetch, and more plainly: a push changes the
        // server, not this checkout. Two pushes cannot overlap anyway — RemoteService
        // reserves the worktree before either starts.
        return writer.RunUnleasedStreamingAsync(
            worktreePath, forceWithLease ? "force push" : "push",
            WriteKind.WorkingTree, GitIntent.Network, onOutput, ct, [.. args]);
    }

    public Task<GitMutation> PushTagAsync(
        string worktreePath, string remote, string tag,
        Action<GitOutputChunk>? onOutput = null, CancellationToken ct = default)
    {
        // Required rather than optional, unlike fetch/pull/push: the remote is a positional
        // argument here, so an empty one becomes an empty argv entry and git answers
        // "'' does not appear to be a git repository" instead of the app saying what is missing.
        var invalid = ValidateRemote(remote, "remote");
        invalid ??= BranchService.Validate(tag)?.Replace("branch", "tag", StringComparison.Ordinal);
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"push tag {tag}", invalid));

        return writer.RunUnleasedStreamingAsync(
            worktreePath, $"push tag {tag}", WriteKind.WorkingTree, GitIntent.Network,
            onOutput, ct, "push", "--progress", remote.Trim(), $"refs/tags/{tag}");
    }

    // ---------------------------------------------------------------------
    // Detached bridge operations
    // ---------------------------------------------------------------------

    public RemoteOperationStarted StartFetch(
        string worktreePath, string remote = "", bool prune = false, bool all = false)
    {
        var id = Reserve(worktreePath, "fetch");
        var output = CreateOutput(id, worktreePath, "fetch");
        _ = RunDetached(id, worktreePath, "fetch", token => FetchAsync(
            worktreePath, remote, prune, all, output.Push, token), output.Flush);
        return new RemoteOperationStarted(id, worktreePath, "fetch");
    }

    public RemoteOperationStarted StartPull(
        string worktreePath, PullStrategy strategy, string remote = "", string branch = "")
    {
        var id = Reserve(worktreePath, "pull");
        var output = CreateOutput(id, worktreePath, "pull");
        _ = RunDetached(id, worktreePath, "pull", token => PullAsync(
            worktreePath, strategy, remote, branch, output.Push, token), output.Flush);
        return new RemoteOperationStarted(id, worktreePath, "pull");
    }

    public RemoteOperationStarted StartPush(
        string worktreePath, string remote = "", string branch = "",
        bool forceWithLease = false, bool setUpstream = false)
    {
        var operation = forceWithLease ? "forcePush" : "push";
        var id = Reserve(worktreePath, operation);
        var output = CreateOutput(id, worktreePath, operation);
        _ = RunDetached(id, worktreePath, operation,
            token => PushAsync(worktreePath, remote, branch, forceWithLease, setUpstream,
                output.Push, token), output.Flush);
        return new RemoteOperationStarted(id, worktreePath, operation);
    }

    public RemoteOperationStarted StartPushTag(string worktreePath, string remote, string tag)
    {
        var id = Reserve(worktreePath, "pushTag");
        var output = CreateOutput(id, worktreePath, "pushTag");
        _ = RunDetached(id, worktreePath, "pushTag",
            token => PushTagAsync(worktreePath, remote, tag, output.Push, token), output.Flush);
        return new RemoteOperationStarted(id, worktreePath, "pushTag");
    }

    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out var operation)) return false;
        try
        {
            operation.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void CancelAll()
    {
        foreach (var operation in _running.Values)
        {
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private string Reserve(string worktreePath, string operation)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new RunningOperation(id, worktreePath, operation, new CancellationTokenSource());

        // A repository's refs are shared by linked worktrees. Refuse overlapping operations
        // from the same path at minimum; the dispatcher also serialises the common UI path.
        // The check and insertion are one critical section: a pair of simultaneous clicks
        // must not both observe an empty dictionary and then launch competing transfers.
        lock (_reserveGate)
        {
            if (_running.Values.Any(r => string.Equals(r.WorktreePath, worktreePath, StringComparison.OrdinalIgnoreCase)))
            {
                entry.Cancellation.Dispose();
                throw new InvalidOperationException("A remote operation is already running for this worktree.");
            }

            if (!_running.TryAdd(id, entry))
            {
                entry.Cancellation.Dispose();
                throw new InvalidOperationException("Could not start the remote operation.");
            }
        }

        return id;
    }

    private async Task RunDetached(
        string id, string worktreePath, string operation,
        Func<CancellationToken, Task<GitMutation>> run,
        Action? flush = null)
    {
        if (!_running.TryGetValue(id, out var running))
        {
            // Reserve always adds before this is scheduled, so this is defensive. It still
            // reports a terminal state rather than returning in silence: the caller already
            // holds an operation id, and a start with no finish leaves the progress strip
            // running forever and Reserve refusing every later fetch for this worktree.
            RaiseFinished(new RemoteProgress
            {
                Id = id,
                WorktreePath = worktreePath,
                Operation = operation,
                State = "failed",
                Phase = "failed",
                Message = $"Could not {operation}: the operation was no longer reserved.",
            });
            return;
        }

        GitMutation mutation;
        try
        {
            mutation = await run(running.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            mutation = CancelledMutation(worktreePath, operation);
        }
        catch (Exception ex)
        {
            mutation = new GitMutation
            {
                Operation = operation,
                WorktreePath = worktreePath,
                CommandLine = "",
                ExitCode = -1,
                Failure = GitFailure.Unknown,
                Detail = $"Could not {operation}: {ex.Message}",
                Attempts = 0,
            };
        }
        finally
        {
            try { flush?.Invoke(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Remote progress flush failed: {ex.Message}");
            }
        }

        // A cancellation racing the final process exit must not hide a completed push or
        // fetch. The mutation's result is authoritative once git has returned successfully;
        // the token only decides a non-successful operation.
        var state = mutation.Failure is GitFailure.Cancelled
            ? "cancelled"
            : mutation.Success
                ? "completed"
                : running.Cancellation.IsCancellationRequested ? "cancelled" : "failed";

        _running.TryRemove(id, out _);
        try { running.Cancellation.Dispose(); }
        catch (ObjectDisposedException) { }

        RaiseFinished(new RemoteProgress
        {
            Id = id,
            WorktreePath = worktreePath,
            Operation = operation,
            State = state,
            Phase = state,
            Message = mutation.Message,
            Percent = state == "completed" ? 100 : null,
            Mutation = mutation,
        });
    }

    private ProgressOutput CreateOutput(string id, string worktreePath, string operation) =>
        new(message => RaiseProgress(id, worktreePath, operation, GitCli.RedactText(message)));

    private void RaiseProgress(string id, string worktreePath, string operation, string message)
    {
        var percent = PercentPattern.Match(message);
        var value = percent.Success && int.TryParse(percent.Groups[1].Value, out var parsed)
            ? Math.Clamp(parsed, 0, 100)
            : (int?)null;

        var phase = message;
        var colon = message.IndexOf(':');
        if (colon > 0) phase = message[..colon].Trim();
        if (phase.StartsWith("remote", StringComparison.OrdinalIgnoreCase)) phase = "remote";

        try
        {
            Progress?.Invoke(new RemoteProgress
            {
                Id = id,
                WorktreePath = worktreePath,
                Operation = operation,
                State = "running",
                Phase = phase,
                Message = message,
                Percent = value,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Remote progress subscriber failed: {ex.Message}");
        }
    }

    private void RaiseFinished(RemoteProgress progress)
    {
        try { Finished?.Invoke(progress); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Remote completion subscriber failed: {ex.Message}");
        }
    }

    private static GitMutation CancelledMutation(string worktreePath, string operation) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.Cancelled,
        Detail = $"{operation} cancelled",
        Attempts = 0,
    };

    private static string? OptionalRemote(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : ValidateRemote(value.Trim(), "remote");

    private static string? OptionalBranch(string value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Optionish(value)
                ? $"{what} cannot begin with a dash"
                : value.Any(char.IsControl)
                    ? $"{what} cannot contain control characters"
                    : null;

    private static string? ValidateUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "a remote needs a URL";
        if (Optionish(value)) return "a remote URL cannot begin with a dash";
        return value.Any(char.IsControl) ? "a remote URL cannot contain control characters" : null;
    }

    private static string? ValidateRemote(string value, string noun)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"a {noun} needs a name";
        if (value != value.Trim()) return $"a {noun} name cannot start or end with a space";
        if (Optionish(value)) return $"a {noun} name cannot begin with a dash";
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
            return $"a {noun} name cannot contain control characters";
        return null;
    }

    private static bool Optionish(string value) => value.TrimStart().StartsWith("-", StringComparison.Ordinal);

    private static GitMutation Refused(string worktreePath, string operation, string reason) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.Unknown,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    };

    private sealed record RunningOperation(
        string Id, string WorktreePath, string Operation, CancellationTokenSource Cancellation);

    private sealed class ProgressOutput(Action<string> report)
    {
        private readonly ProgressLineParser _parser = new((_, message) => report(message));

        public void Push(GitOutputChunk chunk) => _parser.Push(chunk);

        public void Flush() => _parser.Flush();
    }
}
