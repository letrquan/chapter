namespace Chapter.Core.Git;

/// <summary>The outcome of writing a file back to the working tree.</summary>
public sealed record SaveResult
{
    public required string Path { get; init; }
    public required bool Success { get; init; }

    /// <summary>What went wrong, in a sentence, or null on success.</summary>
    public string? Error { get; init; }

    public int BytesWritten { get; init; }

    /// <summary>The format the file was written in — what the app preserved.</summary>
    public TextFormat? Format { get; init; }

    public static SaveResult Failed(string path, string error) =>
        new() { Path = path, Success = false, Error = error };
}

/// <summary>
/// Writes a file back to the working tree without changing anything the user did not.
///
/// Two properties matter and neither is free. The file keeps its encoding, byte-order mark
/// and newline convention, because an editor that silently converts a CRLF file to LF turns
/// a one-line edit into a whole-file diff. And the write is atomic, because this app writes
/// to worktrees that agents are reading from — a half-written file is a file an agent will
/// happily compile against.
/// </summary>
public static class WorkingTreeWriter
{
    /// <summary>
    /// Suffix of the file the atomic write goes through.
    ///
    /// It has to live in the target's own directory — a rename is only atomic within a
    /// volume — which puts it inside the worktree the app is watching and git is tracking.
    /// Exported so the watcher and the changed-file scan can both filter it out; without
    /// that it shows up as an untracked file mid-write, and an agent running
    /// <c>git add -A</c> at the wrong moment commits it.
    /// </summary>
    public const string TempSuffix = ".chapter-tmp";

    /// <summary>
    /// The CLI used only to resolve a repository's lease key, for callers that have none.
    ///
    /// Shared rather than constructed per save, and overridable: the key comes from
    /// <c>rev-parse --git-common-dir</c>, so a host running a git that is not on PATH must be
    /// able to hand its own CLI in. Two callers resolving the key with different gits would
    /// hash to two different lease files and the mutual exclusion would be silently absent —
    /// the one failure mode a lock has no way of reporting.
    /// </summary>
    private static readonly GitCli DefaultGit = new();

