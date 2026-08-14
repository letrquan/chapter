using System.Collections.Concurrent;
using System.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Indexing;

public enum IndexState
{
    Idle,
    Indexing,
    Ready,
    Failed,
}

/// <summary>
/// The symbol index for one worktree.
///
/// Built lazily on first visit and entirely in the background: the diff view and file list
/// work immediately, and navigation lights up when indexing finishes. Syntax trees are
/// dropped once declarations have been extracted — only the index is retained, which is
/// what keeps several worktrees resident at once without the memory cost that made a full
/// semantic workspace unworkable.
/// </summary>
public sealed class WorkspaceIndex(string worktreePath, ILanguageIndexer indexer)
{
    private readonly ConcurrentDictionary<string, List<SymbolDeclaration>> _byName =
        new(StringComparer.Ordinal);

    /// <summary>Inverted index: identifier name to the files mentioning it.</summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _filesByIdentifier =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, FileIndex> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private readonly HashSet<string> _allFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _filesLock = new();
    private readonly Lock _buildGate = new();

    /// <summary>
    /// Paths that changed while a build was in flight. The build snapshots the file list
    /// at the start, so anything an agent writes during those seconds would otherwise be
    /// missed permanently — no later event fires for a file that has stopped changing.
    /// </summary>
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    private Task? _buildTask;

