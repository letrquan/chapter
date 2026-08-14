using System.Diagnostics;
using System.Text;

namespace Chapter.Core.Git;

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

    public string GitPath { get; } = gitPath;

    /// <summary>Runs git and returns stdout as text, throwing if the command fails.</summary>
    public async Task<string> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var result = await TryRunAsync(workingDirectory, ct, args).ConfigureAwait(false);
        if (!result.Success)
            throw new GitException(result.CommandLine, result.ExitCode, result.StandardError);
        return result.StandardOutput;
    }

    /// <summary>
    /// Runs git and returns the full result without throwing. Use for commands where a
    /// non-zero exit is a legitimate answer — <c>merge-base</c> on unrelated histories,
    /// <c>symbolic-ref</c> when origin/HEAD is unset, and so on.
    /// </summary>
    public async Task<GitResult> TryRunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(GitPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in GlobalArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Stop git from ever blocking on a credential or editor prompt: this runs
        // headless inside a UI process, and a hung git is indistinguishable from a hang.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        var commandLine = $"git {string.Join(' ', args)}";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitException(commandLine, -1, $"Failed to start '{GitPath}': {ex.Message}");
        }

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

        foreach (var a in GlobalArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        var commandLine = $"git {string.Join(' ', args)}";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitException(commandLine, -1, $"Failed to start '{GitPath}': {ex.Message}");
        }

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
