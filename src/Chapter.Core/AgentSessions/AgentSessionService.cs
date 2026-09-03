using System.Globalization;
using System.Text;
using System.Text.Json;
using Chapter.Core.Git;

namespace Chapter.Core.AgentSessions;

/// <summary>Agent products whose on-disk session logs Chapter knows how to find.</summary>
public enum AgentSessionProvider
{
    Claude,
    Book,
    Codex,
}

/// <summary>Locations of the local agent session stores.</summary>
public sealed record AgentSessionLocations
{
    public required string ClaudeProjectsPath { get; init; }
    public required string BookSessionsPath { get; init; }
    public required string BookIndexPath { get; init; }
    public required string CodexSessionsPath { get; init; }
    public required string CodexIndexPath { get; init; }

    public static AgentSessionLocations Default()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetEnvironmentVariable("USERPROFILE")
                      ?? Environment.GetEnvironmentVariable("HOME")
                      ?? AppContext.BaseDirectory;

        return new AgentSessionLocations
        {
            ClaudeProjectsPath = Path.Combine(profile, ".claude", "projects"),
            BookSessionsPath = Path.Combine(profile, ".book", "sessions"),
            BookIndexPath = Path.Combine(profile, ".book", "sessions", "session-index.json"),
            CodexSessionsPath = Path.Combine(profile, ".codex", "sessions"),
            CodexIndexPath = Path.Combine(profile, ".codex", "session_index.jsonl"),
        };
    }
}

/// <summary>A session metadata record, deliberately without transcript content.</summary>
public sealed record AgentSession
{
    public required AgentSessionProvider Provider { get; init; }
    public required string Id { get; init; }
    public required string LogPath { get; init; }
    public string? Name { get; init; }
    public string? Branch { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? MessageCount { get; init; }

    /// <summary>Internal ranking value; it is never sent over the bridge.</summary>
    internal int MatchScore { get; init; }
}

/// <summary>One worktree to resolve against the local session stores.</summary>
public sealed record AgentSessionWorktree(string Path, string? Branch);

/// <summary>
/// Finds Claude Code, Book and Codex sessions that belong to a worktree.
///
/// Session logs are user data and can be very large. The scanner reads only metadata-bearing
/// prefixes (and Book's explicit index), never returns transcript text, and only returns paths
/// under the known agent directories. A caller can therefore offer an external-open action
/// without turning the bridge into a transcript reader or an arbitrary file launcher.
/// </summary>
public sealed class AgentSessionService
{
    private const int MaxMetadataBytes = 128 * 1024;
    private const int MaxMetadataLines = 160;
    private const long MaxIndexBytes = 8L * 1024 * 1024;
    private const int MaxResultsPerWorktree = 12;
    private const int MaxFilesPerStore = 1500;

    private readonly AgentSessionLocations _locations;
    private readonly string[] _safeRoots;

    public AgentSessionService(AgentSessionLocations? locations = null)
    {
        _locations = locations ?? AgentSessionLocations.Default();
        _safeRoots =
        [
            NormalizeRoot(_locations.ClaudeProjectsPath),
            NormalizeRoot(_locations.BookSessionsPath),
            NormalizeRoot(_locations.CodexSessionsPath),
        ];
    }

    public AgentSessionLocations Locations => _locations;

    /// <summary>Finds sessions for one worktree, newest and best matched first.</summary>
    public async Task<IReadOnlyList<AgentSession>> FindAsync(
        string worktreePath, string? branch = null, CancellationToken ct = default)
    {
        var result = await FindForWorktreesAsync(
            [new AgentSessionWorktree(worktreePath, branch)], ct).ConfigureAwait(false);
        return result.TryGetValue(worktreePath, out var sessions) ? sessions : [];
    }

    /// <summary>Descriptive alias for callers that treat the scanner as a list operation.</summary>
    public Task<IReadOnlyList<AgentSession>> ListAsync(
        string worktreePath, string? branch = null, CancellationToken ct = default) =>
        FindAsync(worktreePath, branch, ct);

