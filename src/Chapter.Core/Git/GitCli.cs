using System.Diagnostics;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>
/// What an invocation is for. This is not decoration: the environment a read wants is
/// actively wrong for a write, so every call has to declare which it is.
/// </summary>
public enum GitIntent
{
    /// <summary>Inspects the repository and changes nothing.</summary>
    Read,

    /// <summary>Mutates the repository locally — index, refs, working tree.</summary>
    Write,

    /// <summary>Talks to a remote, and so may need credentials.</summary>
    Network,
}

/// <summary>
/// Thin async wrapper around git.exe. We shell out rather than use LibGit2Sharp because
/// libgit2's linked-worktree support has historically lagged git itself, and worktrees
/// are the whole point of this app.
/// </summary>
public sealed class GitCli(string gitPath = "git")
{
    /// <summary>
    /// Arguments prepended to every invocation.
    /// <c>core.quotepath=false</c> stops git escaping non-ASCII path bytes as \303\251,
    /// which would otherwise corrupt any path outside ASCII.
    /// </summary>
    private static readonly string[] GlobalArgs =
    [
        "--no-pager",
        "-c", "core.quotepath=false",
        "-c", "color.ui=false",
    ];

    /// <summary>
    /// Stand-in for an editor. Git launches one for a commit or merge message whenever no
    /// message was supplied, and this process has no console to host it, so the launch has
    /// to fail rather than block: a git waiting forever on an editor that will never appear
    /// is indistinguishable from a hang. The name is deliberately ours, so the resulting
    /// "cannot run" message points at Chapter rather than looking like a broken git.
    /// </summary>
    private const string NoEditor = "chapter-supplies-no-editor";

    public string GitPath { get; } = gitPath;

    /// <summary>
    /// Whether <see cref="GitIntent.Network"/> commands may raise a credential prompt.
    ///
    /// False everywhere today, which is correct while nothing pushes or fetches. Phase 5
    /// turns it on: until then a network command that needs credentials fails with an
    /// opaque error instead of asking, and this is the single switch that changes it.
    /// </summary>
    public bool AllowCredentialPrompts { get; set; }

    /// <summary>Runs a read-only git command, returning stdout and throwing if it fails.</summary>
    public async Task<string> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var result = await TryRunAsync(workingDirectory, ct, args).ConfigureAwait(false);
        if (!result.Success)
            throw new GitException(result.CommandLine, result.ExitCode, result.StandardError);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs a read-only git command and returns the full result without throwing. Use for
    /// commands where a non-zero exit is a legitimate answer — <c>merge-base</c> on
    /// unrelated histories, <c>symbolic-ref</c> when origin/HEAD is unset, and so on.
    /// </summary>
    public Task<GitResult> TryRunAsync(string workingDirectory, CancellationToken ct, params string[] args) =>
        ExecuteAsync(workingDirectory, GitIntent.Read, ct, args);

    /// <summary>
    /// Runs a git command under an intent's environment. Mutations must come through here
    /// (or <see cref="GitWriter"/>, which wraps it) rather than <see cref="TryRunAsync"/>:
    /// a write executed with the read environment cannot take <c>index.lock</c>, so it
    /// fails or silently does nothing.
    /// </summary>
    public async Task<GitResult> ExecuteAsync(
        string workingDirectory, GitIntent intent, CancellationToken ct, params string[] args)
    {
        var psi = CreateStartInfo(workingDirectory, intent, args, utf8Stdout: true);
        var commandLine = DescribeCommand(args);

        using var process = Start(psi, commandLine);
        process.StandardInput.Close();

        // Read both streams concurrently. Reading them in sequence deadlocks as soon as
        // the other stream's pipe buffer fills, which large diffs do routinely.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new GitResult(commandLine, process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Runs git and returns stdout as raw bytes. Needed for file content, which may be
    /// any encoding or genuinely binary; decoding as UTF-8 would corrupt it.
    /// </summary>
    public async Task<GitBytesResult> RunBytesAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = CreateStartInfo(workingDirectory, GitIntent.Read, args, utf8Stdout: false);
        var commandLine = DescribeCommand(args);

        using var process = Start(psi, commandLine);
        process.StandardInput.Close();

        using var buffer = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await copyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        return new GitBytesResult(commandLine, process.ExitCode, buffer.ToArray(), stderr);
    }

