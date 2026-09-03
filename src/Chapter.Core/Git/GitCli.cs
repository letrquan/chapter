using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
    private static readonly Regex UriCredentials = new(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<userinfo>[^/\s'""<>?\\#]*)@(?<host>[^/\s'""<>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScpCredentials = new(
        @"(?<![\w@])(?<userinfo>[^\s/:\\]+:[^\s@/:\\]+)@(?<host>[^\s/:\\]+):(?<path>[^\s]*)",
        RegexOptions.Compiled);

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
    /// Enabled by default for network commands now that remote operations are exposed. It
    /// remains a switch so hosts and tests that must be completely non-interactive can turn
    /// credential-manager UI off explicitly.
    /// </summary>
    public bool AllowCredentialPrompts { get; set; } = true;

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
    public Task<GitResult> ExecuteAsync(
        string workingDirectory, GitIntent intent, CancellationToken ct, params string[] args) =>
        ExecuteWithEnvironmentAsync(workingDirectory, intent, null, ct, args);

    /// <summary>
    /// Runs a companion command (for example <c>gh</c>) with the same safe process
    /// plumbing as git. Keeping this here means external git tools cannot accidentally
    /// inherit a shell, an interactive terminal, or an unbounded process lifetime.
    /// </summary>
    public Task<GitResult> ExecuteExternalAsync(
        string executable,
        string workingDirectory,
        GitIntent intent,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        params string[] args) =>
        ExecuteCoreAsync(workingDirectory, intent, environment, ct, executable, args, onOutput: null);

    /// <summary>
    /// Runs a command with a small set of per-invocation environment overrides.
    ///
    /// Git's sequence editor is intentionally disabled for ordinary calls, because an
    /// editor prompt has nowhere to go in the desktop host. Interactive rebase is the one
    /// operation that needs a controlled editor, so it supplies a temporary script here
    /// rather than changing the process-wide environment or bypassing <see cref="GitWriter"/>.
    /// A null value removes an inherited variable.
    /// </summary>
    public Task<GitResult> ExecuteWithEnvironmentAsync(
        string workingDirectory,
        GitIntent intent,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        params string[] args) =>
        ExecuteCoreAsync(workingDirectory, intent, environment, ct, GitPath, args, onOutput: null);

    /// <summary>Streaming counterpart to <see cref="ExecuteExternalAsync"/>.</summary>
    public Task<GitResult> ExecuteExternalStreamingAsync(
        string executable,
        string workingDirectory,
        GitIntent intent,
        Action<GitOutputChunk>? onOutput,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        params string[] args) =>
        ExecuteCoreAsync(workingDirectory, intent, environment, ct, executable, args, onOutput);

    private async Task<GitResult> ExecuteCoreAsync(
        string workingDirectory,
        GitIntent intent,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        string executable,
        string[] args,
        Action<GitOutputChunk>? onOutput)
    {
        var psi = CreateStartInfo(executable, workingDirectory, intent, args, utf8Stdout: true, environment);
        var commandLine = DescribeCommand(executable, args);

        using var process = Start(psi, commandLine, executable);
        process.StandardInput.Close();

        // Read both streams concurrently. Reading them in sequence deadlocks as soon as
        // the other stream's pipe buffer fills, which large diffs do routinely.
        if (onOutput is not null)
        {
            var streamedStdout = new StringBuilder();
            var streamedStderr = new StringBuilder();
            var streamedStdoutTask = ReadChunksAsync(process.StandardOutput, GitOutputStream.StandardOutput, streamedStdout, onOutput, ct);
            var streamedStderrTask = ReadChunksAsync(process.StandardError, GitOutputStream.StandardError, streamedStderr, onOutput, ct);

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                await Task.WhenAll(streamedStdoutTask, streamedStderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await Task.WhenAll(streamedStdoutTask, streamedStderrTask).ConfigureAwait(false); }
                catch { }
                throw;
            }

            return new GitResult(commandLine, process.ExitCode, streamedStdout.ToString(), streamedStderr.ToString());
        }

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
    /// Runs git while forwarding output as it arrives.
    ///
    /// Git deliberately writes transfer progress to stderr (and rewrites the same line with
    /// carriage returns), so waiting for <c>ReadToEndAsync</c> makes a fetch or push look
    /// frozen until it is already over. The complete streams are still returned for the
    /// mutation result; the callback is only the live view of them.
    /// </summary>
    public async Task<GitResult> ExecuteStreamingAsync(
        string workingDirectory,
        GitIntent intent,
        Action<GitOutputChunk>? onOutput,
        CancellationToken ct,
        params string[] args)
    {
        var psi = CreateStartInfo(GitPath, workingDirectory, intent, args, utf8Stdout: true, environment: null);
        var commandLine = DescribeCommand(GitPath, args);

        using var process = Start(psi, commandLine, GitPath);
        process.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = ReadChunksAsync(process.StandardOutput, GitOutputStream.StandardOutput, stdout, onOutput, ct);
        var stderrTask = ReadChunksAsync(process.StandardError, GitOutputStream.StandardError, stderr, onOutput, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            // Let both readers observe the closed pipes before disposing the process. Their
            // exceptions are secondary to the cancellation and must not mask it.
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { }

            throw;
        }

        return new GitResult(commandLine, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs git and returns stdout as raw bytes. Needed for file content, which may be
    /// any encoding or genuinely binary; decoding as UTF-8 would corrupt it.
    /// </summary>
    public async Task<GitBytesResult> RunBytesAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = CreateStartInfo(GitPath, workingDirectory, GitIntent.Read, args, utf8Stdout: false);
        var commandLine = DescribeCommand(GitPath, args);

        using var process = Start(psi, commandLine, GitPath);
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
        string executable,
        string workingDirectory,
        GitIntent intent,
        string[] args,
        bool utf8Stdout,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo(executable)
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

        // Companion tools such as `gh` do not understand git's global `-c` options.
        // Only prepend them when this invocation is actually git.
        if (string.Equals(executable, GitPath, StringComparison.OrdinalIgnoreCase))
            foreach (var a in GlobalArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        ApplyEnvironment(psi, intent);

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
        }

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

    private Process Start(ProcessStartInfo psi, string commandLine, string executable)
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
            // Use the executable that was actually requested. External tools such as `gh`
            // share this process wrapper, and blaming the configured git binary makes a
            // missing companion CLI needlessly difficult to diagnose.
            throw new GitException(commandLine, -1, $"Failed to start '{executable}': {ex.Message}");
        }
    }

    internal static string DescribeCommand(string[] args) => DescribeCommand("git", args);

    internal static string DescribeCommand(string executable, string[] args) =>
        $"{Path.GetFileNameWithoutExtension(executable)} {string.Join(' ', args.Select(RedactArgument))}";

    /// <summary>
    /// Keeps credentials embedded in a remote URL out of mutation results and the persistent
    /// operation log. Git accepts URLs with userinfo, and a remote-add command is otherwise a
    /// surprisingly easy way to write a token to disk forever.
    /// </summary>
    internal static string RedactArgument(string argument) => RedactText(argument);

    /// <summary>
    /// Removes embedded credentials from arbitrary git output too.
    ///
    /// Authentication failures commonly echo the URL. That text becomes the mutation's
    /// user-facing message and is persisted in the operation log, so redacting command
    /// arguments alone is not enough.
    /// </summary>
    internal static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var redacted = UriCredentials.Replace(text, match =>
            $"{match.Groups["scheme"].Value}***@{match.Groups["host"].Value}");
        return ScpCredentials.Replace(redacted, "***@${host}:${path}");
    }

    private static async Task ReadChunksAsync(
        StreamReader reader,
        GitOutputStream stream,
        StringBuilder destination,
        Action<GitOutputChunk>? onOutput,
        CancellationToken ct)
    {
        var buffer = new char[4096];

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;

            var text = new string(buffer, 0, count);
            destination.Append(text);

            if (onOutput is null) continue;

            try
            {
                onOutput(new GitOutputChunk(stream, text));
            }
            catch
            {
                // Progress is observational. A UI subscriber must never kill the git process.
            }
        }
    }

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

public enum GitOutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record GitOutputChunk(GitOutputStream Stream, string Text);

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
