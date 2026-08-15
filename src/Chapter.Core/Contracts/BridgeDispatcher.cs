using System.Text.Json;
using Chapter.Core.Ai;
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
public sealed class BridgeDispatcher
{
    public WorkspaceService Workspace { get; }
    public AppSettings Settings { get; }

    private readonly EditorLauncher _editors;
    private readonly WorktreeWatcher _watcher = new();

    public IndexService Index { get; } = new();

    /// <summary>Where the Claude API key lives. Never <c>settings.json</c>.</summary>
    public ApiKeyStore Keys { get; }

    /// <summary>Writes commit messages, when asked and when a credential exists.</summary>
    public CommitMessageGenerator Generator { get; }

    public BridgeDispatcher(WorkspaceService workspace, AppSettings settings, ApiKeyStore? keys = null)
    {
        Workspace = workspace;
        Settings = settings;
        _editors = new EditorLauncher(settings);

        Keys = keys ?? new ApiKeyStore();
        Generator = new CommitMessageGenerator(workspace.Git, settings, Keys, workspace.Log);

        // Close the loop the app would otherwise run against itself: the writer opens a
        // window on the watcher for the duration of every mutation, so the watcher can tell
        // the app's writes from an agent's.
        Workspace.Writer.SelfWriteScope = _watcher.BeginSelfWrite;
    }

    /// <summary>
    /// Shows a folder picker. Supplied by the window because the dialog must run on the
    /// UI thread, which the dispatcher deliberately knows nothing about.
    /// </summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>
    /// Notifies the host that the theme changed, so it can repaint the native window
    /// caption to match. Supplied by the window for the same reason as the picker: only
    /// it has the handle, and the dispatcher deliberately knows nothing about windows.
    /// </summary>
    public Action<string>? ThemeChanged { get; set; }

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
        Workspace.Undo.StackChanged += worktreePath => RaiseEvent("undoChanged", new { worktreePath });
        Workspace.Log.Appended += entry => RaiseEvent("operationLogged", entry);

