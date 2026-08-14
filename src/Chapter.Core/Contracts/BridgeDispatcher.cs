using System.Text.Json;
using Chapter.Core.Editors;
using Chapter.Core.Git;
using Chapter.Core.Indexing;

namespace Chapter.Core.Contracts;

/// <summary>
/// Routes JSON requests from the front-end to the Core services and serialises the reply.
///
/// Knows nothing about WebView2 — it takes a JSON string and returns a JSON string — so
/// the whole protocol can be exercised without a window.
/// </summary>
public sealed class BridgeDispatcher(WorkspaceService workspace, AppSettings settings)
{
    public WorkspaceService Workspace { get; } = workspace;
    public AppSettings Settings { get; } = settings;

    private readonly EditorLauncher _editors = new(settings);
    private readonly WorktreeWatcher _watcher = new();

    public IndexService Index { get; } = new();

    /// <summary>
    /// Shows a folder picker. Supplied by the window because the dialog must run on the
    /// UI thread, which the dispatcher deliberately knows nothing about.
    /// </summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Raised when the backend wants to push an event to the front-end.</summary>
    public event Action<BridgeEvent>? EventRaised;

    public void RaiseEvent(string name, object? payload) =>
        EventRaised?.Invoke(new BridgeEvent { Event = name, Payload = payload });

    /// <summary>
    /// Starts watching worktrees and pushing change notifications. Called once at startup;
    /// individual worktrees are watched the first time they are read.
    /// </summary>
    public void StartWatching()
    {
        _watcher.Changed += OnWorktreeChanged;
        Index.StatusChanged += status => RaiseEvent("indexStatus", status);
    }

    private async void OnWorktreeChanged(
        string worktreePath, IReadOnlyList<string> paths, WorktreeWatcher.ChangeReason reason)
    {
        try
        {
            // The cached scan is now stale whatever the reason — a working-tree edit, a
            // commit, or dropped events.
            Workspace.InvalidateChanges(worktreePath);

            switch (reason)
            {
                case WorktreeWatcher.ChangeReason.Files:
                    // Keep navigation honest while an agent works: a stale index sends F12
                    // to the wrong line, which is worse than no index at all.
                    foreach (var path in paths)
                        await Index.FileChangedAsync(worktreePath, path).ConfigureAwait(false);
                    break;

                case WorktreeWatcher.ChangeReason.GitState:
                    // A commit or checkout leaves the working tree as it was, so the symbol
                    // index is still accurate — only the diff needs re-reading. Rebuilding
                    // here would cost seconds on a large repo for no benefit.
                    break;

                case WorktreeWatcher.ChangeReason.Overflow:
                    // Events were dropped, so no incremental update can be trusted.
                    Index.Invalidate(worktreePath);
                    break;
            }

            RaiseEvent("filesChanged", new { worktreePath });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Watcher refresh failed: {ex.Message}");
        }
    }

    public void Dispose() => _watcher.Dispose();

    public async Task<string> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(requestJson, BridgeJson.Options);
            if (request is null)
                return BridgeJson.Serialize(BridgeResponse.Failure(0, "Malformed request"));