    /// <summary>
    /// Scans the stores once and resolves all requested worktrees. This matters in a real
    /// repository with several linked agents: reading every log once is cheaper than doing
    /// the same directory walk once per row in the refs panel.
    /// </summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<AgentSession>>> FindForWorktreesAsync(
        IReadOnlyList<AgentSessionWorktree> worktrees, CancellationToken ct = default) =>
        Task.Run(() => FindForWorktrees(worktrees, ct), ct);

    /// <summary>
    /// Verifies a path before handing it to the shell. The caller still has to ensure the
    /// session came from this service; this check protects the final filesystem boundary.
    /// </summary>
    public bool IsSafeLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;
        if (!IsUnderAnyRoot(full)) return false;

        // A reparse-point log could redirect an otherwise safe-looking path to arbitrary
        // user data. Refuse it rather than asking the shell to follow the link.
        try
        {
            var file = new FileInfo(full);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return false;

            var current = file.Directory;
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                current = current.Parent;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<AgentSession>> FindForWorktrees(
        IReadOnlyList<AgentSessionWorktree> requested, CancellationToken ct)
    {
        var normalized = requested
            .Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => (Original: w.Path, Path: NormalizePath(w.Path), Branch: NormalizeBranch(w.Branch)))
            .ToArray();

        var buckets = normalized.ToDictionary(
            w => w.Original,
            _ => new List<AgentSession>(),
            StringComparer.OrdinalIgnoreCase);

        if (normalized.Length == 0)
            return buckets.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AgentSession>)pair.Value,
                StringComparer.OrdinalIgnoreCase);

        var sessions = new List<DiscoveredSession>();
        sessions.AddRange(ReadClaude(ct));
        sessions.AddRange(ReadBook(ct));
        sessions.AddRange(ReadCodex(ct));

        // Companion files and index fallbacks can describe the same session twice. Keep the
        // path as part of the key: two providers may legitimately use the same id.
        sessions = sessions
            .GroupBy(session => $"{session.Value.Provider}:{session.Value.Id}:{session.Value.LogPath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var worktree in normalized)
        {
            ct.ThrowIfCancellationRequested();
            var activity = WorktreeActivityAt(worktree.Path);
            var matched = sessions
                .Select(session => (Session: session, Score: Score(
                    session, worktree.Path, worktree.Branch, activity)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Session.Value.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.Session.Value.StartedAt ?? DateTimeOffset.MinValue)
                .Take(MaxResultsPerWorktree)
                .Select(item => item.Session.Value with { MatchScore = item.Score })
                .ToArray();

            buckets[worktree.Original].AddRange(matched);
        }

        return buckets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AgentSession>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<DiscoveredSession> ReadClaude(CancellationToken ct)
    {
        foreach (var file in EnumerateJsonl(_locations.ClaudeProjectsPath, recursive: true, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSafeLogPath(file)) continue;
            if (!TryGetClaudeProjectFolder(file, out var folderHint)) continue;
            var metadata = ReadMetadata(file, AgentSessionProvider.Claude, ct);
            if (metadata is null) continue;

            var id = FirstNonEmpty(metadata.Id, Path.GetFileNameWithoutExtension(file));
            if (id.Length == 0) continue;

            yield return new DiscoveredSession(
                new AgentSession
                {
                    Provider = AgentSessionProvider.Claude,
                    Id = id,
                    LogPath = file,
                    Name = metadata.Name,
                    Branch = NormalizeBranch(metadata.Branch),
                    StartedAt = metadata.StartedAt,
                    UpdatedAt = Latest(metadata.UpdatedAt, FileTime(file)),
                    MessageCount = metadata.MessageCount,
                },
                metadata.Cwd,
                folderHint);
        }
    }

    private IEnumerable<DiscoveredSession> ReadBook(CancellationToken ct)
    {
        var indexed = ReadBookIndex(ct).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in indexed)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(_locations.BookSessionsPath, item.Id + ".jsonl");
            if (!IsSafeLogPath(file)) continue;

            seen.Add(Path.GetFullPath(file));
            yield return new DiscoveredSession(
                new AgentSession
                {
                    Provider = AgentSessionProvider.Book,
                    Id = item.Id,
                    LogPath = Path.GetFullPath(file),
                    Name = item.Name,
                    Branch = NormalizeBranch(item.Branch),
                    StartedAt = item.StartedAt,
                    UpdatedAt = Latest(item.UpdatedAt, FileTime(file)),
                    MessageCount = item.MessageCount,
                },
                item.Cwd,
                "");
        }

        // Older Book versions did not write an index. The bounded fallback keeps those
        // sessions discoverable without making the normal path parse every transcript.
        foreach (var file in EnumerateJsonl(_locations.BookSessionsPath, recursive: false, ct))
        {
            if (Path.GetFileName(file).Equals("session-index.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(Path.GetFullPath(file))) continue;
            if (!IsSafeLogPath(file)) continue;

            var metadata = ReadMetadata(file, AgentSessionProvider.Book, ct);
            if (metadata is null) continue;
            var id = FirstNonEmpty(metadata.Id, Path.GetFileNameWithoutExtension(file));
            if (id.Length == 0) continue;

            yield return new DiscoveredSession(
                new AgentSession
                {
                    Provider = AgentSessionProvider.Book,
                    Id = id,
                    LogPath = Path.GetFullPath(file),
                    Name = metadata.Name,
                    Branch = NormalizeBranch(metadata.Branch),
                    StartedAt = metadata.StartedAt,
                    UpdatedAt = Latest(metadata.UpdatedAt, FileTime(file)),
                    MessageCount = metadata.MessageCount,
                },
                metadata.Cwd,
                "");
        }
    }

    private IEnumerable<DiscoveredSession> ReadCodex(CancellationToken ct)
    {
        var index = ReadCodexIndex(ct);

        foreach (var file in EnumerateJsonl(_locations.CodexSessionsPath, recursive: true, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSafeLogPath(file)) continue;
            var metadata = ReadMetadata(file, AgentSessionProvider.Codex, ct);
            if (metadata is null) continue;

            var id = FirstNonEmpty(metadata.Id, CodexIdFromFile(file));
            if (id.Length == 0) continue;
            index.TryGetValue(id, out var indexed);

            yield return new DiscoveredSession(
                new AgentSession
                {
                    Provider = AgentSessionProvider.Codex,
                    Id = id,
                    LogPath = file,
                    Name = FirstNonEmptyOrNull(metadata.Name, indexed?.Name),
                    Branch = NormalizeBranch(FirstNonEmptyOrNull(metadata.Branch, indexed?.Branch)),
                    StartedAt = metadata.StartedAt ?? indexed?.StartedAt,
                    UpdatedAt = Latest(metadata.UpdatedAt, indexed?.UpdatedAt, FileTime(file)),
                    MessageCount = metadata.MessageCount ?? indexed?.MessageCount,
                },
                metadata.Cwd,
                "");
        }
    }

    private IEnumerable<SessionMetadata> ReadBookIndex(CancellationToken ct)
    {
        if (!File.Exists(_locations.BookIndexPath)) yield break;

        JsonDocument? document = null;
        try
        {
            var info = new FileInfo(_locations.BookIndexPath);
            if (!info.Exists || info.Length > MaxIndexBytes) yield break;
            using var stream = new FileStream(
                _locations.BookIndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 16 * 1024, options: FileOptions.SequentialScan);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            var collection = root.ValueKind == JsonValueKind.Object
                             && root.TryGetProperty("sessions", out var sessions)
                ? sessions
                : root;

            if (collection.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in collection.EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = MetadataFromElement(property.Value, AgentSessionProvider.Book);
                    if (metadata is null) continue;
                    yield return metadata with { Id = FirstNonEmpty(metadata.Id, property.Name) };
                }
            }
            else if (collection.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in collection.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = MetadataFromElement(item, AgentSessionProvider.Book);
                    if (metadata is not null && metadata.Id.Length > 0) yield return metadata;
                }
            }
        }
    }

    private Dictionary<string, SessionMetadata> ReadCodexIndex(CancellationToken ct)
    {
        var result = new Dictionary<string, SessionMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_locations.CodexIndexPath)) return result;

        foreach (var line in ReadPrefixLines(_locations.CodexIndexPath, ct))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = GetString(root, "id");
                if (id.Length == 0) continue;

                result[id] = new SessionMetadata
                {
                    Id = id,
                    Name = GetStringOrNull(root, "thread_name", "name"),
                    UpdatedAt = GetDate(root, "updated_at", "timestamp"),
                    StartedAt = GetDate(root, "created_at"),
                    Branch = GetNestedString(root, "git", "branch"),
                };
            }
            catch (JsonException)
            {
                // One truncated index line should not hide the other sessions.
            }
        }

        return result;
    }

    private static SessionMetadata? ReadMetadata(
        string file, AgentSessionProvider provider, CancellationToken ct)
    {
        var metadata = new SessionMetadata();
        var found = false;
        var lineNumber = 0;

        foreach (var line in ReadPrefixLines(file, ct))
        {
            if (++lineNumber > MaxMetadataLines) break;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = MetadataFromElement(document.RootElement, provider);
                if (item is null) continue;

                metadata = Merge(metadata, item);
                found = true;
            }
            catch (JsonException)
            {
                // Logs are append-only and may be observed while a line is still being
                // written. Ignore that line and keep the metadata already read.
            }
        }

        return found ? metadata : null;
    }

    private static SessionMetadata? MetadataFromElement(JsonElement element, AgentSessionProvider provider)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var recordType = GetString(element, "type", "kind");
        var codexSessionMetadata = provider != AgentSessionProvider.Codex
                                   || recordType.Equals("session_meta", StringComparison.OrdinalIgnoreCase)
                                   || (recordType.Length == 0
                                       && (HasString(element, "session_id", "sessionId")
                                           || HasString(element, "cwd", "workingDirectory", "worktree")));
        if (provider == AgentSessionProvider.Codex && !codexSessionMetadata) return null;

        var roots = new List<JsonElement> { element };
        foreach (var name in new[] { "payload", "data", "meta", "metadata" })
            if (element.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object)
                roots.Add(nested);

        string id = "", cwd = "", branch = "", nameValue = "";
        DateTimeOffset? started = null, updated = null;
        int? messageCount = null;

        foreach (var root in roots)
        {
            id = FirstNonEmpty(id, provider switch
            {
                AgentSessionProvider.Claude => GetString(root, "sessionId", "session_id", "id"),
                AgentSessionProvider.Codex when codexSessionMetadata =>
                    GetString(root, "session_id", "sessionId", "id"),
                AgentSessionProvider.Book => GetString(root, "sessionId", "session_id", "id"),
                _ => "",
            });

            if (codexSessionMetadata)
            {
                cwd = FirstNonEmpty(cwd, GetString(root, "cwd", "workingDirectory", "worktree"));
                branch = FirstNonEmpty(branch, GetString(root, "gitBranch", "branch"));
            }

            nameValue = FirstNonEmpty(nameValue, provider switch
            {
                AgentSessionProvider.Claude => GetString(root, "aiTitle", "title", "name", "thread_name"),
                AgentSessionProvider.Book => GetString(root, "name", "title", "aiTitle"),
                AgentSessionProvider.Codex => GetString(root, "thread_name"),
                _ => "",
            });
            started ??= GetDate(root, "createdAt", "created_at", "startedAt", "started_at", "timestamp");
            updated ??= GetDate(root, "updatedAt", "updated_at", "timestamp");
            messageCount ??= GetInt(root, "messageCount", "message_count");

            if (codexSessionMetadata
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("git", out var git)
                && git.ValueKind == JsonValueKind.Object)
                branch = FirstNonEmpty(branch, GetString(git, "branch"));
        }

        if (id.Length == 0 && cwd.Length == 0 && branch.Length == 0 && nameValue.Length == 0
            && started is null && updated is null && messageCount is null)
            return null;

        return new SessionMetadata
        {
            Id = id,
            Cwd = cwd,
            Branch = branch,
            Name = nameValue.Length == 0 ? null : nameValue,
            StartedAt = started,
            UpdatedAt = updated,
            MessageCount = messageCount,
        };
    }

    private static SessionMetadata Merge(SessionMetadata current, SessionMetadata next) => new()
    {
        Id = FirstNonEmpty(current.Id, next.Id),
        Cwd = FirstNonEmpty(current.Cwd, next.Cwd),
        Branch = FirstNonEmpty(current.Branch, next.Branch),
        Name = current.Name ?? next.Name,
        StartedAt = Earliest(current.StartedAt, next.StartedAt),
        UpdatedAt = Latest(current.UpdatedAt, next.UpdatedAt),
        MessageCount = next.MessageCount ?? current.MessageCount,
    };

    private static int Score(
        DiscoveredSession discovered,
        string worktreePath,
        string? branch,
        DateTimeOffset? worktreeActivity)
    {
        var pathScore = 0;
        var cwd = NormalizePath(discovered.Cwd);

        if (cwd.Length > 0)
        {
            if (PathEquals(cwd, worktreePath)) pathScore = 120;
            else if (IsWithin(cwd, worktreePath) || IsWithin(worktreePath, cwd)) pathScore = 75;
        }

        var folderScore = discovered.FolderHint.Length > 0
                          && ClaudeFolderMatches(discovered.FolderHint, worktreePath)
            ? 110
            : 0;

        var branchScore = 0;
        var sessionBranch = NormalizeBranch(discovered.Value.Branch);
        if (branch is not null && branch.Length > 0 && sessionBranch is not null)
        {
            if (string.Equals(branch, sessionBranch, StringComparison.OrdinalIgnoreCase)) branchScore = 45;
            else if (BranchLeaf(branch) == BranchLeaf(sessionBranch)) branchScore = 15;
        }

        // A known, different cwd is strong evidence this is another repository. Branch names
        // such as "main" are common, so branch metadata is only a tie-breaker after a path or
        // folder match. Logs that never recorded cwd still use branch fallback.
        if (cwd.Length > 0 && pathScore == 0 && folderScore == 0) return 0;

        var score = pathScore + folderScore + branchScore;
        if (score == 0) return 0;

        // Timestamps rank otherwise-valid candidates without making an exact path match
        // disappear when a filesystem timestamp is coarse or unavailable.
        var sessionTime = discovered.Value.UpdatedAt ?? discovered.Value.StartedAt;
        if (worktreeActivity is not null && sessionTime is not null)
        {
            var distance = (worktreeActivity.Value - sessionTime.Value).Duration();
            score += distance <= TimeSpan.FromMinutes(15) ? 18
                : distance <= TimeSpan.FromHours(2) ? 10
                : distance <= TimeSpan.FromDays(2) ? 3
                : 0;
        }

        return score;
    }

    private bool IsUnderAnyRoot(string path)
    {
        var full = NormalizePath(path);
        return _safeRoots.Any(root => IsWithin(full, root));
    }

    private static bool ClaudeFolderMatches(string folder, string worktreePath)
    {
        var normalized = NormalizePath(worktreePath);
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            EncodeClaudePath(normalized),
            EncodeClaudePath(normalized.Replace('/', '\\')),
        };

        return variants.Contains(folder);
    }

    private static string EncodeClaudePath(string path)
    {
        var value = path.Replace('\\', '-').Replace('/', '-').Replace(':', '-');
        return value.TrimEnd('-');
    }

    private bool TryGetClaudeProjectFolder(string file, out string folder)
    {
        folder = "";
        string relative;
        try { relative = Path.GetRelativePath(_locations.ClaudeProjectsPath, file); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        folder = parts[0];
        return folder.Length > 0;
    }

    private static IEnumerable<string> EnumerateJsonl(string root, bool recursive, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;

        string[] files;
        try
        {
            // Materialise while the directory walk is inside the guarded block. A log store is
            // append-only and can be pruned concurrently; lazy enumeration otherwise lets one
            // disappearing directory abort the entire refs refresh.
            files = Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files
                     .OrderByDescending(path => SafeLastWriteTime(path))
                     .Take(MaxFilesPerStore))
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(file)) yield return Path.GetFullPath(file);
        }
    }

    private static IReadOnlyList<string> ReadPrefixLines(string file, CancellationToken ct)
    {
        var lines = new List<string>();
        if (!File.Exists(file)) return lines;

        try
        {
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 16 * 1024, options: FileOptions.SequentialScan);
            var buffer = new byte[MaxMetadataBytes];
            var bytes = 0;
            while (bytes < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, bytes, buffer.Length - bytes);
                if (read == 0) break;
                bytes += read;
            }

            // Agent stores are UTF-8 JSONL. Decoding the bounded byte buffer before splitting
            // lines means one very large transcript record cannot force ReadLine to allocate
            // the whole record just to discover that its metadata is irrelevant.
            var text = Encoding.UTF8.GetString(buffer, 0, bytes).TrimStart('\uFEFF');
            lines.AddRange(text.Split('\n').Select(line => line.TrimEnd('\r')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log can disappear while the user is looking at the refs panel.
        }

        return lines;
    }

    private static string CodexIdFromFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        const int uuidLength = 36;
        if (name.Length >= uuidLength)
        {
            var candidate = name[^uuidLength..];
            if (Guid.TryParseExact(candidate, "D", out _)) return candidate;
        }

        var marker = name.LastIndexOf('-');
        return marker >= 0 && marker + 1 < name.Length ? name[(marker + 1)..] : "";
    }

    private static DateTimeOffset? FileTime(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values) =>
        values.Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty().Max() is var latest
        && latest != default
            ? latest
            : null;

    private static DateTimeOffset? Earliest(params DateTimeOffset?[] values) =>
        values.Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty().Min() is var earliest
        && earliest != default
            ? earliest
            : null;

    private static DateTimeOffset? WorktreeActivityAt(string path)
    {
        var candidates = new[]
        {
            path,
            Path.Combine(path, ".git"),
            Path.Combine(path, ".git", "HEAD"),
        };

        return candidates
            .Select(SafeLastWriteTime)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .OrderByDescending(value => value)
            .FirstOrDefault();
    }

    private static DateTimeOffset? SafeLastWriteTime(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static DateTimeOffset? GetDate(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                try
                {
                    // Agent indexes use milliseconds; tolerate seconds for older fixtures.
                    return number > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                        : DateTimeOffset.FromUnixTimeSeconds(number);
                }
                catch (ArgumentOutOfRangeException) { }
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }

        return null;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        }

        return null;
    }

    private static string GetString(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }

    private static bool HasString(JsonElement root, params string[] names) =>
        names.Any(name => root.ValueKind == JsonValueKind.Object
                          && root.TryGetProperty(name, out var value)
                          && value.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(value.GetString()));

    private static string? GetStringOrNull(JsonElement root, params string[] names) =>
        GetString(root, names) is { Length: > 0 } value ? value : null;

    private static string GetNestedString(JsonElement root, string parent, string child) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var nested)
            ? GetString(nested, child)
            : "";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string? FirstNonEmptyOrNull(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var full = Path.GetFullPath(RepoPaths.ToPlatform(path.Trim()));
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static string? NormalizeBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var value = branch.Trim();
        return value.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? value["refs/heads/".Length..]
            : value;
    }

    private static string BranchLeaf(string? branch) =>
        branch?.Replace('\\', '/').Split('/').LastOrDefault() ?? "";

    private static bool PathEquals(string left, string right) =>
        left.Length > 0 && right.Length > 0
        && string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string root)
    {
        if (path.Length == 0 || root.Length == 0) return false;
        var normalizedPath = path.TrimEnd('\\', '/');
        var normalizedRoot = root.TrimEnd('\\', '/');
        if (PathEquals(normalizedPath, normalizedRoot)) return true;
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SessionMetadata
    {
        public string Id { get; init; } = "";
        public string Cwd { get; init; } = "";
        public string Branch { get; init; } = "";
        public string? Name { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public int? MessageCount { get; init; }
    }

    private sealed record DiscoveredSession(AgentSession Value, string Cwd, string FolderHint);
}
