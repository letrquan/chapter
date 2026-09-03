using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Chapter.Core.Git;

/// <summary>A clone operation accepted before the git process starts.</summary>
public sealed record CloneOperationStarted(string Id, string Source, string Destination);

/// <summary>Progress and terminal state for a detached clone.</summary>
public sealed record CloneProgress
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required string State { get; init; }
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Percent { get; init; }
    public GitMutation? Mutation { get; init; }
}

/// <summary>
/// Runs <c>git clone</c> outside the bridge request and reports progress by id.
///
/// Clone is unlike the other git mutations: there is no existing worktree to guard and no
/// writer to invalidate, but the process can legitimately run for minutes. The destination
/// is validated before launch, then the same GitCli cancellation and credential environment
/// are used for the transfer.
/// </summary>
public sealed class CloneService(GitCli git, Chapter.Core.Diagnostics.OperationLog log)
{
    private static readonly Regex PercentPattern = new(
        @"(?<!\d)(\d{1,3})%(?!\d)", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, RunningClone> _running = new();
    private readonly object _gate = new();

    public event Action<CloneProgress>? Progress;
    public event Action<CloneProgress>? Finished;

    public CloneOperationStarted Start(
        string source, string destination, bool bare = false, bool recursive = true)
    {
        var invalid = Validate(source, destination);
        if (invalid is not null) throw new ArgumentException(invalid);

        var fullDestination = Path.GetFullPath(destination.Trim());
        var id = Guid.NewGuid().ToString("N");
        var running = new RunningClone(id, source.Trim(), fullDestination, new CancellationTokenSource());

        lock (_gate)
        {
            if (_running.Values.Any(value =>
                    string.Equals(value.Destination, fullDestination, StringComparison.OrdinalIgnoreCase)))
            {
                running.Cancellation.Dispose();
                throw new InvalidOperationException("A clone is already running for that destination.");
            }

            _running[id] = running;
        }

        _ = RunDetached(running, bare, recursive);
        return new CloneOperationStarted(id, GitCli.RedactText(running.Source), fullDestination);
    }

    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out var clone)) return false;
        try
        {
            clone.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void CancelAll()
    {
        foreach (var clone in _running.Values)
        {
            try { clone.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal static string? Validate(string? source, string? destination)
    {
        if (string.IsNullOrWhiteSpace(source)) return "a repository URL or path is required";
        if (source.Any(char.IsControl)) return "the repository URL or path cannot contain control characters";
        if (source.TrimStart().StartsWith("-", StringComparison.Ordinal))
            return "the repository URL or path cannot begin with a dash";
        if (string.IsNullOrWhiteSpace(destination)) return "a destination folder is required";
        if (destination.Any(char.IsControl)) return "the destination cannot contain control characters";

        try
        {
            var full = Path.GetFullPath(destination.Trim());
            if (Directory.Exists(full) || File.Exists(full)) return "the destination already exists";
            var parent = Directory.GetParent(full)?.FullName;
            if (parent is null || !Directory.Exists(parent)) return "the destination's parent folder does not exist";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the destination is not a valid local path";
        }

        return null;
    }

    private async Task RunDetached(RunningClone clone, bool bare, bool recursive)
    {
        GitMutation mutation;
        var stopwatch = Stopwatch.StartNew();
        ProgressLineParser? parser = null;
        var args = new List<string> { "clone", "--progress" };
        if (bare) args.Add("--bare");
        // Both directions are explicit. git clone does not recurse by default, so omitting
        // the flag left "Include submodules" — which is checked by default — doing nothing at
        // all; and passing the negative form only when unchecked would still inherit a user's
        // submodule.recurse=true config for the other branch.
        args.Add(recursive ? "--recurse-submodules" : "--no-recurse-submodules");
        // `--` keeps a local path beginning with a dash from becoming an option after the
        // validation above, and makes the boundary explicit in the operation log.
        args.Add("--");
        args.Add(clone.Source);
        args.Add(clone.Destination);

        try
        {
            Raise(new CloneProgress
            {
                Id = clone.Id,
                Source = GitCli.RedactText(clone.Source),
                Destination = clone.Destination,
                State = "running",
                Phase = "starting",
                Message = "Starting clone…",
            });

            var parent = Directory.GetParent(clone.Destination)?.FullName ?? Environment.CurrentDirectory;
            parser = new ProgressLineParser((_, message) => RaiseProgress(clone, message));
            var result = await git.ExecuteExternalStreamingAsync(
                git.GitPath, parent, GitIntent.Network, parser.Push, environment: null,
                clone.Cancellation.Token, [.. args]).ConfigureAwait(false);

            var failure = result.Success
                ? GitFailure.None
                : GitFailureClassifier.Classify(result.StandardError, result.StandardOutput);
            mutation = new GitMutation
            {
                Operation = "clone",
                WorktreePath = clone.Destination,
                CommandLine = result.CommandLine,
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                Failure = failure,
                Attempts = 1,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            mutation = new GitMutation
            {
                Operation = "clone",
                WorktreePath = clone.Destination,
                CommandLine = GitCli.DescribeCommand(git.GitPath, [.. args]),
                ExitCode = -1,
                Failure = GitFailure.Cancelled,
                Detail = "clone cancelled",
                Attempts = 1,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (GitException ex)
        {
            var stderr = GitCli.RedactText(ex.StandardError);
            mutation = new GitMutation
            {
                Operation = "clone",
                WorktreePath = clone.Destination,
                CommandLine = ex.CommandLine,
                ExitCode = ex.ExitCode,
                StandardError = stderr,
                Failure = GitFailureClassifier.Classify(stderr),
                Detail = $"Could not clone: {stderr.Trim()}",
                Attempts = 1,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            mutation = new GitMutation
            {
                Operation = "clone",
                WorktreePath = clone.Destination,
                CommandLine = GitCli.DescribeCommand(git.GitPath, [.. args]),
                ExitCode = -1,
                Failure = GitFailure.Unknown,
                Detail = $"Could not clone: {GitCli.RedactText(ex.Message)}",
                Attempts = 1,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            // Git may close a stream with a status line that has no trailing newline. Flush
            // on every terminal path, including cancellation and process-start failures.
            parser?.Flush();
        }

        stopwatch.Stop();

        var state = mutation.Failure == GitFailure.Cancelled
            ? "cancelled"
            : mutation.Success
                ? "completed"
                : clone.Cancellation.IsCancellationRequested ? "cancelled" : "failed";

        _running.TryRemove(clone.Id, out _);
        try { clone.Cancellation.Dispose(); }
        catch (ObjectDisposedException) { }

        log.Append(new Chapter.Core.Diagnostics.OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = "clone",
            WorktreePath = clone.Destination,
            CommandLine = mutation.CommandLine,
            ExitCode = mutation.ExitCode,
            Attempts = mutation.Attempts,
            Failure = mutation.Failure == GitFailure.None ? null : mutation.Failure.ToString(),
            Detail = mutation.Success ? null : mutation.Message,
        });

        RaiseFinished(new CloneProgress
        {
            Id = clone.Id,
            Source = GitCli.RedactText(clone.Source),
            Destination = clone.Destination,
            State = state,
            Phase = state,
                Message = mutation.Failure == GitFailure.Cancelled
                    ? $"{mutation.Message}. Partial files may remain in the destination."
                    : mutation.Message,
            Percent = state == "completed" ? 100 : null,
            Mutation = mutation,
        });
    }

    private void RaiseProgress(RunningClone clone, string message)
    {
        message = GitCli.RedactText(message);
        var percent = PercentPattern.Match(message);
        var value = percent.Success && int.TryParse(percent.Groups[1].Value, out var parsed)
            ? Math.Clamp(parsed, 0, 100)
            : (int?)null;

        var phase = message;
        var colon = message.IndexOf(':');
        if (colon > 0) phase = message[..colon].Trim();
        if (phase.StartsWith("remote", StringComparison.OrdinalIgnoreCase)) phase = "remote";

        Raise(new CloneProgress
        {
            Id = clone.Id,
            Source = GitCli.RedactText(clone.Source),
            Destination = clone.Destination,
            State = "running",
            Phase = phase,
            Message = message,
            Percent = value,
        });
    }

    private void Raise(CloneProgress progress)
    {
        try { Progress?.Invoke(progress); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clone progress subscriber failed: {ex.Message}"); }
    }

    private void RaiseFinished(CloneProgress progress)
    {
        try { Finished?.Invoke(progress); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clone completion subscriber failed: {ex.Message}"); }
    }

    private sealed record RunningClone(
        string Id, string Source, string Destination, CancellationTokenSource Cancellation);
}