    private ProcessStartInfo CreateStartInfo(
        string workingDirectory, GitIntent intent, string[] args, bool utf8Stdout)
    {
        var psi = new ProcessStartInfo(GitPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Left unset for byte reads: forcing an encoding there would decode the very bytes
        // the caller asked for raw.
        if (utf8Stdout) psi.StandardOutputEncoding = Encoding.UTF8;

        foreach (var a in GlobalArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        ApplyEnvironment(psi, intent);
        return psi;
    }

    /// <summary>
    /// Sets the environment for an intent. The split exists because the read settings are
    /// not merely unhelpful for writes, they break them.
    ///
    /// Every variable below is either assigned or explicitly removed, never just left
    /// alone. <see cref="ProcessStartInfo.Environment"/> starts out as a copy of *this*
    /// process's environment, so "not setting it" does not mean "unset" — it means
    /// "whatever launched Chapter decided". Agent harnesses and other git GUIs export
    /// exactly these variables, so a leaked <c>GIT_OPTIONAL_LOCKS=0</c> would put every
    /// mutation back under the restriction this method exists to lift, silently and only
    /// for the users who launch the app that way.
    /// </summary>
    private void ApplyEnvironment(ProcessStartInfo psi, GitIntent intent)
    {
        // Reads must not take index.lock. Browsing a worktree an agent is working in
        // otherwise makes the app the thing that breaks the agent's `git add`: status and
        // diff refresh the index as a side effect, and that refresh takes the lock.
        //
        // A write must not run under the same restriction. The flag does not stop git
        // locking when it has no choice — `add` and `commit` still do — but it does stop
        // git writing back the index it refreshed, so a mutation runs against stat data git
        // was forbidden to update. That leaves "is anything actually staged" answered from
        // a stale cache, which is the wrong footing for a command whose entire purpose is
        // to change what is staged.
        if (intent is GitIntent.Read) psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        else psi.Environment.Remove("GIT_OPTIONAL_LOCKS");

        psi.Environment["GIT_EDITOR"] = NoEditor;
        psi.Environment["GIT_SEQUENCE_EDITOR"] = NoEditor;

        // Terminal prompting stays off regardless of intent: the process has no console, so
        // a git that decides to ask on the terminal would block on a device nobody can
        // answer.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Credential Manager is the one that matters, because it opens its own window.
        // Removing the variable is what permits that — setting it to anything else would
        // not, and leaving it alone would inherit whatever the parent chose.
        var mayPrompt = intent is GitIntent.Network && AllowCredentialPrompts;
        if (mayPrompt) psi.Environment.Remove("GCM_INTERACTIVE");
        else psi.Environment["GCM_INTERACTIVE"] = "never";
    }

    private Process Start(ProcessStartInfo psi, string commandLine)
    {
        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            return process;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new GitException(commandLine, -1, $"Failed to start '{GitPath}': {ex.Message}");
        }
    }

    private static string DescribeCommand(string[] args) => $"git {string.Join(' ', args)}";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process died on its own between the check and the kill. Nothing to do.
        }
    }
}

public sealed record GitResult(string CommandLine, int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>Stdout with the single trailing newline git appends removed.</summary>
    public string Trimmed => StandardOutput.TrimEnd('\n', '\r');
}

public sealed record GitBytesResult(string CommandLine, int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed class GitException(string commandLine, int exitCode, string stderr)
    : Exception($"{commandLine} failed with exit code {exitCode}: {stderr.Trim()}")
{
    public string CommandLine { get; } = commandLine;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = stderr;
}
