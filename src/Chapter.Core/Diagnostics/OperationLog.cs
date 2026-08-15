using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chapter.Core.Diagnostics;

/// <summary>One thing the app did to a repository.</summary>
public sealed record OperationLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The user-facing name of what was attempted — "commit", "discard".</summary>
    public required string Operation { get; init; }

    public required string WorktreePath { get; init; }

    /// <summary>The git command as run. The whole point of this log.</summary>
    public required string CommandLine { get; init; }

    public required int ExitCode { get; init; }
    public long ElapsedMs { get; init; }

    /// <summary>Above one means the command was retried through lock contention.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>The classified failure, or null when the command succeeded.</summary>
    public string? Failure { get; init; }

    /// <summary>What went wrong, in the words the user was shown.</summary>
    public string? Detail { get; init; }

    public bool Success => ExitCode == 0;
}

/// <summary>
/// An append-only record of every mutation the app performed.
///
/// This exists for one moment: the first time the app does something the user did not
/// expect. Without it there is no way to find out what happened — git leaves no trace of
/// who ran a command, and "Chapter did something to my repo" is unanswerable. With it the
/// answer is a line of text naming the exact command.
///
/// Kept in memory for the UI and mirrored to disk, because the interesting case is the one
/// where the app has already been restarted by the time anybody asks.
/// </summary>
public sealed class OperationLog
{
    /// <summary>How many entries the UI can scroll back through in one session.</summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// Point at which the file is rotated. Small on purpose: this is a diagnostic aid, not
    /// an audit trail, and a log that grows without limit is its own bug report.
    /// </summary>
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentQueue<OperationLogEntry> _entries = new();
    private readonly object _fileGate = new();
    private readonly string? _filePath;

    /// <param name="filePath">
    /// Where to mirror entries, or null to keep the log in memory only — which is what the
    /// tests want, since a test run should not write to the user's profile.
    /// </param>
    public OperationLog(string? filePath = null) => _filePath = filePath;

    /// <summary>The default on-disk location, alongside the settings file.</summary>
    public static string DefaultFilePath =>
        Path.Combine(AppSettings.DirectoryPath, "operations.log");

    /// <summary>Raised on every append, so the UI can show operations as they happen.</summary>
    public event Action<OperationLogEntry>? Appended;

    public void Append(OperationLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries) _entries.TryDequeue(out _);

        WriteToFile(entry);

        // A subscriber that throws must not take down the mutation that logged it.
        try
        {
            Appended?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Operation log subscriber failed: {ex.Message}");
        }
    }

    /// <summary>Most recent entries first, which is the order anybody reads a log in.</summary>
    public IReadOnlyList<OperationLogEntry> Recent(int limit = 100)
    {
        if (limit <= 0) return [];

        var all = _entries.ToArray();
        var take = Math.Min(limit, all.Length);

        var result = new OperationLogEntry[take];
        for (var i = 0; i < take; i++) result[i] = all[all.Length - 1 - i];
        return result;
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private void WriteToFile(OperationLogEntry entry)
    {
        if (_filePath is null) return;

        try
        {
            var line = JsonSerializer.Serialize(entry, Json);

            // One lock rather than one handle: appends are rare and small, and holding a
            // stream open would keep a handle on a file in the user's profile for the life
            // of the app for no gain.
            lock (_fileGate)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (directory is not null) Directory.CreateDirectory(directory);

                RotateIfLarge();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Losing a log line must never fail the operation it was describing.
            System.Diagnostics.Debug.WriteLine($"Operation log write failed: {ex.Message}");
        }
    }

    /// <summary>Keeps one previous generation, so a rotation mid-investigation loses nothing.</summary>
    private void RotateIfLarge()
    {
        if (_filePath is null) return;

        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        var previous = _filePath + ".1";
        try
        {
            File.Move(_filePath, previous, overwrite: true);
        }
        catch (IOException)
        {
            // Rotation failing is not a reason to stop logging; the file just grows.
        }
    }
}