        // Generation outlives the call that started it — the bridge would time out long before
        // a model does — so its text comes back the same way a file change does.
        Generator.Progress += progress => RaiseEvent("messageDelta", progress);
        Generator.Finished += result => RaiseEvent("messageGenerated", result);
    }

    private async void OnWorktreeChanged(WorktreeWatcher.WorktreeChange change)
    {
        try
        {
            // The cached scan is now stale whatever the reason — a working-tree edit, a
            // commit, or dropped events. This happens even for the app's own writes: they
            // changed the repository just as much as anybody else's.
            Workspace.InvalidateChanges(change.WorktreePath);

            switch (change.Reason)
            {
                case WorktreeWatcher.ChangeReason.Files:
                    // Keep navigation honest while an agent works: a stale index sends F12
                    // to the wrong line, which is worse than no index at all.
                    foreach (var path in change.Paths)
                        await Index.FileChangedAsync(change.WorktreePath, path).ConfigureAwait(false);
                    break;

                case WorktreeWatcher.ChangeReason.GitState:
                    // A commit or checkout leaves the working tree as it was, so the symbol
                    // index is still accurate — only the diff needs re-reading. Rebuilding
                    // here would cost seconds on a large repo for no benefit.
                    //
                    // The repository state is a different matter: this signal is exactly what
                    // an agent starting a merge looks like, and the write guard must not go
                    // on answering from a reading taken before it.
                    Workspace.InvalidateState(change.WorktreePath);
                    break;

                case WorktreeWatcher.ChangeReason.Overflow:
                    // Events were dropped, so no incremental update can be trusted.
                    Index.Invalidate(change.WorktreePath);
                    Workspace.InvalidateState(change.WorktreePath);
                    break;
            }

            // Announced whether or not the batch was ours.
            //
            // Dropping self-originated batches was the obvious saving and it is not safe:
            // attribution is by time window, not by path, so an agent writing during the
            // few hundred milliseconds around one of the app's own mutations is credited to
            // the app and its change never reaches the UI. The justification for dropping —
            // "the mutation announces its own" — only ever covered the app's write, never
            // the agent's that got swept up with it.
            //
            // The cost of announcing anyway is one redundant refresh after a mutation. The
            // cost of the alternative is failing to show what an agent did, which is the
            // one thing this app exists for. The tag is still carried, for the operation
            // log and for Phase 1 to coalesce by path once it knows exactly what it wrote.
            RaiseEvent("filesChanged", new
            {
                worktreePath = change.WorktreePath,
                selfOriginated = change.SelfOriginated,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Watcher refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Announces a change the app itself made, immediately, rather than waiting for the
    /// watcher's debounce.
    ///
    /// Called after every mutation whether or not it succeeded: a command that fails
    /// part-way — a merge stopping on conflicts is the ordinary case — has still changed
    /// the working tree, and announcing only on success leaves the UI showing the state
    /// from before it.
    /// </summary>
    private void AnnounceSelfWrite(string worktreePath)
    {
        Workspace.InvalidateChanges(worktreePath);
        RaiseEvent("filesChanged", new { worktreePath, selfOriginated = true });
    }

    public void Dispose()
    {
        // Unhooked before the watcher goes, and the writer's hook into it cleared: the
        // workspace outlives this dispatcher in tests and in any future multi-window host,
        // and a mutation afterwards would otherwise open a scope on a disposed watcher.
        _watcher.Changed -= OnWorktreeChanged;
        Workspace.Writer.SelfWriteScope = null;

        // A generation in flight would otherwise go on holding an HTTP connection open and
        // raising events at a window that has gone.
        Generator.CancelAll();

        _watcher.Dispose();
    }

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

        "getAsset" => await GetAssetAsync(request.ParamsAs<FileRequest>(), ct).ConfigureAwait(false),

        // --- writing ---------------------------------------------------------

        "getRepositoryState" => await Workspace
            .GetRepositoryStateAsync(request.ParamsAs<WorktreeRequest>().WorktreePath, ct).ConfigureAwait(false),

        "saveFile" => await SaveFileAsync(request.ParamsAs<SaveFileRequest>(), ct).ConfigureAwait(false),

        // --- staging and committing ------------------------------------------

        "getCommitView" => await GetCommitViewAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "stage" => await MutateAsync(
            request.ParamsAs<StageRequest>(),
            (req, token) => Workspace.Staging.StageAsync(req.WorktreePath, req.Paths, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "unstage" => await MutateAsync(
            request.ParamsAs<StageRequest>(),
            (req, token) => Workspace.Staging.UnstageAsync(req.WorktreePath, req.Paths, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "discard" => await MutateAsync(
            request.ParamsAs<StageRequest>(),
            (req, token) => Workspace.Staging.DiscardAsync(
                req.WorktreePath, req.Paths, req.Target, req.Untracked, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "getFilePatch" => await GetFilePatchAsync(request.ParamsAs<FileRequest>(), ct).ConfigureAwait(false),

        "applyPatch" => await MutateAsync(
            request.ParamsAs<PatchRequest>(),
            (req, token) => Workspace.ApplyPatchAsync(req, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "commit" => await MutateAsync(
            request.ParamsAs<CommitCommandRequest>(),
            (req, token) => Workspace.Commits.CommitAsync(req.WorktreePath, ToCommitRequest(req), token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "reviewMessage" => await ReviewMessageAsync(
            request.ParamsAs<MessageReviewRequest>(), ct).ConfigureAwait(false),

        // --- generated commit messages ----------------------------------------

        "getAiStatus" => Generator.Describe(),

        "setApiKey" => SetApiKey(request.ParamsAs<ApiKeyRequest>()),

        "generateCommitMessage" => StartGeneration(request.ParamsAs<GenerateMessageRequest>()),

        "cancelGeneration" => Generator.Cancel(request.ParamsAs<CancelGenerationRequest>().Id),

        "getUndo" => await GetUndoAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "undo" => await UndoAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "getOperationLog" => Workspace.Log.Recent(request.ParamsAs<OperationLogRequest>().Limit),

        "getSettings" => Settings,

        "setTheme" => SetTheme(request.ParamsAs<SetThemeRequest>()),

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
    /// Records the theme and lets the host repaint its chrome.
    ///
    /// Unrecognised values fall back to "system" rather than being stored: the settings
    /// file is hand-editable, and a typo here would otherwise persist a theme neither
    /// side knows how to render.
    /// </summary>
    private bool SetTheme(SetThemeRequest req)
    {
        var theme = req.Theme is "dark" or "light" ? req.Theme : "system";

        Settings.Theme = theme;
        Settings.Save();
        ThemeChanged?.Invoke(theme);

        return true;
    }

    private async Task<object> SaveFileAsync(SaveFileRequest req, CancellationToken ct)
    {
        var result = await Workspace
            .SaveFileAsync(req.WorktreePath, req.Path, req.Text, ct)
            .ConfigureAwait(false);

        // Announced even on failure: the atomic write can fail after the rename, and a
        // refused save still costs nothing to refresh from.
        AnnounceSelfWrite(req.WorktreePath);

        return new SavePayload
        {
            Path = result.Path,
            Ok = result.Success,
            Error = result.Error,
            BytesWritten = result.BytesWritten,
        };
    }

    /// <summary>
    /// Runs a mutation and announces it, whatever the outcome.
    ///
    /// Every staging and commit method needs the same three things afterwards — the cached
    /// scan dropped, the repository state re-read, and the front-end told — and a mutation
    /// that skipped any of them would leave the window showing the state from before it.
    /// Failure is not an exception to that: a discard that could not delete one file has
    /// still deleted the others, and a commit refused for unmerged paths has still had the
    /// index read.
    /// </summary>
    private async Task<object> MutateAsync<TRequest>(
        TRequest request,
        Func<TRequest, CancellationToken, Task<GitMutation>> run,
        Func<TRequest, string> worktreeOf,
        CancellationToken ct)
    {
        var worktreePath = worktreeOf(request);

        if (!Workspace.IsKnownWorktree(worktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        try
        {
            var mutation = await run(request, ct).ConfigureAwait(false);
            return MutationPayload.From(mutation);
        }
        finally
        {
            // The repository state looks after itself — GitWriter.Mutated drops it as part
            // of running the command, so no call site has to remember.
            AnnounceSelfWrite(worktreePath);
        }
    }

    /// <summary>
    /// The hunks of a file as git divides them, which is what the staging controls have to
    /// be drawn from — Monaco groups its own diff differently, and a control placed on one
    /// of its regions would name a hunk the user never saw.
    /// </summary>
    private async Task<object> GetFilePatchAsync(FileRequest req, CancellationToken ct)
    {
        var side = req.Side is DiffSide.Combined ? DiffSide.Unstaged : req.Side;

        var patch = await PatchBuilder
            .ReadAsync(Workspace.Git, req.WorktreePath, req.Path, side, ct: ct)
            .ConfigureAwait(false);

        return FilePatchPayload.From(patch, req.Path, side);
    }

    private async Task<object> GetCommitViewAsync(WorktreeRequest req, CancellationToken ct)
    {
        var state = await Workspace.GetCommitViewAsync(req.WorktreePath, ct).ConfigureAwait(false);
        var payload = CommitViewPayload.From(state);

        var (name, email) = await Workspace.Commits
            .ReadIdentityAsync(req.WorktreePath, ct).ConfigureAwait(false);

        // Read unconditionally rather than only when amending: the commit box offers amend
        // as a toggle, and fetching the message at the moment the toggle flips would put a
        // round-trip in the middle of a keystroke.
        var headMessage = state.IsUnborn
            ? null
            : await Workspace.Commits.ReadHeadMessageAsync(req.WorktreePath, ct).ConfigureAwait(false);

        return payload with { AuthorName = name, AuthorEmail = email, HeadMessage = headMessage };
    }

    private async Task<object> ReviewMessageAsync(MessageReviewRequest req, CancellationToken ct)
    {
        var policy = Settings.CommitPolicyFor(req.WorktreePath);
        var review = CommitMessageReader.Review(req.Message, policy);

        var recent = await CommitMessageReader
            .RecentSubjectsAsync(Workspace.Git, req.WorktreePath, 20, ct)
            .ConfigureAwait(false);

        return MessageReviewPayload.From(review, recent);
    }

    private static CommitRequest ToCommitRequest(CommitCommandRequest req) => new()
    {
        Message = req.Message,
        Amend = req.Amend,
        SignOff = req.SignOff,
        Sign = req.Sign,
        // Anything without an address is dropped rather than sent: git accepts the trailer
        // and every tool that reads them ignores it, which looks like the app losing it.
        CoAuthors = req.CoAuthors
            .Select(CoAuthor.Parse)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToArray(),
    };

    /// <summary>
    /// Stores or forgets the API key.
    ///
    /// Returns the resulting availability rather than a bare boolean, because the question the
    /// UI is really asking is "can I generate now" — and answering it here saves a second call
    /// on the one path where the answer has just changed.
    /// </summary>
    private object SetApiKey(ApiKeyRequest req)
    {
        // Stored against whichever provider is configured. The prompt that collected it named
        // that provider's environment variable, so this is the one it is for.
        var error = Keys.Store(CommitMessageGenerator.NormaliseProvider(Settings.Ai.Provider), req.Key);

        return new ApiKeyPayload
        {
            Ok = error is null,
            Error = error,
            Status = Generator.Describe(),
        };
    }

    /// <summary>
    /// Begins a generation and returns its id at once.
    ///
    /// Deliberately not awaited: the bridge gives up on a call after sixty seconds, and a
    /// model call is the first thing this app does that can legitimately take longer than
    /// that. The text arrives on the event channel instead, which is the shape the roadmap's
    /// cross-cutting "long-running operations" item asks for and this is its first use.
    ///
    /// The worktree is checked the same way every mutation checks it. Generation does not
    /// write to the repository, but it does read the whole staged diff and send it to an API —
    /// which makes an unchecked path a way to exfiltrate any repository on the machine, not
    /// merely a way to read one.
    /// </summary>
    private object StartGeneration(GenerateMessageRequest req)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        var status = Generator.Describe();
        if (!status.Available)
            throw new InvalidOperationException(status.Reason ?? "Message generation is unavailable.");

        var id = Generator.Begin(req.WorktreePath, req.Amend, Math.Clamp(req.Count, 1, 5));

        return new GenerationStartedPayload { Id = id, WorktreePath = req.WorktreePath };
    }

    private async Task<object> GetUndoAsync(WorktreeRequest req, CancellationToken ct)
    {
        var point = Workspace.Undo.Peek(req.WorktreePath);
        var reflog = await Workspace.Undo.ReadReflogAsync(req.WorktreePath, 25, ct).ConfigureAwait(false);

        return new UndoPayload
        {
            Label = point?.Label,
            IsDestructive = point?.IsDestructive ?? false,
            Warning = point?.Warning,
            Reflog = reflog,
        };
    }

    private async Task<object> UndoAsync(WorktreeRequest req, CancellationToken ct)
    {
        var mutation = await Workspace.Undo.UndoAsync(req.WorktreePath, ct).ConfigureAwait(false);

        // A failed undo can still have moved something — `reset` is not all-or-nothing — so
        // the refresh is unconditional.
        AnnounceSelfWrite(req.WorktreePath);

        return MutationPayload.From(mutation);
    }

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
        await Workspace.GetDiffAsync(req.WorktreePath, req.Path, req.Scope, req.Side, ct).ConfigureAwait(false);

    private async Task<object> GetFileContentAsync(FileRequest req, CancellationToken ct) =>
        await Workspace.GetFileContentAsync(req.WorktreePath, req.Path, req.Scope, ct).ConfigureAwait(false);

    private static async Task<object> GetAssetAsync(FileRequest req, CancellationToken ct) =>
        await WorkspaceService.GetAssetAsync(req.WorktreePath, req.Path, ct).ConfigureAwait(false);

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
