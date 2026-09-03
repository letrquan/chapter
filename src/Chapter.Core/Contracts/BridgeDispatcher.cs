using System.Text.Json;
using Chapter.Core.AgentSessions;
using Chapter.Core.Ai;
using Chapter.Core.Editors;
using Chapter.Core.Git;
using Chapter.Core.Indexing;
using Chapter.Core.Updates;

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
    private int _disposed;

    public IndexService Index { get; } = new();

    /// <summary>Where the Claude API key lives. Never <c>settings.json</c>.</summary>
    public ApiKeyStore Keys { get; }

    /// <summary>Writes commit messages, when asked and when a credential exists.</summary>
    public CommitMessageGenerator Generator { get; }
    public ReviewWatermarkService ReviewWatermarks { get; }
    public AgentSessionService AgentSessions { get; }

    public BridgeDispatcher(
        WorkspaceService workspace,
        AppSettings settings,
        ApiKeyStore? keys = null,
        AgentSessionService? agentSessions = null)
    {
        Workspace = workspace;
        Settings = settings;
        _editors = new EditorLauncher(settings);

        Keys = keys ?? new ApiKeyStore();
        Generator = new CommitMessageGenerator(workspace.Git, settings, Keys, workspace.Log);
        ReviewWatermarks = new ReviewWatermarkService(workspace.Git, settings);
        AgentSessions = agentSessions ?? new AgentSessionService();

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

    /// <summary>
    /// Opens a validated local session log in the user's associated application. Supplied by
    /// the window because launching shell handlers is a host concern, like the folder picker.
    /// </summary>
    public Func<string, bool>? OpenExternalPath { get; set; }

    /// <summary>
    /// Self-update, when the shell has one to give. Supplied by the window for the same
    /// reason as the picker and the theme hook: whether this copy can replace itself is a
    /// fact about how Windows installed it, and Core deliberately knows nothing about that.
    ///
    /// Null in the test host, and in any future shell without an installer behind it. That
    /// is a supported state, not a broken one — it answers <see cref="UpdateState.Unmanaged"/>.
    /// </summary>
    public IUpdater? Updater { get; set; }

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

        Workspace.Remotes.Progress += OnRemoteProgress;
        Workspace.Remotes.Finished += OnRemoteFinished;
        Workspace.Clones.Progress += OnCloneProgress;
        Workspace.Clones.Finished += OnCloneFinished;

        // An update check outlives the call that asked for it, and the automatic one at
        // startup was never asked for by the page at all, so both report the same way.
        if (Updater is not null)
            Updater.StatusChanged += status => RaiseEvent("updateStatus", status);
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
                    RaiseEvent("historyChanged", new { worktreePath = change.WorktreePath });
                    RaiseEvent("rebaseChanged", new { worktreePath = change.WorktreePath });
                    RaiseEvent("conflictChanged", new { worktreePath = change.WorktreePath });
                    break;

                case WorktreeWatcher.ChangeReason.Overflow:
                    // Events were dropped, so no incremental update can be trusted.
                    Index.Invalidate(change.WorktreePath);
                    Workspace.InvalidateState(change.WorktreePath);
                    RaiseEvent("historyChanged", new { worktreePath = change.WorktreePath });
                    RaiseEvent("rebaseChanged", new { worktreePath = change.WorktreePath });
                    RaiseEvent("conflictChanged", new { worktreePath = change.WorktreePath });
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
        RaiseEvent("rebaseChanged", new { worktreePath });
        RaiseEvent("conflictChanged", new { worktreePath });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Unhooked before the watcher goes, and the writer's hook into it cleared: the
        // workspace outlives this dispatcher in tests and in any future multi-window host,
        // and a mutation afterwards would otherwise open a scope on a disposed watcher.
        _watcher.Changed -= OnWorktreeChanged;
        Workspace.Writer.SelfWriteScope = null;

        // Unsubscribed before anything is cancelled, not after: cancelling raises the
        // terminal Finished event, and with the handlers still attached that event ran the
        // full invalidate-and-announce path against a window that is already going. The
        // handlers also check _disposed, because a callback can already be in flight.
        Workspace.Remotes.Progress -= OnRemoteProgress;
        Workspace.Remotes.Finished -= OnRemoteFinished;
        Workspace.Clones.Progress -= OnCloneProgress;
        Workspace.Clones.Finished -= OnCloneFinished;

        // A generation in flight would otherwise go on holding an HTTP connection open and
        // raising events at a window that has gone.
        Generator.CancelAll();
        Workspace.Remotes.CancelAll();
        Workspace.Clones.CancelAll();

        // The rebase service's temporary editor directories are the workspace's to release,
        // and it is now IDisposable so that whoever owns its lifetime can. Nothing of the
        // workspace is torn down from here.
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

        "getHistory" => await GetHistoryAsync(request.ParamsAs<HistoryRequest>(), ct)
            .ConfigureAwait(false),

        "searchHistory" => await SearchHistoryAsync(request.ParamsAs<HistorySearchRequest>(), ct)
            .ConfigureAwait(false),

        "getCommitDetail" => await GetCommitDetailAsync(
            request.ParamsAs<CommitDetailRequest>(), ct).ConfigureAwait(false),

        "getCommitFileDiff" => await GetCommitFileDiffAsync(
            request.ParamsAs<CommitFileDiffRequest>(), ct).ConfigureAwait(false),

        "getFileHistory" => await GetFileHistoryAsync(
            request.ParamsAs<FileHistoryRequest>(), ct).ConfigureAwait(false),

        "getBlame" => await GetBlameAsync(
            request.ParamsAs<BlameRequest>(), ct).ConfigureAwait(false),

        "getRebasePlan" => await GetRebasePlanAsync(
            request.ParamsAs<RebasePlanRequest>(), ct).ConfigureAwait(false),

        // Friendly aliases kept for clients that name the feature explicitly.
        "getInteractiveRebasePlan" => await GetRebasePlanAsync(
            request.ParamsAs<RebasePlanRequest>(), ct).ConfigureAwait(false),

        "startRebase" => await MutateAsync(
            request.ParamsAs<StartRebaseRequest>(),
            (req, token) => Workspace.Rebases.StartAsync(
                req.WorktreePath,
                req.Upstream,
                req.Entries.Select(entry => new RebaseTodoEntry
                {
                    Sha = entry.Sha,
                    Subject = "",
                    Action = entry.Action,
                    Message = entry.Message,
                }).ToArray(),
                req.ExpectedHead,
                token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "startInteractiveRebase" => await MutateAsync(
            request.ParamsAs<StartRebaseRequest>(),
            (req, token) => Workspace.Rebases.StartAsync(
                req.WorktreePath,
                req.Upstream,
                req.Entries.Select(entry => new RebaseTodoEntry
                {
                    Sha = entry.Sha,
                    Subject = "",
                    Action = entry.Action,
                    Message = entry.Message,
                }).ToArray(),
                req.ExpectedHead,
                token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "getRebaseState" => await GetRebaseStateAsync(
            request.ParamsAs<RebaseStateRequest>(), ct).ConfigureAwait(false),

        "rebaseState" => await GetRebaseStateAsync(
            request.ParamsAs<RebaseStateRequest>(), ct).ConfigureAwait(false),

        "continueRebase" => await MutateAsync(
            request.ParamsAs<ContinueRebaseRequest>(),
            (req, token) => Workspace.Rebases.ContinueAsync(req.WorktreePath, req.Message, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "rebaseContinue" => await MutateAsync(
            request.ParamsAs<ContinueRebaseRequest>(),
            (req, token) => Workspace.Rebases.ContinueAsync(req.WorktreePath, req.Message, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "skipRebase" => await MutateAsync(
            request.ParamsAs<RebaseStateRequest>(),
            (req, token) => Workspace.Rebases.SkipAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "rebaseSkip" => await MutateAsync(
            request.ParamsAs<RebaseStateRequest>(),
            (req, token) => Workspace.Rebases.SkipAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "abortRebase" => await MutateAsync(
            request.ParamsAs<RebaseStateRequest>(),
            (req, token) => Workspace.Rebases.AbortAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "rebaseAbort" => await MutateAsync(
            request.ParamsAs<RebaseStateRequest>(),
            (req, token) => Workspace.Rebases.AbortAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        // --- shared conflict resolution ------------------------------------

        "getConflictState" => await GetConflictStateAsync(
            request.ParamsAs<ConflictStateRequest>(), ct).ConfigureAwait(false),

        "conflictState" => await GetConflictStateAsync(
            request.ParamsAs<ConflictStateRequest>(), ct).ConfigureAwait(false),

        "getConflictFile" => await GetConflictFileAsync(
            request.ParamsAs<ConflictFileRequest>(), ct).ConfigureAwait(false),

        "resolveConflict" => await MutateAsync(
            request.ParamsAs<ResolveConflictRequest>(),
            (req, token) => Workspace.Conflicts.ResolveFileAsync(
                req.WorktreePath, req.Path, req.Action, req.ManualText, token,
                req.Region, req.Fingerprint),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "markResolved" => await MutateAsync(
            request.ParamsAs<MarkResolvedRequest>(),
            (req, token) => Workspace.Conflicts.MarkResolvedAsync(
                req.WorktreePath, req.Path, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "continueOperation" => await MutateAsync(
            request.ParamsAs<ConflictOperationRequest>(),
            (req, token) => Workspace.Conflicts.ContinueAsync(
                req.WorktreePath, req.Message, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "skipOperation" => await MutateAsync(
            request.ParamsAs<ConflictOperationRequest>(),
            (req, token) => Workspace.Conflicts.SkipAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "abortOperation" => await MutateAsync(
            request.ParamsAs<ConflictOperationRequest>(),
            (req, token) => Workspace.Conflicts.AbortAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "enableRerere" => await MutateAsync(
            request.ParamsAs<ConflictStateRequest>(),
            (req, token) => Workspace.Conflicts.EnableRerereAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "applyRerere" => await MutateAsync(
            request.ParamsAs<ConflictStateRequest>(),
            (req, token) => Workspace.Conflicts.ApplyRerereAsync(req.WorktreePath, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "rerereStatus" => await GetRerereStatusAsync(
            request.ParamsAs<ConflictStateRequest>(), ct).ConfigureAwait(false),

        "forgetRerere" => await MutateAsync(
            request.ParamsAs<ForgetRerereRequest>(),
            (req, token) => Workspace.Conflicts.ForgetRerereAsync(req.WorktreePath, req.Paths, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "cherryPick" => await MutateAsync(
            request.ParamsAs<CherryPickRequest>(),
            (req, token) => Workspace.HistoryMutations.CherryPickAsync(
                req.WorktreePath, req.Sha, req.ParentIndex, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        // Keep the hyphenated spelling accepted for callers that mirror Git's command name.
        "cherry-pick" => await MutateAsync(
            request.ParamsAs<CherryPickRequest>(),
            (req, token) => Workspace.HistoryMutations.CherryPickAsync(
                req.WorktreePath, req.Sha, req.ParentIndex, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "revert" => await MutateAsync(
            request.ParamsAs<RevertRequest>(),
            (req, token) => Workspace.HistoryMutations.RevertAsync(
                req.WorktreePath, req.Sha, req.ParentIndex, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "revertCommit" => await MutateAsync(
            request.ParamsAs<RevertRequest>(),
            (req, token) => Workspace.HistoryMutations.RevertAsync(
                req.WorktreePath, req.Sha, req.ParentIndex, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "getChanges" => await GetChangesAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "getWorktreeComparison" => await GetWorktreeComparisonAsync(
            request.ParamsAs<WorktreeComparisonRequest>(), ct).ConfigureAwait(false),

        "compareWorktrees" => await GetWorktreeComparisonAsync(
            request.ParamsAs<WorktreeComparisonRequest>(), ct).ConfigureAwait(false),

        "getWorktreeComparisonFile" => await GetWorktreeComparisonFileAsync(
            request.ParamsAs<WorktreeComparisonFileRequest>(), ct).ConfigureAwait(false),

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

        // --- branches, stash and tags -----------------------------------------

        "getRefs" => await GetRefsAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "switchBranch" => await MutateBranchAsync(
            request.ParamsAs<SwitchBranchRequest>(),
            (req, token) => Workspace.Branches.SwitchAsync(req.WorktreePath, req.Branch, req.Strategy, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "createBranch" => await MutateBranchAsync(
            request.ParamsAs<CreateBranchRequest>(),
            (req, token) => Workspace.Branches.CreateAsync(
                req.WorktreePath, req.Name, req.StartPoint, req.Checkout, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "renameBranch" => await MutateBranchAsync(
            request.ParamsAs<RenameBranchRequest>(),
            (req, token) => Workspace.Branches.RenameAsync(req.WorktreePath, req.From, req.To, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "deleteBranch" => await MutateBranchAsync(
            request.ParamsAs<DeleteBranchRequest>(),
            (req, token) => Workspace.Branches.DeleteAsync(req.WorktreePath, req.Name, req.Force, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "setUpstream" => await MutateAsync(
            request.ParamsAs<SetUpstreamRequest>(),
            (req, token) => Workspace.Branches.SetUpstreamAsync(
                req.WorktreePath, req.Branch, req.Upstream, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "stashPush" => await MutateAsync(
            request.ParamsAs<StashPushRequest>(),
            (req, token) => Workspace.Stashes.PushAsync(
                req.WorktreePath, req.Message, req.IncludeUntracked, req.KeepIndex, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "stashApply" => await MutateAsync(
            request.ParamsAs<StashEntryRequest>(),
            (req, token) => Workspace.Stashes.ApplyAsync(req.WorktreePath, req.Index, req.Sha, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "stashPop" => await MutateAsync(
            request.ParamsAs<StashEntryRequest>(),
            (req, token) => Workspace.Stashes.PopAsync(req.WorktreePath, req.Index, req.Sha, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "stashDrop" => await MutateAsync(
            request.ParamsAs<StashEntryRequest>(),
            (req, token) => Workspace.Stashes.DropAsync(req.WorktreePath, req.Index, req.Sha, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "createTag" => await MutateAsync(
            request.ParamsAs<CreateTagRequest>(),
            (req, token) => Workspace.Tags.CreateAsync(
                req.WorktreePath, req.Name, req.Message, req.Target, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "deleteTag" => await MutateAsync(
            request.ParamsAs<DeleteTagRequest>(),
            (req, token) => Workspace.Tags.DeleteAsync(req.WorktreePath, req.Name, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        // --- remotes ----------------------------------------------------------

        "getRemotes" => await GetRemotesAsync(
            request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "addRemote" => await MutateAsync(
            request.ParamsAs<AddRemoteRequest>(),
            (req, token) => Workspace.Remotes.AddAsync(req.WorktreePath, req.Name, req.Url, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "renameRemote" => await MutateAsync(
            request.ParamsAs<RenameRemoteRequest>(),
            (req, token) => Workspace.Remotes.RenameAsync(req.WorktreePath, req.From, req.To, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "removeRemote" => await MutateAsync(
            request.ParamsAs<RemoteNameRequest>(),
            (req, token) => Workspace.Remotes.RemoveAsync(req.WorktreePath, req.Name, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "pruneRemote" => await MutateAsync(
            request.ParamsAs<RemoteNameRequest>(),
            (req, token) => Workspace.Remotes.PruneAsync(req.WorktreePath, req.Name, token),
            req => req.WorktreePath, ct).ConfigureAwait(false),

        "previewPruneRemote" => await PreviewPruneRemoteAsync(
            request.ParamsAs<RemoteNameRequest>(), ct).ConfigureAwait(false),

        "previewPush" => await PreviewPushAsync(
            request.ParamsAs<PushRequest>(), ct).ConfigureAwait(false),

        "previewDeleteBranch" => await PreviewDeleteBranchAsync(
            request.ParamsAs<DeleteBranchRequest>(), ct).ConfigureAwait(false),

        "fetch" => StartFetch(request.ParamsAs<FetchRequest>()),
        "pull" => StartPull(request.ParamsAs<PullRequest>()),
        "push" => StartPush(request.ParamsAs<PushRequest>()),
        "pushTag" => StartPushTag(request.ParamsAs<PushTagRequest>()),
        "cancelRemoteOperation" => Workspace.Remotes.Cancel(request.ParamsAs<CancelRemoteRequest>().Id),

        // --- detached repository clone -------------------------------------

        "startClone" => StartClone(request.ParamsAs<CloneRequest>()),
        "cloneRepository" => StartClone(request.ParamsAs<CloneRequest>()),
        "cancelClone" => Workspace.Clones.Cancel(request.ParamsAs<CancelCloneRequest>().Id),

        // --- GitHub pull requests (gh CLI) ---------------------------------

        "listPullRequests" => await ListPullRequestsAsync(
            request.ParamsAs<PullRequestListRequest>(), ct).ConfigureAwait(false),

        "getPullRequests" => await ListPullRequestsAsync(
            request.ParamsAs<PullRequestListRequest>(), ct).ConfigureAwait(false),

        "viewPullRequest" => await ViewPullRequestAsync(
            request.ParamsAs<PullRequestRequest>(), ct).ConfigureAwait(false),

        "getPullRequest" => await ViewPullRequestAsync(
            request.ParamsAs<PullRequestRequest>(), ct).ConfigureAwait(false),

        "createPullRequest" => await CreatePullRequestAsync(
            request.ParamsAs<CreatePullRequestRequest>(), ct).ConfigureAwait(false),

        "createPr" => await CreatePullRequestAsync(
            request.ParamsAs<CreatePullRequestRequest>(), ct).ConfigureAwait(false),

        "checkoutPullRequest" => await CheckoutPullRequestAsync(
            request.ParamsAs<PullRequestRequest>(), ct).ConfigureAwait(false),

        "checkoutPr" => await CheckoutPullRequestAsync(
            request.ParamsAs<PullRequestRequest>(), ct).ConfigureAwait(false),

        // --- worktrees --------------------------------------------------------

        "addWorktree" => await MutateWorktreeAsync(
            request.ParamsAs<AddWorktreeRequest>(),
            (req, token) => Workspace.Worktrees.AddAsync(
                req.WorktreePath, req.Path, req.Branch, req.CreateBranch, req.StartPoint, token),
            req => req.WorktreePath, releases: null, ct).ConfigureAwait(false),

        "removeWorktree" => await MutateWorktreeAsync(
            request.ParamsAs<WorktreeTargetRequest>(),
            (req, token) => Workspace.Worktrees.RemoveAsync(req.WorktreePath, req.Target, req.Force, token),
            req => req.WorktreePath, releases: req => req.Target, ct).ConfigureAwait(false),

        "moveWorktree" => await MutateWorktreeAsync(
            request.ParamsAs<WorktreeTargetRequest>(),
            (req, token) => Workspace.Worktrees.MoveAsync(req.WorktreePath, req.Target, req.Destination, token),
            req => req.WorktreePath, releases: req => req.Target, ct).ConfigureAwait(false),

        "lockWorktree" => await MutateWorktreeAsync(
            request.ParamsAs<WorktreeTargetRequest>(),
            (req, token) => Workspace.Worktrees.LockAsync(req.WorktreePath, req.Target, req.Reason, token),
            req => req.WorktreePath, releases: null, ct).ConfigureAwait(false),

        "unlockWorktree" => await MutateWorktreeAsync(
            request.ParamsAs<WorktreeTargetRequest>(),
            (req, token) => Workspace.Worktrees.UnlockAsync(req.WorktreePath, req.Target, token),
            req => req.WorktreePath, releases: null, ct).ConfigureAwait(false),

        "previewPrune" => await PreviewPruneAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "previewRemoveWorktree" => await PreviewRemoveWorktreeAsync(
            request.ParamsAs<WorktreeTargetRequest>(), ct).ConfigureAwait(false),

        "pruneWorktrees" => await MutateWorktreeAsync(
            request.ParamsAs<WorktreeRequest>(),
            (req, token) => Workspace.Worktrees.PruneAsync(req.WorktreePath, token),
            req => req.WorktreePath, releases: null, ct).ConfigureAwait(false),

        "suggestWorktreePath" => await SuggestWorktreePathAsync(
            request.ParamsAs<SuggestPathRequest>(), ct).ConfigureAwait(false),

        "acceptWorktree" => await AcceptWorktreeAsync(
            request.ParamsAs<AcceptWorkRequest>(), ct).ConfigureAwait(false),

        "acceptAgentWork" => await AcceptWorktreeAsync(
            request.ParamsAs<AcceptWorkRequest>(), ct).ConfigureAwait(false),

        "previewRejectWorktree" => await PreviewRejectWorktreeAsync(
            request.ParamsAs<RejectWorkPreviewRequest>(), ct).ConfigureAwait(false),

        "previewRejectAgentWork" => await PreviewRejectWorktreeAsync(
            request.ParamsAs<RejectWorkPreviewRequest>(), ct).ConfigureAwait(false),

        "rejectWorktree" => await RejectWorktreeAsync(
            request.ParamsAs<RejectWorkRequest>(), ct).ConfigureAwait(false),

        "rejectAgentWork" => await RejectWorktreeAsync(
            request.ParamsAs<RejectWorkRequest>(), ct).ConfigureAwait(false),

        // --- generated commit messages ----------------------------------------

        "getAiStatus" => Generator.Describe(),

        "setApiKey" => SetApiKey(request.ParamsAs<ApiKeyRequest>()),

        "generateCommitMessage" => StartGeneration(request.ParamsAs<GenerateMessageRequest>()),

        "cancelGeneration" => Generator.Cancel(request.ParamsAs<CancelGenerationRequest>().Id),

        "getUndo" => await GetUndoAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "undo" => await UndoAsync(request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "getOperationLog" => Workspace.Log.Recent(request.ParamsAs<OperationLogRequest>().Limit),

        "getSettings" => Settings,

        "getReviewWatermark" => await GetReviewWatermarkAsync(
            request.ParamsAs<WorktreeRequest>(), ct).ConfigureAwait(false),

        "markReviewWatermark" => await MarkReviewWatermarkAsync(
            request.ParamsAs<MarkReviewWatermarkRequest>(), ct).ConfigureAwait(false),

        "getAgentSessions" => await GetAgentSessionsAsync(
            request.ParamsAs<AgentSessionsRequest>(), ct).ConfigureAwait(false),

        "openAgentSession" => await OpenAgentSessionAsync(
            request.ParamsAs<OpenAgentSessionRequest>(), ct).ConfigureAwait(false),

        // Friendly aliases for clients that use the shorter session wording.
        "getSessions" => await GetAgentSessionsAsync(
            request.ParamsAs<AgentSessionsRequest>(), ct).ConfigureAwait(false),

        "openSession" => await OpenAgentSessionAsync(
            request.ParamsAs<OpenAgentSessionRequest>(), ct).ConfigureAwait(false),

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

        // --- self-update ------------------------------------------------------

        "getUpdateStatus" => Updater?.Status ?? Unmanaged,

        "checkForUpdate" => Updater is null
            ? Unmanaged
            : await Updater.CheckAsync(ct).ConfigureAwait(false),

        "applyUpdate" => ApplyUpdate(),

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
    /// A branch mutation, which needs one refresh more than any other kind.
    ///
    /// Every mutation invalidates the changed-file scan; a branch mutation also changes what
    /// the *rail* says, because a worktree is labelled with the branch it is on. Nothing else
    /// in the app moves that label, so without this a switch leaves every worktree row naming
    /// the branch it used to be on until something else happens to reload them.
    ///
    /// Announced on failure too. A stash-and-switch that fails part-way has still stashed,
    /// and a rename that reports an error has occasionally still moved the ref.
    /// </summary>
    private async Task<object> MutateBranchAsync<TRequest>(
        TRequest request,
        Func<TRequest, CancellationToken, Task<GitMutation>> run,
        Func<TRequest, string> worktreeOf,
        CancellationToken ct)
    {
        try
        {
            return await MutateAsync(request, run, worktreeOf, ct).ConfigureAwait(false);
        }
        finally
        {
            await AnnounceBranchChangeAsync(worktreeOf(request), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A mutation to the set of worktrees rather than to the contents of one.
    ///
    /// Two things separate these from every other mutation. The rail is a list of worktrees,
    /// so all of them change it and all of them have to announce that — the same refresh a
    /// branch switch needs, for the same reason.
    ///
    /// And two of them destroy or move the directory the app is holding open. A recursive
    /// <see cref="WorktreeWatcher"/> and a symbol index both keep handles into a worktree,
    /// and on Windows a directory that anything holds open cannot be deleted — so
    /// <paramref name="releases"/> names the worktree to let go of, and it is let go of
    /// <b>before</b> git runs rather than after. A removal that then fails leaves the app
    /// having forgotten a worktree that still exists, which costs one re-listing: the
    /// announcement below is what triggers it.
    ///
    /// Written out rather than wrapping <see cref="MutateAsync"/>, because the release above
    /// is exactly what that method's membership check would trip over: removing the worktree
    /// the panel was opened on means host and target are the same path, and forgetting it
    /// first would make the app refuse its own request. The check happens here, before
    /// anything is let go of.
    /// </summary>
    private async Task<object> MutateWorktreeAsync<TRequest>(
        TRequest request,
        Func<TRequest, CancellationToken, Task<GitMutation>> run,
        Func<TRequest, string> worktreeOf,
        Func<TRequest, string>? releases,
        CancellationToken ct)
    {
        var host = worktreeOf(request);

        if (!Workspace.IsKnownWorktree(host))
            throw new InvalidOperationException("That worktree is not open in this window.");

        // Resolved before the mutation, because afterwards it may be unresolvable: removing
        // the worktree the panel was opened against leaves `host` naming a directory that no
        // longer exists, and asking git which repository it belonged to would fail — leaving
        // the rail still showing the worktree that was just deleted.
        var repo = await ResolveRepoAsync(host, ct).ConfigureAwait(false);

        var target = releases?.Invoke(request);
        var released = !string.IsNullOrEmpty(target);
        var releasedHost = false;

        if (released)
        {
            _watcher.Unwatch(target!);
            Index.Forget(target!);
            Workspace.ForgetWorktree(target!);

            releasedHost = string.Equals(target, host, StringComparison.OrdinalIgnoreCase);
        }

        var succeeded = false;

        try
        {
            var mutation = await run(request, ct).ConfigureAwait(false);
            succeeded = mutation.Success;

            return MutationPayload.From(mutation);
        }
        finally
        {
            // Put back what was let go of, unless git actually took the worktree away.
            //
            // Not merely tidiness: membership is what `getRefs` and every mutation are gated
            // on, so a worktree released for a removal git then refuses is one the app will
            // not talk about — and the refusal this matters for is the *expected* one, where
            // the panel re-reads immediately and then offers to force. Waiting for the
            // announcement below to bring it back is a race the user's next click is in.
            // Re-listing is what admits a worktree, so this is the same call that resolved
            // the repository above.
            if (released && !succeeded) await ResolveRepoAsync(repo ?? host, ct).ConfigureAwait(false);

            // Skipped when the host is the thing that was just removed: "these files changed"
            // about a directory that no longer exists sends the front-end to re-read it.
            if (!releasedHost || !succeeded) AnnounceSelfWrite(host);

            if (repo is not null) RaiseEvent("worktreesChanged", new { repoPath = repo });
            else await AnnounceBranchChangeAsync(host, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> ResolveRepoAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var worktrees = await Workspace.GetWorktreesAsync(worktreePath, ct).ConfigureAwait(false);
            return worktrees.FirstOrDefault(w => w.IsMain)?.Path;
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// What pruning would forget. A dry run: it is offered before the button, not instead
    /// of it, so the answer has to come from git rather than from the prunable flags in the
    /// worktree list — see <see cref="WorktreeService.PreviewPruneAsync"/> for where the two
    /// disagree.
    /// </summary>
    private async Task<object> PreviewPruneAsync(WorktreeRequest req, CancellationToken ct)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        var entries = await Workspace.Worktrees.PreviewPruneAsync(req.WorktreePath, ct).ConfigureAwait(false);
        return new PrunePreviewPayload { Entries = entries };
    }

    /// <summary>
    /// What a worktree removal would delete. Read-only, and never through the writer: asking
    /// is not doing, and the operation log records what the app did.
    /// </summary>
    private async Task<object> PreviewRemoveWorktreeAsync(WorktreeTargetRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var preview = await Workspace.Worktrees
            .PreviewRemoveAsync(req.WorktreePath, req.Target, ct).ConfigureAwait(false);
        return WorktreeRemovalPreviewPayload.From(preview);
    }

    /// <summary>Which tracking refs the server no longer has. Contacts the remote.</summary>
    private async Task<object> PreviewPruneRemoteAsync(RemoteNameRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var preview = await Workspace.Remotes
            .PreviewPruneAsync(req.WorktreePath, req.Name, ct).ConfigureAwait(false);
        return RemotePrunePreviewPayload.From(preview);
    }

    /// <summary>
    /// What a push would change on the server. This is the one preview that can legitimately
    /// prompt for credentials, because it is a network operation with the transfer left out.
    /// </summary>
    private async Task<object> PreviewPushAsync(PushRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var preview = await Workspace.Remotes.PreviewPushAsync(
            req.WorktreePath, req.Remote, req.Branch, req.ForceWithLease, ct).ConfigureAwait(false);
        return PushPreviewPayload.From(preview);
    }

    /// <summary>What a branch delete would leave with nothing pointing at it.</summary>
    private async Task<object> PreviewDeleteBranchAsync(DeleteBranchRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var preview = await Workspace.Branches
            .PreviewDeleteAsync(req.WorktreePath, req.Name, ct).ConfigureAwait(false);
        return BranchDeletionPreviewPayload.From(preview);
    }

    private async Task<object> SuggestWorktreePathAsync(SuggestPathRequest req, CancellationToken ct)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        return await Workspace.Worktrees.SuggestPathAsync(req.WorktreePath, req.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the committed branch from one linked worktree into the repository's main
    /// worktree. The integration and optional directory removal are deliberately separate:
    /// the former can succeed while the latter is refused because somebody changed the source
    /// directory in the meantime.
    /// </summary>
    private async Task<object> AcceptWorktreeAsync(AcceptWorkRequest req, CancellationToken ct)
    {
        var host = FirstNonEmpty(req.WorktreePath, req.RepositoryWorktreePath, req.SourceWorktreePath);
        var source = FirstNonEmpty(req.SourceWorktreePath, req.Target);
        if (host.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("Both a repository worktree and a source worktree are required.");

        EnsureKnownWorktree(host);

        // Same reason as StartPull: an explicit null overwrites the initialiser, and
        // ParseAcceptStrategy already defends itself while this caller did not.
        var strategy = ParseAcceptStrategy(
            string.IsNullOrEmpty(req.Mode) ? req.Strategy : req.Mode);
        var remove = req.RemoveWorktree || req.RemoveAfter;

        // Removal is handled below through MutateWorktreeAsync so the watcher and symbol
        // index are released before Windows is asked to delete the source directory.
        var result = await Workspace.Acceptances.AcceptAsync(
            host, source, strategy, removeWorktree: false,
            noFastForward: req.NoFastForward,
            expectedSourceHead: req.ExpectedSourceHead,
            expectedTargetHead: req.ExpectedTargetHead,
            ct: ct)
            .ConfigureAwait(false);

        AnnounceSelfWrite(result.TargetWorktreePath);
        RaiseEvent("historyChanged", new { worktreePath = result.TargetWorktreePath });
        RaiseEvent("rebaseChanged", new { worktreePath = result.TargetWorktreePath });
        RaiseEvent("conflictChanged", new { worktreePath = result.TargetWorktreePath });

        var payload = AcceptWorkPayload.From(result) with
        {
            RemoveRequested = remove,
        };

        if (!result.Success || !remove || result.SourceWorktreePath.Length == 0)
            return payload;

        // The integration is already committed by this point, so nothing the cleanup half
        // does may be reported as a failure of the whole action. MutateWorktreeAsync throws
        // rather than returning a payload — an unknown worktree, or a git failure while
        // resolving the repository — and letting that escape told the user the merge had not
        // happened while it sat on main.
        MutationPayload removal;
        try
        {
            removal = (MutationPayload)await MutateWorktreeAsync(
                new WorktreeTargetRequest
                {
                    WorktreePath = result.TargetWorktreePath,
                    Target = result.SourceWorktreePath,
                },
                (_, token) => Workspace.Acceptances.RemoveAcceptedWorktreeAsync(result, token),
                request => request.WorktreePath,
                request => request.Target,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or GitException or IOException)
        {
            removal = new MutationPayload
            {
                Operation = "remove worktree",
                Ok = false,
                Message = ex.Message,
                Failure = GitFailure.Unknown,
                ExitCode = -1,
            };
        }

        var name = result.SourceBranch.Length > 0 ? result.SourceBranch : "the source worktree";
        var message = removal.Ok
            ? $"{result.Message}; removed {name}"
            : $"{result.Message} The worktree was not removed: {removal.Message}";

        return payload with
        {
            Removal = removal,
            Removed = removal.Ok,
            Message = message,
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static WorktreeAcceptStrategy ParseAcceptStrategy(string value)
    {
        // A typo here is materially different from the omitted value: silently turning
        // "cherry-pik" into a merge can create a commit the caller did not ask for.
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "merge" => WorktreeAcceptStrategy.Merge,
            "cherrypick" or "cherry-pick" or "cherry_pick" => WorktreeAcceptStrategy.CherryPick,
            _ => throw new InvalidOperationException(
                "Accept strategy must be merge or cherry-pick."),
        };
    }

    private async Task<object> PreviewRejectWorktreeAsync(
        RejectWorkPreviewRequest req, CancellationToken ct)
    {
        var host = FirstNonEmpty(req.WorktreePath, req.RepositoryWorktreePath, req.SourceWorktreePath);
        var source = FirstNonEmpty(req.SourceWorktreePath, req.Target);
        if (host.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("Both a repository worktree and a source worktree are required.");

        EnsureKnownWorktree(host);
        var preview = await Workspace.Rejections
            .PreviewAsync(host, source, ct)
            .ConfigureAwait(false);
        return RejectWorkPreviewPayload.From(preview);
    }

    private async Task<object> RejectWorktreeAsync(RejectWorkRequest req, CancellationToken ct)
    {
        var host = FirstNonEmpty(req.WorktreePath, req.RepositoryWorktreePath, req.SourceWorktreePath);
        var source = FirstNonEmpty(req.SourceWorktreePath, req.Target);
        if (host.Length == 0 || source.Length == 0)
            throw new InvalidOperationException("Both a repository worktree and a source worktree are required.");

        EnsureKnownWorktree(host);

        try
        {
            var result = await Workspace.Rejections.RejectAsync(
                host,
                source,
                req.ExpectedSourceHead,
                req.ExpectedBaseHead,
                req.ExpectedSnapshotFingerprint,
                ct)
                .ConfigureAwait(false);

            var sourcePath = result.SourceWorktreePath.Length > 0 ? result.SourceWorktreePath : source;
            AnnounceSelfWrite(sourcePath);
            RaiseEvent("historyChanged", new { worktreePath = sourcePath });
            RaiseEvent("rebaseChanged", new { worktreePath = sourcePath });
            RaiseEvent("conflictChanged", new { worktreePath = sourcePath });
            await AnnounceBranchChangeAsync(host, ct).ConfigureAwait(false);

            return RejectWorkPayload.From(result);
        }
        finally
        {
            // The service can refuse before it has a resolved source path. The host is still
            // the right cache to invalidate, and a failed preview must not leave the refs
            // panel displaying a result from before the attempted action.
            Workspace.InvalidateChanges(host);
        }
    }

    /// <summary>
    /// Tells the front-end to re-read the worktree list for the repository a worktree
    /// belongs to.
    ///
    /// The repository is resolved by asking git rather than by remembering, because the
    /// event carries a repo path and the caller only has a worktree path. That is one extra
    /// git process on an action that already ran several, and it is exact — where a guess
    /// from the directory layout would be wrong for the sibling-worktree arrangement the app
    /// explicitly supports.
    /// </summary>
    private async Task AnnounceBranchChangeAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var worktrees = await Workspace.GetWorktreesAsync(worktreePath, ct).ConfigureAwait(false);
            var main = worktrees.FirstOrDefault(w => w.IsMain);

            if (main is not null) RaiseEvent("worktreesChanged", new { repoPath = main.Path });
        }
        catch (GitException ex)
        {
            // The refresh is a courtesy; the mutation itself already happened and has been
            // reported. Failing here would turn a successful switch into an error.
            System.Diagnostics.Debug.WriteLine($"Could not announce a branch change: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything the ref panel shows, read together.
    ///
    /// One call rather than three: the panel renders all of it at once and every mutation
    /// refreshes the lot, so three independently-timed replies would only give the panel
    /// more ways to disagree with itself.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<AgentSession>>> FindAgentSessionsAsync(
        Task<IReadOnlyList<Worktree>> worktrees, CancellationToken ct)
    {
        var known = await worktrees.ConfigureAwait(false);
        return await AgentSessions.FindForWorktreesAsync(
            known.Select(worktree => new AgentSessionWorktree(worktree.Path, worktree.Branch)).ToArray(), ct)
            .ConfigureAwait(false);
    }

    private async Task<object> GetRefsAsync(WorktreeRequest req, CancellationToken ct)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        var branchTask = Workspace.Branches.ListAsync(req.WorktreePath, ct);
        var stashTask = Workspace.Stashes.ListAsync(req.WorktreePath, ct);
        var tagTask = Workspace.Tags.ListAsync(req.WorktreePath, ct);
        var remoteTask = Workspace.Remotes.ListAsync(req.WorktreePath, ct);
        var stateTask = Workspace.GetRepositoryStateAsync(req.WorktreePath, ct);

        // Through the workspace rather than the lister, because that is also what admits a
        // worktree to the set the app may write to: a worktree added through this panel is
        // then usable immediately, rather than only after the rail happens to re-read.
        var worktreeTask = Workspace.GetWorktreesAsync(req.WorktreePath, ct);

        // The session scan needs the worktree list, so it cannot start with the others — but
        // it can still run beside them. Awaiting it after the WhenAll added a recursive walk
        // of three session stores to every refs refresh instead of overlapping it with the
        // git reads, which are the slow half and which it does not depend on.
        var sessionTask = FindAgentSessionsAsync(worktreeTask, ct);

        await Task.WhenAll(
            branchTask, stashTask, tagTask, remoteTask, stateTask, worktreeTask, sessionTask)
            .ConfigureAwait(false);

        var state = await stateTask.ConfigureAwait(false);
        var branches = await branchTask.ConfigureAwait(false);
        var worktrees = await worktreeTask.ConfigureAwait(false);
        var sessions = await sessionTask.ConfigureAwait(false);

        // The same question the writer's guard will ask, asked early so the buttons can say
        // why they are disabled instead of each failing when pressed.
        var guard = state.CanWrite(WriteKind.StartsOperation);

        return new RefsPayload
        {
            WorktreePath = req.WorktreePath,
            Branches = branches,
            Stashes = await stashTask.ConfigureAwait(false),
            Tags = await tagTask.ConfigureAwait(false),
            Remotes = await remoteTask.ConfigureAwait(false),
            Worktrees = worktrees,
            AgentSessions = sessions.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AgentSessionPayload>)pair.Value
                    .Select(AgentSessionPayload.From).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            Current = branches.FirstOrDefault(b => b.IsCurrent)?.Name,
            CanSwitch = guard.Allowed,
            BlockedReason = guard.Reason,
        };
    }

    private async Task<object> GetReviewWatermarkAsync(WorktreeRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        return ReviewWatermarkPayload.From(await ReviewWatermarks.GetAsync(req.WorktreePath, ct)
            .ConfigureAwait(false));
    }

    private async Task<object> GetAgentSessionsAsync(
        AgentSessionsRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var worktree = await FindWorktreeAsync(req.WorktreePath, ct).ConfigureAwait(false);
        var sessions = await AgentSessions
            .FindAsync(req.WorktreePath, worktree.Branch, ct)
            .ConfigureAwait(false);

        return new AgentSessionsPayload
        {
            WorktreePath = req.WorktreePath,
            Sessions = sessions.Select(AgentSessionPayload.From).ToArray(),
        };
    }

    private async Task<object> OpenAgentSessionAsync(
        OpenAgentSessionRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var worktree = await FindWorktreeAsync(req.WorktreePath, ct).ConfigureAwait(false);
        var sessions = await AgentSessions
            .FindAsync(req.WorktreePath, worktree.Branch, ct)
            .ConfigureAwait(false);

        // Accept both field spellings so older front-end builds and callers using the
        // protocol's descriptive name can share the same bridge.
        var id = FirstNonEmpty(req.SessionId, req.Id);
        var provider = ParseSessionProvider(req.Provider);
        var session = sessions.FirstOrDefault(candidate =>
            candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            && (req.Provider.Length == 0 || candidate.Provider == provider));

        if (session is null)
            throw new InvalidOperationException("That agent session is no longer available.");

        if (!AgentSessions.IsSafeLogPath(session.LogPath))
            throw new InvalidOperationException("That session log is no longer a safe local file.");

        var opened = OpenExternalPath?.Invoke(session.LogPath) ?? false;
        return new OpenAgentSessionPayload
        {
            Success = opened,
            WorktreePath = req.WorktreePath,
            SessionId = session.Id,
            Provider = session.Provider.ToString().ToLowerInvariant(),
            LogPath = session.LogPath,
            Detail = opened ? "Session log opened." : "No external file handler is available.",
        };
    }

    private static AgentSessionProvider ParseSessionProvider(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "" => AgentSessionProvider.Claude,
            "claude" or "claude-code" => AgentSessionProvider.Claude,
            "book" or "book-agent" => AgentSessionProvider.Book,
            "codex" or "codex-cli" => AgentSessionProvider.Codex,
            _ => throw new InvalidOperationException($"Unknown agent session provider '{value}'."),
        };

    private async Task<object> MarkReviewWatermarkAsync(MarkReviewWatermarkRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        return ReviewWatermarkPayload.From(await ReviewWatermarks
            .MarkAsync(req.WorktreePath, req.ExpectedFingerprint, ct)
            .ConfigureAwait(false));
    }

    private async Task<object> GetRemotesAsync(WorktreeRequest req, CancellationToken ct)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        return await Workspace.Remotes.ListAsync(req.WorktreePath, ct).ConfigureAwait(false);
    }

    private async Task<object> ListPullRequestsAsync(
        PullRequestListRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        try
        {
            var values = await Workspace.PullRequests
                .ListAsync(req.WorktreePath, req.Limit, ct).ConfigureAwait(false);
            return new PullRequestListPayload
            {
                WorktreePath = req.WorktreePath,
                PullRequests = values.Select(PullRequestPayload.From).ToArray(),
            };
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
            return new PullRequestListPayload
            {
                WorktreePath = req.WorktreePath,
                PullRequests = [],
                Success = false,
                Detail = ex.Message,
            };
        }
    }

    private async Task<object> ViewPullRequestAsync(
        PullRequestRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var result = await Workspace.PullRequests
            .ViewAsync(req.WorktreePath, req.Selector, ct).ConfigureAwait(false);
        return PullRequestResultPayload.From(result);
    }

    private async Task<object> CreatePullRequestAsync(
        CreatePullRequestRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var result = await Workspace.PullRequests.CreateAsync(
            req.WorktreePath, req.Title, req.Body, req.BaseBranch, req.HeadBranch, req.Draft, ct)
            .ConfigureAwait(false);
        return PullRequestResultPayload.From(result);
    }

    private async Task<object> CheckoutPullRequestAsync(
        PullRequestRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var mutation = await Workspace.PullRequests
            .CheckoutAsync(req.WorktreePath, req.Selector, ct).ConfigureAwait(false);
        return MutationPayload.From(mutation);
    }

    private object StartFetch(FetchRequest req)
    {
        EnsureKnownWorktree(req.WorktreePath);
        return new RemoteOperationStartedPayload
        {
            // Reserve happens in the service before the detached task is scheduled, so a
            // second click gets a useful bridge error rather than two competing fetches.
            Id = Workspace.Remotes.StartFetch(req.WorktreePath, req.Remote, req.Prune, req.All).Id,
            WorktreePath = req.WorktreePath,
            Operation = "fetch",
        };
    }

    private object StartPull(PullRequest req)
    {
        EnsureKnownWorktree(req.WorktreePath);
        // `?? ""` rather than trusting the record's initialiser: System.Text.Json writes an
        // explicit JSON null straight over a property default, so a client that sends
        // "strategy": null gets a NullReferenceException reported as an opaque bridge error.
        var strategy = (req.Strategy ?? "").ToLowerInvariant() switch
        {
            "rebase" => PullStrategy.Rebase,
            "ff-only" or "fastforwardonly" or "fast-forward-only" => PullStrategy.FastForwardOnly,
            _ => PullStrategy.Merge,
        };
        var started = Workspace.Remotes.StartPull(req.WorktreePath, strategy, req.Remote, req.Branch);
        return new RemoteOperationStartedPayload
        {
            Id = started.Id,
            WorktreePath = started.WorktreePath,
            Operation = started.Operation,
        };
    }

    private object StartPush(PushRequest req)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var operation = Workspace.Remotes.StartPush(
            req.WorktreePath, req.Remote, req.Branch, req.ForceWithLease, req.SetUpstream);
        return new RemoteOperationStartedPayload
        {
            Id = operation.Id,
            WorktreePath = operation.WorktreePath,
            Operation = operation.Operation,
        };
    }

    private object StartPushTag(PushTagRequest req)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var started = Workspace.Remotes.StartPushTag(req.WorktreePath, req.Remote, req.Tag);
        return new RemoteOperationStartedPayload
        {
            Id = started.Id,
            WorktreePath = started.WorktreePath,
            Operation = started.Operation,
        };
    }

    private void EnsureKnownWorktree(string path)
    {
        if (!Workspace.IsKnownWorktree(path))
            throw new InvalidOperationException("That worktree is not open in this window.");
    }

    private void OnRemoteFinished(RemoteProgress progress)
    {
        // Guarded like the clone counterpart: Dispose cancels in-flight operations, and their
        // terminal callback can land after the window has gone.
        if (Volatile.Read(ref _disposed) != 0) return;

        // The operation runs outside the bridge request, so it needs the same invalidation and
        // refresh announcements that MutateAsync normally supplies synchronously.
        Workspace.InvalidateChanges(progress.WorktreePath);
        Workspace.InvalidateState(progress.WorktreePath);

        RaiseEvent("remoteFinished", ToRemoteProgress(progress));
        // Fetch can move remote decorations without touching the working tree; pull and
        // push can move the current tip. Both make an open history list stale.
        RaiseEvent("historyChanged", new { worktreePath = progress.WorktreePath });
        RaiseEvent("filesChanged", new { worktreePath = progress.WorktreePath, selfOriginated = true });
        RaiseEvent("rebaseChanged", new { worktreePath = progress.WorktreePath });
        RaiseEvent("conflictChanged", new { worktreePath = progress.WorktreePath });
        _ = AnnounceRemoteBranchChangeAsync(progress.WorktreePath);
    }

    private void OnRemoteProgress(RemoteProgress progress)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        RaiseEvent("remoteProgress", ToRemoteProgress(progress));
    }

    private async Task AnnounceRemoteBranchChangeAsync(string worktreePath)
    {
        try
        {
            var worktrees = await Workspace.GetWorktreesAsync(worktreePath).ConfigureAwait(false);
            var main = worktrees.FirstOrDefault(w => w.IsMain);
            if (main is not null) RaiseEvent("worktreesChanged", new { repoPath = main.Path });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not announce remote change: {ex.Message}");
        }
    }

    private static RemoteProgressPayload ToRemoteProgress(RemoteProgress progress) => new()
    {
        Id = progress.Id,
        WorktreePath = progress.WorktreePath,
        Operation = progress.Operation,
        State = progress.State,
        Phase = progress.Phase,
        Message = progress.Message,
        Percent = progress.Percent,
        Result = progress.Mutation is null ? null : MutationPayload.From(progress.Mutation),
    };

    private object StartClone(CloneRequest req)
    {
        var started = Workspace.Clones.Start(
            req.Source, req.Destination, req.Bare, req.Recursive);

        return new CloneOperationStartedPayload
        {
            Id = started.Id,
            Source = started.Source,
            Destination = started.Destination,
        };
    }

    private void OnCloneProgress(CloneProgress progress) =>
        RaiseEvent("cloneProgress", ToCloneProgress(progress));

    private void OnCloneFinished(CloneProgress progress) =>
        _ = HandleCloneFinishedAsync(progress);

    private async Task HandleCloneFinishedAsync(CloneProgress progress)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var repositoryPath = progress.Destination;
        string? registered = null;

        if (progress.State == "completed" && Directory.Exists(progress.Destination))
        {
            try
            {
                registered = await Workspace.AddRepoAsync(progress.Destination)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not register cloned repository: {ex.Message}");
            }
        }

        if (Volatile.Read(ref _disposed) != 0) return;

        RaiseEvent("cloneFinished", ToCloneProgress(progress, registered));
        if (registered is not null)
        {
            try
            {
                Settings.RecordRepo(registered);
                Settings.Save();
            }
            catch (Exception ex)
            {
                // Repository registration is already in memory; a settings-file failure must
                // not turn a completed clone into an unobserved async-void exception.
                System.Diagnostics.Debug.WriteLine($"Could not persist cloned repository: {ex.Message}");
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                RaiseEvent("reposChanged", new { repoPath = registered });
                RaiseEvent("worktreesChanged", new { repoPath = registered });
            }
        }
    }

    private static CloneProgressPayload ToCloneProgress(
        CloneProgress progress, string? repositoryPath = null) => new()
    {
        Id = progress.Id,
        Source = progress.Source,
        Destination = progress.Destination,
        State = progress.State,
        Phase = progress.Phase,
        Message = progress.Message,
        Percent = progress.Percent,
        Result = progress.Mutation is null ? null : MutationPayload.From(progress.Mutation),
        RepositoryPath = repositoryPath,
    };

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
    /// <summary>
    /// The answer when there is no updater behind the seam at all. Distinct from "no update
    /// available": the front-end draws nothing rather than claiming this build is current.
    /// </summary>
    private static readonly UpdateStatus Unmanaged = new() { State = UpdateState.Unmanaged };

    /// <summary>
    /// Restarts into the staged build, or explains why it cannot.
    ///
    /// The success path never returns — the process is gone before the reply is serialised —
    /// so the value here is the refusal, and the refusal is the current status. The button
    /// only exists while that status is <see cref="UpdateState.Ready"/>, and the check is
    /// repeated anyway because the click and the state it was drawn from are separated by a
    /// user, who may have taken a minute over it.
    /// </summary>
    private UpdateStatus ApplyUpdate()
    {
        var status = Updater?.Status ?? Unmanaged;
        if (Updater is null || status.State is not UpdateState.Ready) return status;

        Updater.ApplyAndRestart();
        return status;
    }

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

        await WatchAsync(worktree, ct).ConfigureAwait(false);

        return await Workspace.GetChangesAsync(worktree, req.Scope, ct).ConfigureAwait(false);
    }

    private async Task<object> GetWorktreeComparisonAsync(
        WorktreeComparisonRequest req, CancellationToken ct)
    {
        var (left, right) = await FindComparisonPairAsync(
            req.LeftWorktreePath, req.RightWorktreePath, ct).ConfigureAwait(false);

        await Task.WhenAll(WatchAsync(left, ct), WatchAsync(right, ct)).ConfigureAwait(false);
        var comparison = await Workspace.Comparisons.CompareAsync(left, right, ct).ConfigureAwait(false);
        return WorktreeComparisonPayload.From(comparison);
    }

    private async Task<object> GetWorktreeComparisonFileAsync(
        WorktreeComparisonFileRequest req, CancellationToken ct)
    {
        var (left, right) = await FindComparisonPairAsync(
            req.LeftWorktreePath, req.RightWorktreePath, ct).ConfigureAwait(false);
        var content = await Workspace.Comparisons.GetFileAsync(
            left, right, req.LeftPath, req.RightPath, ct).ConfigureAwait(false);
        return WorktreeComparisonContentPayload.From(content);
    }

    private async Task<object> GetHistoryAsync(HistoryRequest req, CancellationToken ct)
    {
        if (!Workspace.IsKnownWorktree(req.WorktreePath))
            throw new InvalidOperationException("That worktree is not open in this window.");

        var page = await Workspace.History
            .ListAsync(req.WorktreePath, req.Offset, req.Limit, req.Anchor, ct)
            .ConfigureAwait(false);

        return CommitLogPagePayload.From(req.WorktreePath, page);
    }

    private async Task<object> SearchHistoryAsync(HistorySearchRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var page = await Workspace.History
            .SearchAsync(req.WorktreePath, req.Kind, req.Query, req.Offset, req.Limit, req.Anchor, ct)
            .ConfigureAwait(false);

        return CommitLogPagePayload.From(req.WorktreePath, page);
    }

    private async Task<object> GetCommitDetailAsync(CommitDetailRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var detail = await Workspace.History
            .GetDetailAsync(req.WorktreePath, req.Sha, req.ParentIndex, ct)
            .ConfigureAwait(false);
        return CommitDetailPayload.From(req.WorktreePath, detail);
    }

    private async Task<object> GetCommitFileDiffAsync(CommitFileDiffRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var diff = await Workspace.History
            .GetFileDiffAsync(req.WorktreePath, req.Sha, req.Path, req.ParentIndex, ct)
            .ConfigureAwait(false);
        return CommitFileDiffPayload.From(req.WorktreePath, diff);
    }

    private async Task<object> GetFileHistoryAsync(FileHistoryRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var path = HistoryService.NormalizeRelativePath(req.Path);
        var page = await Workspace.History
            .ListFileAsync(req.WorktreePath, path, req.Offset, req.Limit, req.Anchor, ct)
            .ConfigureAwait(false);
        return FileHistoryPagePayload.From(req.WorktreePath, path, page);
    }

    private async Task<object> GetBlameAsync(BlameRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var path = HistoryService.NormalizeRelativePath(req.Path);
        var result = await Workspace.History
            .BlameAsync(req.WorktreePath, path, req.Revision, ct)
            .ConfigureAwait(false);
        return BlamePayload.From(req.WorktreePath, result);
    }

    private async Task<object> GetRebasePlanAsync(RebasePlanRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var plan = await Workspace.Rebases
            .GetPlanAsync(req.WorktreePath, req.Upstream, ct)
            .ConfigureAwait(false);
        return RebasePlanPayload.From(plan);
    }

    private async Task<object> GetRebaseStateAsync(RebaseStateRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var state = await Workspace.Rebases.GetStateAsync(req.WorktreePath, ct).ConfigureAwait(false);
        return RebaseStatePayload.From(state);
    }

    private async Task<object> GetConflictStateAsync(ConflictStateRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var state = await Workspace.Conflicts.GetStateAsync(req.WorktreePath, ct).ConfigureAwait(false);
        return ConflictStatePayload.From(state);
    }

    private async Task<object> GetConflictFileAsync(ConflictFileRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        var file = await Workspace.Conflicts.GetFileAsync(req.WorktreePath, req.Path, ct)
            .ConfigureAwait(false);
        return file is null ? null! : ConflictFilePayload.From(file);
    }

    private async Task<object> GetRerereStatusAsync(ConflictStateRequest req, CancellationToken ct)
    {
        EnsureKnownWorktree(req.WorktreePath);
        return await Workspace.Conflicts.RerereStatusAsync(req.WorktreePath, ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveGitDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await Workspace.Git
            .TryRunAsync(worktreePath, ct, "rev-parse", "--absolute-git-dir")
            .ConfigureAwait(false);

        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    private async Task WatchAsync(Worktree worktree, CancellationToken ct)
    {
        if (!worktree.IsUsable) return;

        // The git directory has to be watched alongside the working tree, or commits —
        // which touch no working-tree file, and for a linked worktree live outside it
        // entirely — go unnoticed.
        var gitDir = await ResolveGitDirAsync(worktree.Path, ct).ConfigureAwait(false);
        _watcher.Watch(worktree.Path, gitDir);
    }

    private async Task<(Worktree Left, Worktree Right)> FindComparisonPairAsync(
        string leftPath, string rightPath, CancellationToken ct)
    {
        EnsureKnownWorktree(leftPath);
        EnsureKnownWorktree(rightPath);

        if (string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose two different worktrees to compare.");

        var worktrees = await Workspace.GetWorktreesAsync(leftPath, ct).ConfigureAwait(false);
        var left = worktrees.FirstOrDefault(worktree =>
            string.Equals(worktree.Path, leftPath, StringComparison.OrdinalIgnoreCase));
        var right = worktrees.FirstOrDefault(worktree =>
            string.Equals(worktree.Path, rightPath, StringComparison.OrdinalIgnoreCase));

        if (left is null || right is null)
            throw new InvalidOperationException("The two worktrees must belong to the same repository.");

        if (!left.IsUsable || !right.IsUsable)
            throw new InvalidOperationException("Both worktrees must have usable working directories.");

        return (left, right);
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
