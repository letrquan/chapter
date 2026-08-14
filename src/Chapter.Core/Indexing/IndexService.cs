using System.Collections.Concurrent;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Indexing;

/// <summary>
/// Holds one <see cref="WorkspaceIndex"/> per worktree and answers navigation queries
/// against them.
///
/// Several indexes stay resident at once — that is what makes switching worktrees instant
/// rather than a reload — bounded by an LRU cap so a session with many repositories open
/// does not grow without limit.
/// </summary>
public sealed class IndexService(ILanguageIndexer? indexer = null)
{
    /// <summary>
    /// How many worktree indexes to keep. Each is a few MB of declarations rather than the
    /// hundreds of MB a semantic workspace needs, so this can be generous.
    /// </summary>
    private const int MaxResidentIndexes = 8;

    private readonly ILanguageIndexer _indexer = indexer ?? new CSharpIndexer();
    private readonly ConcurrentDictionary<string, WorkspaceIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastUsed = new(StringComparer.OrdinalIgnoreCase);

    private long _tick;

    /// <summary>Raised when an index finishes building, so the UI can report readiness.</summary>
    public event Action<IndexStatusPayload>? StatusChanged;

    public WorkspaceIndex GetOrCreate(string worktreePath)
    {
        _lastUsed[worktreePath] = Interlocked.Increment(ref _tick);

        var index = _indexes.GetOrAdd(worktreePath, path => new WorkspaceIndex(path, _indexer));
        EvictIfNeeded();
        return index;
    }

    /// <summary>
    /// Starts building an index without waiting for it. Navigation calls await the same
    /// task, so a query issued while indexing simply resolves when it completes.
    /// </summary>
    public IndexStatusPayload BeginIndexing(string worktreePath)
    {
        var index = GetOrCreate(worktreePath);

        if (index.State is IndexState.Idle)
        {
            _ = index.EnsureBuiltAsync()
                .ContinueWith(
                    _ => StatusChanged?.Invoke(StatusOf(index)),
                    TaskScheduler.Default);
        }

        return StatusOf(index);
    }

    public IndexStatusPayload Status(string worktreePath) => StatusOf(GetOrCreate(worktreePath));

    private static IndexStatusPayload StatusOf(WorkspaceIndex index) => new()
    {
        WorktreePath = index.WorktreePath,
        State = index.State.ToString().ToLowerInvariant(),
        FilesIndexed = index.FilesIndexed,
        SymbolCount = index.SymbolCount,
        ElapsedMs = index.ElapsedMs,
    };

    /// <summary>
    /// Resolves the identifier under a position to its declarations.
    ///
    /// Name-based, so an overloaded method or a type name reused across namespaces returns
    /// several candidates rather than one. That is surfaced as a chooser rather than
    /// guessed at — a wrong jump is worse than a short list.
    /// </summary>
    public async Task<IReadOnlyList<SymbolLocation>> GoToDefinitionAsync(
        string worktreePath, string repoRelativePath, int line, int column, CancellationToken ct = default)
    {
        var identifier = await IdentifierAtAsync(worktreePath, repoRelativePath, line, column, ct).ConfigureAwait(false);
        if (identifier is null) return [];

        var index = GetOrCreate(worktreePath);
        await index.EnsureBuiltAsync(ct).ConfigureAwait(false);

        return index.FindDeclarations(identifier)
            // A single-candidate jump to the line you are already on is a no-op that reads
            // as a broken feature; prefer any other declaration of the same name.
            .OrderBy(d => d.FilePath.Equals(repoRelativePath, StringComparison.OrdinalIgnoreCase) && d.Line == line ? 1 : 0)
            .Select(ToLocation)
            .ToArray();
    }

    public async Task<IReadOnlyList<SymbolLocation>> FindReferencesAsync(
        string worktreePath, string repoRelativePath, int line, int column, CancellationToken ct = default)
    {
        var identifier = await IdentifierAtAsync(worktreePath, repoRelativePath, line, column, ct).ConfigureAwait(false);
        if (identifier is null) return [];

        var index = GetOrCreate(worktreePath);
        await index.EnsureBuiltAsync(ct).ConfigureAwait(false);

        var references = await index.FindReferencesAsync(identifier, ct).ConfigureAwait(false);

        return references.Select(reference => new SymbolLocation
        {
            Path = reference.FilePath,
            Line = reference.Line,
            Column = reference.Column,
            EndLine = reference.Line,
            EndColumn = reference.EndColumn,
            Name = identifier,
            Kind = reference.IsDeclaration ? "declaration" : "reference",
            Preview = reference.Preview,
        }).ToArray();
    }