    public string WorktreePath { get; } = worktreePath;
    public IndexState State { get; private set; } = IndexState.Idle;
    public int FilesIndexed { get; private set; }
    public int SymbolCount { get; private set; }
    public long ElapsedMs { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// Every file in the worktree, for the file picker — not just indexable ones.
    /// Returns a snapshot: the watcher mutates this set while queries read it.
    /// </summary>
    public IReadOnlyList<string> AllFiles
    {
        get { lock (_filesLock) return _allFiles.ToArray(); }
    }

    /// <summary>
    /// Builds the index if it has not been built. Safe to call repeatedly and from
    /// anywhere; concurrent callers await the same build.
    ///
    /// A finished-but-unsuccessful build is discarded so the next caller retries. Holding
    /// on to it would latch the failure for the lifetime of the process, leaving F12 and
    /// Ctrl+T silently returning nothing with no way to recover short of a restart.
    /// </summary>
    public Task EnsureBuiltAsync(CancellationToken ct = default)
    {
        if (State is IndexState.Ready) return Task.CompletedTask;

        lock (_buildGate)
        {
            if (_buildTask is { IsCompleted: true } && State is not IndexState.Ready)
                _buildTask = null;

            // Deliberately not passing the caller's token: the build is shared, and one
            // caller walking away must not cancel the index everyone else is waiting on.
            _buildTask ??= Task.Run(() => BuildAsync(CancellationToken.None), CancellationToken.None);
            return _buildTask;
        }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            State = IndexState.Indexing;
            var stopwatch = Stopwatch.StartNew();

            var root = RepoPaths.ToPlatform(WorktreePath);

            // FileScanner swallows per-directory IO errors, so an unreadable root yields an
            // empty list rather than throwing. Treating that as a successful empty index
            // would latch Ready and stop the worktree ever being indexed.
            if (!Directory.Exists(root))
            {
                Error = $"Worktree directory not found: {root}";
                State = IndexState.Failed;
                return;
            }

            var discovered = FileScanner.Enumerate(root, _ => true, ct);

            lock (_filesLock)
            {
                _allFiles.Clear();
                foreach (var path in discovered) _allFiles.Add(path);
            }

            var indexable = discovered.Where(indexer.CanIndex).ToArray();

            // The dominant cost is parsing, which is CPU-bound and embarrassingly parallel.
            await Parallel.ForEachAsync(
                indexable,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                async (relativePath, token) =>
                {
                    var absolute = Path.Combine(root, RepoPaths.ToPlatform(relativePath));
                    try
                    {
                        var text = await File.ReadAllTextAsync(absolute, token).ConfigureAwait(false);
                        AddFile(relativePath, indexer.IndexFile(relativePath, text));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished mid-scan — an agent is probably mid-write.
                    }
                }).ConfigureAwait(false);

            FilesIndexed = _byFile.Count;
            SymbolCount = _byName.Values.Sum(list => list.Count);
            ElapsedMs = stopwatch.ElapsedMilliseconds;
            State = IndexState.Ready;

            // Changes that arrived while this build was running were queued rather than
            // dropped — the agent does not stop working just because we started indexing.
            await DrainPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            State = IndexState.Idle;
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = IndexState.Failed;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private void AddFile(string relativePath, FileIndex fileIndex)
    {
        _byFile[relativePath] = fileIndex;

        foreach (var declaration in fileIndex.Declarations)
        {
            _byName.AddOrUpdate(
                declaration.Name,
                _ => [declaration],
                (_, existing) =>
                {
                    lock (existing) existing.Add(declaration);
                    return existing;
                });
        }

        foreach (var identifier in fileIndex.Identifiers)
        {
            var files = _filesByIdentifier.GetOrAdd(identifier, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (files) files.Add(relativePath);
        }
    }

    /// <summary>
    /// Re-indexes a single file after it changes on disk. Cheap enough to run on every
    /// watcher event, which is what keeps navigation correct while an agent is working.
    /// </summary>
    public async Task ReindexFileAsync(string relativePath, CancellationToken ct = default)
    {
        // Mid-build, the file list is a snapshot that is about to be overwritten, so
        // applying the change now would be lost. Queue it for the drain instead.
        if (State is IndexState.Indexing)
        {
            lock (_pendingChanges) _pendingChanges.Add(relativePath);
            return;
        }

        var absolute = Path.Combine(RepoPaths.ToPlatform(WorktreePath), RepoPaths.ToPlatform(relativePath));
        var exists = File.Exists(absolute);

        // The file list has to be maintained before the indexable check, not after: the
        // picker lists every file, and agents create .md and .ts files too. Gating this on
        // CanIndex would leave brand-new files — the ones most worth finding — invisible
        // to Ctrl+P until a full rebuild.
        lock (_filesLock)
        {
            if (exists) _allFiles.Add(relativePath);
            else _allFiles.Remove(relativePath);
        }

        if (!indexer.CanIndex(relativePath)) return;

        RemoveFile(relativePath);
        if (!exists) return;

        try
        {
            var text = await File.ReadAllTextAsync(absolute, ct).ConfigureAwait(false);
            AddFile(relativePath, indexer.IndexFile(relativePath, text));
            SymbolCount = _byName.Values.Sum(list => list.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-write; the next watcher event will pick it up.
        }
    }

    /// <summary>Applies changes that were queued while the build held the file list.</summary>
    private async Task DrainPendingAsync(CancellationToken ct)
    {
        while (true)
        {
            string[] batch;
            lock (_pendingChanges)
            {
                if (_pendingChanges.Count == 0) return;
                batch = _pendingChanges.ToArray();
                _pendingChanges.Clear();
            }

            foreach (var path in batch)
                await ReindexFileAsync(path, ct).ConfigureAwait(false);

            // Loop: reindexing is async, so more events may have arrived meanwhile.
        }
    }

    public void RemoveFile(string relativePath)
    {
        if (!_byFile.TryRemove(relativePath, out var previous)) return;

        foreach (var declaration in previous.Declarations)
        {
            if (!_byName.TryGetValue(declaration.Name, out var list)) continue;
            lock (list) list.RemoveAll(d => d.FilePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var identifier in previous.Identifiers)
        {
            if (!_filesByIdentifier.TryGetValue(identifier, out var files)) continue;
            lock (files) files.Remove(relativePath);
        }
    }

    // -----------------------------------------------------------------------
    // Queries
    // -----------------------------------------------------------------------

    /// <summary>Declarations with exactly this name — the go-to-definition candidates.</summary>
    public IReadOnlyList<SymbolDeclaration> FindDeclarations(string name)
    {
        if (!_byName.TryGetValue(name, out var list)) return [];
        lock (list) return list.ToArray();
    }

    /// <summary>Fuzzy symbol search for the Ctrl+T palette.</summary>
    public IReadOnlyList<SymbolDeclaration> SearchSymbols(string query, int limit)
    {
        if (query.Length == 0) return [];

        var scored = new List<(int Score, SymbolDeclaration Declaration)>();

        foreach (var (name, declarations) in _byName)
        {
            var score = FuzzyMatcher.Score(name, query);
            if (score < 0) continue;

            lock (declarations)
            {
                foreach (var declaration in declarations)
                {
                    // Types outrank their members when both match, since typing a type
                    // name almost always means you want the type.
                    var bonus = declaration.Kind is SymbolKind.Class or SymbolKind.Interface
                        or SymbolKind.Record or SymbolKind.Struct or SymbolKind.Enum ? 25 : 0;
                    scored.Add((score + bonus, declaration));
                }
            }
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Declaration.Name.Length)
            .Take(limit)
            .Select(entry => entry.Declaration)
            .ToArray();
    }

    /// <summary>Fuzzy file search for the Ctrl+P palette, matched on the file name first.</summary>
    public IReadOnlyList<string> SearchFiles(string query, int limit)
    {
        var files = AllFiles;
        if (query.Length == 0) return files.Take(limit).ToArray();

        var scored = new List<(int Score, string Path)>();

        foreach (var path in files)
        {
            var fileName = path[(path.LastIndexOf('/') + 1)..];

            // Score the name and the full path, and keep the better of the two, so both
            // "runner" and "features/chat/runner" find the same file.
            //
            // Each candidate must be rejected *before* its bonus is applied: Score returns
            // -1 for "no match at all", and adding the filename bonus to that sentinel
            // turns every non-matching file into a positive score, which made the palette
            // list the entire worktree for any query.
            var nameScore = FuzzyMatcher.Score(fileName, query);
            var pathScore = FuzzyMatcher.Score(path, query);
            if (nameScore < 0 && pathScore < 0) continue;

            const int fileNameBonus = 20;
            var score = Math.Max(
                nameScore < 0 ? int.MinValue : nameScore + fileNameBonus,
                pathScore);

            scored.Add((score, path));
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path.Length)
            .Take(limit)
            .Select(entry => entry.Path)
            .ToArray();
    }

    /// <summary>
    /// Every use of an identifier across the worktree.
    ///
    /// The inverted index narrows this to the files that actually contain the name, so a
    /// query re-reads a handful of files rather than the whole worktree.
    /// </summary>
    public async Task<IReadOnlyList<SymbolReference>> FindReferencesAsync(
        string identifier, CancellationToken ct = default)
    {
        if (!_filesByIdentifier.TryGetValue(identifier, out var files)) return [];

        string[] candidates;
        lock (files) candidates = files.ToArray();

        var root = RepoPaths.ToPlatform(WorktreePath);
        var results = new ConcurrentBag<SymbolReference>();

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (relativePath, token) =>
            {
                var absolute = Path.Combine(root, RepoPaths.ToPlatform(relativePath));
                try
                {
                    var text = await File.ReadAllTextAsync(absolute, token).ConfigureAwait(false);
                    foreach (var reference in indexer.FindOccurrences(relativePath, text, identifier))
                        results.Add(reference);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File went away between indexing and querying.
                }
            }).ConfigureAwait(false);

        return results
            .OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToArray();
    }

    /// <summary>Declarations in one file, for the outline and breadcrumbs.</summary>
    public IReadOnlyList<SymbolDeclaration> DocumentSymbols(string relativePath) =>
        _byFile.TryGetValue(relativePath, out var fileIndex) ? fileIndex.Declarations : [];
}