            var result = await InvokeAsync(request, ct).ConfigureAwait(false);
            return BridgeJson.Serialize(BridgeResponse.Success(request.Id, result));
        }
        catch (Exception ex)
        {
            // One failing call must never take down the UI: report it and carry on.
            return BridgeJson.Serialize(BridgeResponse.Failure(request?.Id ?? 0, Describe(ex)));
        }
    }

    private async Task<object?> InvokeAsync(BridgeRequest request, CancellationToken ct) => request.Method switch
    {
        "ping" => "pong",

        "listRepos" => await ListReposAsync(ct).ConfigureAwait(false),

        "addRepo" => await AddRepoAsync(request.ParamsAs<RepoRequest>(), ct).ConfigureAwait(false),

        "removeRepo" => await RemoveRepoAsync(request.ParamsAs<RepoRequest>(), ct).ConfigureAwait(false),

        "getWorktrees" => await Workspace
            .GetWorktreesAsync(request.ParamsAs<RepoRequest>().RepoPath, ct).ConfigureAwait(false),

        "getChanges" => await GetChangesAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "getDiff" => await GetDiffAsync(request.ParamsAs<FileRequest>(), ct).ConfigureAwait(false),

        "getFileContent" => await GetFileContentAsync(request.ParamsAs<FileRequest>(), ct).ConfigureAwait(false),

        "getSettings" => Settings,

        "pickFolder" => FolderPicker is null ? null : await FolderPicker().ConfigureAwait(false),

        "listEditors" => _editors.Detect(),

        "openInEditor" => OpenInEditor(request.ParamsAs<OpenInEditorRequest>()),

        // --- navigation -----------------------------------------------------

        "ensureIndex" => Index.BeginIndexing(request.ParamsAs<WorktreeRequest>().WorktreePath),

        "goToDefinition" => await Navigate(request, Index.GoToDefinitionAsync, ct).ConfigureAwait(false),

        "findReferences" => await Navigate(request, Index.FindReferencesAsync, ct).ConfigureAwait(false),

        "searchSymbols" => await SearchSymbolsAsync(request.ParamsAs<SearchRequest>(), ct).ConfigureAwait(false),

        "searchFiles" => await SearchFilesAsync(request.ParamsAs<SearchRequest>(), ct).ConfigureAwait(false),

        "documentSymbols" => await DocumentSymbolsAsync(request.ParamsAs<FileRequest>(), ct).ConfigureAwait(false),

        _ => throw new InvalidOperationException($"Unknown method '{request.Method}'"),
    };

    private delegate Task<IReadOnlyList<SymbolLocation>> NavigationQuery(
        string worktreePath, string path, int line, int column, CancellationToken ct);

    private static async Task<object> Navigate(BridgeRequest request, NavigationQuery query, CancellationToken ct)
    {
        var req = request.ParamsAs<NavigationRequest>();
        return await query(req.WorktreePath, req.Path, req.Line, req.Column, ct).ConfigureAwait(false);
    }

    private async Task<object> SearchSymbolsAsync(SearchRequest req, CancellationToken ct) =>
        await Index.SearchSymbolsAsync(req.WorktreePath, req.Query, req.Limit, ct).ConfigureAwait(false);

    private async Task<object> SearchFilesAsync(SearchRequest req, CancellationToken ct) =>
        await Index.SearchFilesAsync(req.WorktreePath, req.Query, req.Limit, ct).ConfigureAwait(false);

    private async Task<object> DocumentSymbolsAsync(FileRequest req, CancellationToken ct) =>
        await Index.DocumentSymbolsAsync(req.WorktreePath, req.Path, ct).ConfigureAwait(false);

    private bool OpenInEditor(OpenInEditorRequest req) =>
        _editors.Open(req.WorktreePath, req.Path, req.Line, req.Column, req.Editor);

    /// <summary>
    /// Recent repositories, normalised to their main worktree. A path recorded from the
    /// command line — or a folder the user picked — may well be a linked worktree, and
    /// registering that as the repository would list the same worktrees under two names.
    /// </summary>
    private async Task<object> ListReposAsync(CancellationToken ct)
    {
        var repos = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();

        foreach (var recorded in Settings.RecentRepos.ToArray())
        {
            if (!Directory.Exists(recorded))
            {
                stale.Add(recorded);
                continue;
            }

            string? resolved;
            try
            {
                resolved = await Workspace.AddRepoAsync(recorded, ct).ConfigureAwait(false);
            }
            catch (GitException)
            {
                resolved = null; // Not a repository any more, or git refused to read it.
            }

            if (resolved is null)
            {
                stale.Add(recorded);
                continue;
            }

            if (seen.Add(resolved))
                repos.Add(new { path = resolved, name = Path.GetFileName(resolved.TrimEnd('\\', '/')) });
        }

        if (stale.Count > 0)
        {
            Settings.RecentRepos.RemoveAll(stale.Contains);
            Settings.Save();
        }

        return repos;
    }

    private async Task<object?> AddRepoAsync(RepoRequest req, CancellationToken ct)
    {
        var repoPath = await Workspace.AddRepoAsync(req.RepoPath, ct).ConfigureAwait(false);
        if (repoPath is null) return null;

        Settings.RecordRepo(repoPath);
        Settings.Save();
        return new { path = repoPath, name = Path.GetFileName(repoPath.TrimEnd('\\', '/')) };
    }

    private async Task<bool> RemoveRepoAsync(RepoRequest req, CancellationToken ct)
    {
        // Release the backend resources this repo's worktrees hold. Without this a closed
        // repo keeps a recursive FileSystemWatcher and a symbol index alive per worktree
        // for the rest of the session, still re-parsing files nobody is looking at.
        try
        {
            foreach (var worktree in await Workspace.GetWorktreesAsync(req.RepoPath, ct).ConfigureAwait(false))
            {
                _watcher.Unwatch(worktree.Path);
                Index.Forget(worktree.Path);
            }
        }
        catch (GitException)
        {
            // The repo is already gone from disk; nothing to enumerate, nothing to free.
        }

        Workspace.RemoveRepo(req.RepoPath);
        Settings.RecentRepos.RemoveAll(r => string.Equals(r, req.RepoPath, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        return true;
    }

    private async Task<object> GetChangesAsync(WorktreeRequest req, CancellationToken ct)
    {
        var worktree = await FindWorktreeAsync(req.WorktreePath, ct).ConfigureAwait(false);

        if (worktree.IsUsable)
        {
            // The git directory has to be watched alongside the working tree, or commits —
            // which touch no working-tree file, and for a linked worktree live outside it
            // entirely — go unnoticed.
            var gitDir = await ResolveGitDirAsync(worktree.Path, ct).ConfigureAwait(false);
            _watcher.Watch(worktree.Path, gitDir);
        }

        return await Workspace.GetChangesAsync(worktree, req.Scope, ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveGitDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await Workspace.Git
            .TryRunAsync(worktreePath, ct, "rev-parse", "--absolute-git-dir")
            .ConfigureAwait(false);

        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    private async Task<object> GetDiffAsync(FileRequest req, CancellationToken ct) =>
        await Workspace.GetDiffAsync(req.WorktreePath, req.Path, req.Scope, ct).ConfigureAwait(false);

    private async Task<object> GetFileContentAsync(FileRequest req, CancellationToken ct) =>
        await Workspace.GetFileContentAsync(req.WorktreePath, req.Path, req.Scope, ct).ConfigureAwait(false);

    /// <summary>
    /// Resolves a path to the worktree git reports for it, so branch name and prunable
    /// state come from git rather than being guessed from the directory.
    /// </summary>
    private async Task<Worktree> FindWorktreeAsync(string worktreePath, CancellationToken ct)
    {
        var worktrees = await Workspace.GetWorktreesAsync(worktreePath, ct).ConfigureAwait(false);

        return worktrees.FirstOrDefault(w => string.Equals(w.Path, worktreePath, StringComparison.OrdinalIgnoreCase))
               ?? new Worktree { Path = worktreePath };
    }

    private static string Describe(Exception ex) => ex switch
    {
        GitException git => git.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
