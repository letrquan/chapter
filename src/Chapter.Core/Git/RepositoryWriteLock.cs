using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>
/// A short-lived, repository-wide lease for mutations started by Chapter.
///
/// Git's own <c>index.lock</c> protects individual index writes, but it does not stop two
/// Chapter windows from both reading a stale branch/stash list and then acting on it. The
/// lease is deliberately outside Git's namespace: a file-region lock is released by the
/// operating system when the process dies, and the hash makes linked worktrees converge on
/// the same file after resolving their common git directory.
/// </summary>
internal sealed class RepositoryWriteLease : IDisposable
{
    private readonly SemaphoreSlim _local;
    private FileStream? _stream;
    private int _disposed;

    internal RepositoryWriteLease(SemaphoreSlim local, FileStream stream)
    {
        _local = local;
        _stream = stream;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            try { stream.Unlock(0, 1); }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
            stream.Dispose();
        }

        _local.Release();
    }
}

/// <summary>The outcome of one attempt to take the repository lease.</summary>
/// <param name="Lease">The lease, or null when it was not available in time.</param>
/// <param name="BusyInThisProcess">
/// True when this process already holds it. The distinction is only for wording: either way
/// the caller did not get the lease.
/// </param>
internal readonly record struct LeaseAttempt(RepositoryWriteLease? Lease, bool BusyInThisProcess)
{
    internal static LeaseAttempt BusyHere { get; } = new(null, true);
    internal static LeaseAttempt BusyElsewhere { get; } = new(null, false);
}

/// <summary>Coordinates Chapter mutations across windows and processes.</summary>
internal static class RepositoryWriteLock
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Local = new(
        StringComparer.OrdinalIgnoreCase);

    internal static async Task<RepositoryWriteLease?> AcquireAsync(
        GitCli git,
        string worktreePath,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        (await TryAcquireAsync(git, worktreePath, ct, timeout).ConfigureAwait(false)).Lease;

    /// <summary>
    /// Acquires the lease, and says which kind of contention refused it.
    ///
    /// The two are worth telling apart in what the user is shown. A lease this process is
    /// already holding — a pull midway through its merge — is a wait, and saying "another
    /// Chapter instance" about it is simply untrue when only one window is open.
    /// </summary>
    internal static async Task<LeaseAttempt> TryAcquireAsync(
        GitCli git,
        string worktreePath,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var key = await ResolveKeyAsync(git, worktreePath, ct).ConfigureAwait(false);
        var local = Local.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        var limit = timeout ?? DefaultTimeout;

        if (!await local.WaitAsync(limit, ct).ConfigureAwait(false))
            return LeaseAttempt.BusyHere;

        try
        {
            var lockPath = LockPath(key);
            var started = Stopwatch.GetTimestamp();

            while (Elapsed(started) < limit)
            {
                ct.ThrowIfCancellationRequested();

                FileStream? stream = null;
                try
                {
                    var directory = Path.GetDirectoryName(lockPath);
                    if (directory is not null) Directory.CreateDirectory(directory);

                    // The file is intentionally opened with sharing enabled. The byte-range
                    // lock, rather than the share flags, is the cross-process arbitration;
                    // this lets a future diagnostic read the owner marker if needed.
                    stream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.ReadWrite, bufferSize: 1, FileOptions.None);
                    if (stream.Length == 0) stream.SetLength(1);
                    stream.Lock(0, 1);

                    var owner = Encoding.UTF8.GetBytes(
                        $"pid={Environment.ProcessId};utc={DateTimeOffset.UtcNow:O}\n");
                    stream.Position = 0;
                    stream.Write(owner, 0, Math.Min(owner.Length, 4096));
                    stream.Flush(flushToDisk: false);

                    return new LeaseAttempt(new RepositoryWriteLease(local, stream), false);
                }
                catch (IOException)
                {
                    stream?.Dispose();
                }
                catch (UnauthorizedAccessException)
                {
                    // A read-only temp directory or a locked file should not turn a write
                    // into an unclassified exception. The local semaphore still protects
                    // windows in this process; retrying gives a transient ACL change a
                    // chance to settle before the caller is told to try again.
                    stream?.Dispose();
                }

                var remaining = limit - Elapsed(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(50)
                        ? remaining
                        : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            local.Release();
            throw;
        }

        local.Release();
        return LeaseAttempt.BusyElsewhere;
    }

    /// <summary>
    /// Resolves linked worktrees to the repository's common git directory. If Git cannot be
    /// queried (for example a deliberately invalid fixture path), the full worktree path is
    /// still a safe per-directory key and the actual command will report the real error.
    /// </summary>
    private static async Task<string> ResolveKeyAsync(
        GitCli git, string worktreePath, CancellationToken ct)
    {
        var fullWorktree = FullPathOrFallback(worktreePath);

        try
        {
            var result = await git.TryRunAsync(
                worktreePath, ct, "rev-parse", "--git-common-dir").ConfigureAwait(false);
            if (result.Success && result.Trimmed.Length > 0)
            {
                var reported = RepoPaths.ToPlatform(result.Trimmed.Trim());
                var common = Path.IsPathRooted(reported)
                    ? reported
                    : Path.Combine(fullWorktree, reported);
                return FullPathOrFallback(common);
            }
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            // The mutation itself will carry the useful failure. Locking a fallback key is
            // still preferable to allowing two callers to race on an invalid path.
        }

        return fullWorktree;
    }

    private static string LockPath(string key)
    {
        // Windows paths are case-insensitive while SHA-256 is not. Two Chapter windows can
        // receive the same repository through differently-cased settings/CLI paths; fold the
        // key before hashing so the cross-process lease still converges on one file.
        var canonical = OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "Chapter", "repository-locks", hash + ".lock");
    }

    private static string FullPathOrFallback(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static TimeSpan Elapsed(long started) =>
        TimeSpan.FromSeconds(
            (double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);
}
