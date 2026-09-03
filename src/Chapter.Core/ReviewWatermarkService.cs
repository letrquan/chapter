using System.Security.Cryptography;
using System.Text;
using Chapter.Core.Git;

namespace Chapter.Core;

/// <summary>The content snapshot last marked as reviewed for one worktree.</summary>
public sealed record ReviewWatermark
{
    public string Head { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset ReviewedAt { get; init; }
}

/// <summary>The current review state, compared with the persisted watermark.</summary>
public sealed record ReviewWatermarkStatus
{
    public required string WorktreePath { get; init; }
    public string Head { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public ReviewWatermark? Watermark { get; init; }
    public bool HasUnreviewedChanges { get; init; }
    public bool Success { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Tracks what a reviewer has seen without adding refs or files to the repository.
///
/// The fingerprint includes the current HEAD, the tracked working diff and ordinary
/// untracked paths/bytes. Ignored build output is intentionally excluded because it is not in
/// Chapter's review surface and can change continuously during a build.
/// </summary>
public sealed class ReviewWatermarkService(GitCli git, AppSettings settings)
{
    private readonly object _settingsGate = new();

    /// <summary>Big enough that a large file is not thousands of reads, small enough to pool.</summary>
    private const int CopyBufferSize = 64 * 1024;

    public async Task<ReviewWatermarkStatus> GetAsync(
        string worktreePath, CancellationToken ct = default)
    {
        var snapshot = await ReadSnapshotAsync(worktreePath, ct).ConfigureAwait(false);
        if (!snapshot.Success)
            return new ReviewWatermarkStatus
            {
                WorktreePath = worktreePath,
                Success = false,
                Detail = "The worktree snapshot could not be read safely",
            };

        ReviewWatermark? watermark;
        lock (_settingsGate) watermark = FindWatermark(worktreePath);

        return new ReviewWatermarkStatus
        {
            WorktreePath = worktreePath,
            Head = snapshot.Head,
            Fingerprint = snapshot.Fingerprint,
            Watermark = watermark,
            HasUnreviewedChanges = watermark is null ||
                !string.Equals(watermark.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal),
            Success = true,
        };
    }

    public async Task<ReviewWatermarkStatus> MarkAsync(
        string worktreePath, string expectedFingerprint = "", CancellationToken ct = default)
    {
        var snapshot = await ReadSnapshotAsync(worktreePath, ct).ConfigureAwait(false);
        if (!snapshot.Success)
            return new ReviewWatermarkStatus
            {
                WorktreePath = worktreePath,
                Success = false,
                Detail = "The worktree snapshot could not be read safely",
            };

        if (!string.IsNullOrEmpty(expectedFingerprint) &&
            !string.Equals(expectedFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
        {
            ReviewWatermark? existingWatermark;
            lock (_settingsGate) existingWatermark = FindWatermark(worktreePath);
            return new ReviewWatermarkStatus
            {
                WorktreePath = worktreePath,
                Head = snapshot.Head,
                Fingerprint = snapshot.Fingerprint,
                Watermark = existingWatermark,
                HasUnreviewedChanges = true,
                Success = false,
                Detail = "The worktree changed after the reviewed snapshot was shown",
            };
        }

        var watermark = new ReviewWatermark
        {
            Head = snapshot.Head,
            Fingerprint = snapshot.Fingerprint,
            ReviewedAt = DateTimeOffset.UtcNow,
        };

        // Keep the dictionary update and its file write together. Two worktrees can finish
        // their mark calls at once; saving outside the gate lets the later writer erase the
        // other watermark in a last-writer-wins race.
        lock (_settingsGate)
        {
            var key = Key(worktreePath);
            foreach (var existing in settings.ReviewWatermarks.Keys
                         .Where(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                settings.ReviewWatermarks.Remove(existing);
            }

            settings.ReviewWatermarks[key] = watermark;
            settings.Save();
        }

        return new ReviewWatermarkStatus
        {
            WorktreePath = worktreePath,
            Head = snapshot.Head,
            Fingerprint = snapshot.Fingerprint,
            Watermark = watermark,
            HasUnreviewedChanges = false,
            Success = true,
        };
    }

    private async Task<Snapshot> ReadSnapshotAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var headTask = git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD");
            var untrackedTask = git.TryRunAsync(
                worktreePath, ct, "ls-files", "--others", "--exclude-standard", "-z");
            var stagedTask = git.RunBytesAsync(
                worktreePath, ct, "diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--");
            var unstagedTask = git.RunBytesAsync(
                worktreePath, ct, "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--");
            var conflictsTask = git.TryRunAsync(worktreePath, ct, "ls-files", "-u", "-z");
            await Task.WhenAll(headTask, untrackedTask, stagedTask, unstagedTask, conflictsTask)
                .ConfigureAwait(false);

            var head = await headTask.ConfigureAwait(false);
            var untrackedResult = await untrackedTask.ConfigureAwait(false);
            var staged = await stagedTask.ConfigureAwait(false);
            var unstaged = await unstagedTask.ConfigureAwait(false);
            var conflicts = await conflictsTask.ConfigureAwait(false);

            // `HEAD` is legitimately absent in a newly initialised repository. The two
            // independent diffs still describe its staged and unstaged work, so only a
            // failed command that is not the expected unborn case makes the snapshot unsafe.
            if (!untrackedResult.Success || !staged.Success || !unstaged.Success || !conflicts.Success)
                return Snapshot.Failed;
            if (!head.Success && !string.IsNullOrWhiteSpace(head.StandardError))
                return Snapshot.Failed;

            var records = RepoPaths.SplitNul(untrackedResult.StandardOutput);
            var untracked = records
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => !path.EndsWith(WorkingTreeWriter.TempSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            // Hashed as they are read rather than collected first. The paths are already in
            // a stable order, so nothing needs the bytes twice — and holding them did: an
            // untracked file that git is not ignoring can be a dataset or a build artifact a
            // .gitignore missed, and every watermark read loaded the whole of it into memory.
            var headSha = head.Success ? head.Trimmed : "";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, headSha);
            Append(hash, staged.StandardOutput);
            Append(hash, unstaged.StandardOutput);
            Append(hash, conflicts.StandardOutput);

            foreach (var path in untracked)
            {
                string absolute;
                try { absolute = RepoPaths.Resolve(worktreePath, path); }
                catch (ArgumentException) { return Snapshot.Failed; }

                if (!await AppendUntrackedAsync(hash, path, absolute, ct).ConfigureAwait(false))
                    return Snapshot.Failed;
            }

            return new Snapshot(true, headSha, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitException)
        {
            return Snapshot.Failed;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, byte[] bytes)
    {
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    /// <summary>
    /// Folds one untracked file into the running hash, in bounded memory.
    ///
    /// The framing is unchanged — path, attributes, then a length-prefixed body — so a
    /// watermark taken by an earlier build still matches. Only the body's route changed: it
    /// is copied through a small buffer instead of being materialised as one array.
    /// </summary>
    private static async Task<bool> AppendUntrackedAsync(
        IncrementalHash hash, string path, string absolute, CancellationToken ct)
    {
        try
        {
            // A symlink can point outside the worktree. It is part of the review surface,
            // but following it would make a metadata read hash arbitrary outside bytes.
            var info = new FileInfo(absolute);
            if (!info.Exists)
            {
                Append(hash, path);
                Append(hash, "missing");
                Append(hash, Encoding.UTF8.GetBytes("<missing>"));
                return true;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Append(hash, path);
                Append(hash, "reparse");
                Append(hash, Encoding.UTF8.GetBytes(info.LinkTarget ?? "<reparse-point>"));
                return true;
            }

            Append(hash, path);
            Append(hash, info.Attributes.ToString());

            await using var stream = new FileStream(
                absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: CopyBufferSize, useAsync: true);

            // The length prefix is the stream's own, taken once so a file being written
            // underneath cannot make the framing disagree with the bytes that follow.
            hash.AppendData(BitConverter.GetBytes((int)Math.Min(stream.Length, int.MaxValue)));

            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    hash.AppendData(buffer.AsSpan(0, read));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ReviewWatermark? FindWatermark(string path)
    {
        var key = Key(path);
        if (settings.ReviewWatermarks.TryGetValue(key, out var exact)) return exact;

        // Settings written by older builds (or copied between machines) may differ only in
        // slash direction or drive-letter casing. Do not turn that into a spurious `new`.
        return settings.ReviewWatermarks
            .FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string Key(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record Snapshot(bool Success, string Head, string Fingerprint)
    {
        public static Snapshot Failed { get; } = new(false, "", "");
    }
}