    /// <summary>
    /// Saves text to a file, preserving the format of whatever is already on disk.
    ///
    /// The format is read from the file rather than passed in by the caller, so it cannot
    /// drift: the bytes on disk are the only authority on how they are encoded, and the
    /// front-end has by then normalised the text through Monaco.
    /// </summary>
    public static async Task<SaveResult> SaveAsync(
        string worktreePath, string repoRelativePath, string text, CancellationToken ct = default,
        GitCli? git = null)
    {
        var lease = await RepositoryWriteLock.AcquireAsync(git ?? DefaultGit, worktreePath, ct)
            .ConfigureAwait(false);
        if (lease is null)
            return SaveResult.Failed(repoRelativePath,
                "another Chapter instance is writing this repository — try again");

        using (lease)
        {
            var format = await ReadFormatAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false);
            return await SaveAsyncUnderLease(worktreePath, repoRelativePath, text, format, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Saves text in an explicitly chosen format.</summary>
    public static async Task<SaveResult> SaveAsync(
        string worktreePath, string repoRelativePath, string text, TextFormat format,
        CancellationToken ct = default, GitCli? git = null)
    {
        var lease = await RepositoryWriteLock.AcquireAsync(git ?? DefaultGit, worktreePath, ct)
            .ConfigureAwait(false);
        if (lease is null)
            return SaveResult.Failed(repoRelativePath,
                "another Chapter instance is writing this repository — try again");

        using (lease)
            return await SaveAsyncUnderLease(worktreePath, repoRelativePath, text, format, ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes while the caller already holds the repository lease. This keeps a format read,
    /// validation and atomic replacement in one critical section without attempting to take
    /// the non-reentrant process semaphore a second time.
    /// </summary>
    internal static async Task<SaveResult> SaveAsyncUnderLease(
        string worktreePath, string repoRelativePath, string text, TextFormat format,
        CancellationToken ct = default)
    {
        // Staying inside the worktree is necessary and not sufficient: .git is inside it,
        // and writing a hook or a config there executes code.
        if (RepoPaths.EntersGitDirectory(repoRelativePath))
            return SaveResult.Failed(repoRelativePath, "that path is inside the git directory");

        string absolute;
        try
        {
            // Same guard as every other path crossing the bridge: a path from the
            // front-end is not trusted to stay inside the worktree.
            absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
        }
        catch (ArgumentException)
        {
            return SaveResult.Failed(repoRelativePath, "that path is outside the worktree");
        }

        var bytes = format.Encode(text);

        try
        {
            var directory = Path.GetDirectoryName(absolute);
            if (directory is not null) Directory.CreateDirectory(directory);

            await WriteAtomicallyAsync(absolute, bytes, ct).ConfigureAwait(false);

            return new SaveResult
            {
                Path = repoRelativePath,
                Success = true,
                BytesWritten = bytes.Length,
                Format = format,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return SaveResult.Failed(repoRelativePath, "the file is read-only or access was denied");
        }
        catch (IOException ex)
        {
            return SaveResult.Failed(repoRelativePath, $"the file could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves raw bytes atomically. Conflict resolution can choose an ours/theirs blob that
    /// is binary, where decoding it as text would corrupt the result.
    /// </summary>
    public static async Task<SaveResult> SaveBytesAsync(
        string worktreePath, string repoRelativePath, byte[] bytes, CancellationToken ct = default,
        GitCli? git = null)
    {
        var lease = await RepositoryWriteLock.AcquireAsync(git ?? DefaultGit, worktreePath, ct)
            .ConfigureAwait(false);
        if (lease is null)
            return SaveResult.Failed(repoRelativePath,
                "another Chapter instance is writing this repository — try again");

        using (lease)
            return await SaveBytesAsyncUnderLease(worktreePath, repoRelativePath, bytes, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Raw-byte counterpart for callers that already hold the repository lease.</summary>
    internal static async Task<SaveResult> SaveBytesAsyncUnderLease(
        string worktreePath, string repoRelativePath, byte[] bytes, CancellationToken ct = default)
    {
        if (RepoPaths.EntersGitDirectory(repoRelativePath))
            return SaveResult.Failed(repoRelativePath, "that path is inside the git directory");

        string absolute;
        try { absolute = RepoPaths.Resolve(worktreePath, repoRelativePath); }
        catch (ArgumentException)
        {
            return SaveResult.Failed(repoRelativePath, "that path is outside the worktree");
        }

        try
        {
            var directory = Path.GetDirectoryName(absolute);
            if (directory is not null) Directory.CreateDirectory(directory);
            await WriteAtomicallyAsync(absolute, bytes, ct).ConfigureAwait(false);
            return new SaveResult
            {
                Path = repoRelativePath,
                Success = true,
                BytesWritten = bytes.Length,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return SaveResult.Failed(repoRelativePath, "the file is read-only or access was denied");
        }
        catch (IOException ex)
        {
            return SaveResult.Failed(repoRelativePath, $"the file could not be written: {ex.Message}");
        }
    }

    /// <summary>The format of the file as it exists on disk, or the default when it does not.</summary>
    public static async Task<TextFormat> ReadFormatAsync(
        string worktreePath, string repoRelativePath, CancellationToken ct = default)
    {
        try
        {
            var absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
            if (!File.Exists(absolute)) return TextFormat.Default;

            var existing = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
            return FileContent.FromBytes(existing).Format;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable is not a reason to refuse the write; it only means the format has
            // to be guessed, and the guess is the same one a new file gets.
            return TextFormat.Default;
        }
    }

    /// <summary>
    /// Writes through a temporary file in the same directory, then replaces the original.
    ///
    /// Same directory specifically: the replace is only atomic within a volume, and a temp
    /// directory elsewhere would silently degrade to a copy. The suffix names this app so
    /// that a leftover from a crash is identifiable rather than looking like an agent's.
    /// </summary>
    private static async Task WriteAtomicallyAsync(string absolute, byte[] bytes, CancellationToken ct)
    {
        var temporary = absolute + TempSuffix;

        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            File.Move(temporary, absolute, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original write already failed; a stranded temp file is the lesser problem.
        }
    }
}