    public async Task<IReadOnlyList<SymbolLocation>> SearchSymbolsAsync(
        string worktreePath, string query, int limit, CancellationToken ct = default)
    {
        var index = GetOrCreate(worktreePath);
        await index.EnsureBuiltAsync(ct).ConfigureAwait(false);

        return index.SearchSymbols(query, limit).Select(ToLocation).ToArray();
    }

    public async Task<IReadOnlyList<string>> SearchFilesAsync(
        string worktreePath, string query, int limit, CancellationToken ct = default)
    {
        var index = GetOrCreate(worktreePath);
        await index.EnsureBuiltAsync(ct).ConfigureAwait(false);

        return index.SearchFiles(query, limit);
    }

    public async Task<IReadOnlyList<SymbolLocation>> DocumentSymbolsAsync(
        string worktreePath, string repoRelativePath, CancellationToken ct = default)
    {
        var index = GetOrCreate(worktreePath);
        await index.EnsureBuiltAsync(ct).ConfigureAwait(false);

        return index.DocumentSymbols(repoRelativePath).Select(ToLocation).ToArray();
    }

    /// <summary>
    /// Re-indexes a changed file so navigation stays correct as an agent works.
    ///
    /// Accepted while a build is still running, not just once it is Ready: opening the app
    /// on a worktree an agent is actively writing is the primary case, and the first few
    /// seconds are exactly when edits land. <see cref="WorkspaceIndex.ReindexFileAsync"/>
    /// queues them for the end of the build.
    /// </summary>
    public Task FileChangedAsync(string worktreePath, string repoRelativePath, CancellationToken ct = default)
    {
        if (!_indexes.TryGetValue(worktreePath, out var index)) return Task.CompletedTask;
        if (index.State is IndexState.Failed) return Task.CompletedTask;

        return index.ReindexFileAsync(repoRelativePath, ct);
    }

    public void Forget(string worktreePath)
    {
        _indexes.TryRemove(worktreePath, out _);
        _lastUsed.TryRemove(worktreePath, out _);
    }

    /// <summary>
    /// Discards a worktree's index so the next query rebuilds it from scratch. Used when
    /// the watcher reports dropped events, after which no incremental update is trustworthy.
    /// </summary>
    public void Invalidate(string worktreePath)
    {
        if (_indexes.TryRemove(worktreePath, out _))
            _indexes.TryAdd(worktreePath, new WorkspaceIndex(worktreePath, _indexer));
    }

    private async Task<string?> IdentifierAtAsync(
        string worktreePath, string repoRelativePath, int line, int column, CancellationToken ct)
    {
        if (!_indexer.CanIndex(repoRelativePath)) return null;

        var absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
        if (!File.Exists(absolute)) return null;

        // Re-parsing one file is a fraction of a millisecond, which is why syntax trees
        // are not retained after indexing.
        var text = await File.ReadAllTextAsync(absolute, ct).ConfigureAwait(false);
        return _indexer.IdentifierAt(text, line, column);
    }

    private static SymbolLocation ToLocation(SymbolDeclaration declaration) => new()
    {
        Path = declaration.FilePath,
        Line = declaration.Line,
        Column = declaration.Column,
        EndLine = declaration.EndLine,
        EndColumn = declaration.EndColumn,
        Name = declaration.Name,
        Kind = declaration.Kind.ToString().ToLowerInvariant(),
        ContainerName = declaration.ContainerName,
        Preview = declaration.Preview,
    };

    private void EvictIfNeeded()
    {
        if (_indexes.Count <= MaxResidentIndexes) return;

        foreach (var (path, _) in _lastUsed.OrderBy(entry => entry.Value).Take(_indexes.Count - MaxResidentIndexes))
            Forget(path);
    }
}
