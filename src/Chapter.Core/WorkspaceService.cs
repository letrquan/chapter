using System.Collections.Concurrent;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core;

/// <summary>
/// Orchestrates the git layer for the UI: which repositories are open, what worktrees
/// they have, and what changed in each. Keeps every git decision in Core so the WPF host
/// stays a thin shell around the WebView.
/// </summary>
public sealed class WorkspaceService
{
    private readonly WorktreeService _worktrees;
    private readonly BaseBranchResolver _bases;
    private readonly DiffService _diff;
    private readonly RepositoryStateReader _state;

    /// <summary>Repository roots the user has opened, in the order they were added.</summary>
    private readonly List<string> _repos = [];

    /// <summary>
    /// The most recent changed-file scan per worktree and scope.
    ///
    /// Opening a file needs one <see cref="ChangedFile"/> record to know which side to read
    /// from. Recomputing the whole scan for it costs five git processes over the entire
    /// tree plus a full read of every untracked file — on every click, tab switch and mode
    /// toggle. Invalidated whenever the watcher reports a change, so it cannot go stale
    /// behind an agent's edits.
    /// </summary>
    private readonly ConcurrentDictionary<(string Worktree, DiffScope Scope), WorktreeChanges> _changeCache =
        new();

    /// <summary>
    /// Worktrees the app has listed, and therefore the only ones it will write to.
    /// Keyed case-insensitively because Windows paths arrive from git, the settings file
    /// and the front-end with no agreement on casing.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _knownWorktrees = new(StringComparer.OrdinalIgnoreCase);

    public GitCli Git { get; }

    /// <summary>Every mutation the app has performed, for when something is unexplained.</summary>
    public OperationLog Log { get; }

    /// <summary>The only route by which this app changes a repository.</summary>
    public GitWriter Writer { get; }

    public UndoService Undo { get; }

    /// <summary>Moves changes between the working tree, the index and nowhere.</summary>
    public StagingService Staging { get; }

    public CommitService Commits { get; }

    /// <summary>
    /// The last repository-state reading per worktree, with the moment it was taken.
    ///
    /// The guard in this class's constructor runs before every mutation and costs four git
    /// processes. That was fine when a mutation was a rare event; per-hunk staging makes it
    /// several a second, and the state it reads — mid-merge, mid-rebase, conflicted — only
    /// changes when the git directory does. So it is cached and dropped on the watcher's
    /// git-state signal, which is what <see cref="InvalidateState"/> is wired to.
    /// </summary>
    private readonly ConcurrentDictionary<string, RepositoryState> _stateCache =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Repos
    {
        get { lock (_repos) return _repos.ToArray(); }
    }

    /// <param name="log">
    /// Where mutations are recorded. Optional so tests and read-only callers get an
    /// in-memory log rather than writing to the user's profile; the app passes a
    /// persistent one.
    /// </param>
    public WorkspaceService(GitCli git, OperationLog? log = null)
    {
        Git = git;
        Log = log ?? new OperationLog();

        _worktrees = new WorktreeService(git);
        _bases = new BaseBranchResolver(git);
        _diff = new DiffService(git);
        _state = new RepositoryStateReader(git);

        Writer = new GitWriter(git, Log)
        {
            // Every mutation is checked against the repository's state first, so the app
            // says "you are mid-rebase" instead of letting git refuse afterwards.
            //
            // Reads through the cache, not the reader: hunk staging turns this from one
            // check per commit into one per click, and four git processes behind each would
            // be felt. The cache is dropped whenever the git directory changes, so the check
            // is never answered from a state the repository has since left.
            Guard = async (worktree, kind, ct) =>
                (await GetRepositoryStateAsync(worktree, ct).ConfigureAwait(false)).CanWrite(kind),

            // The other half of caching the guard's answer: every mutation through the one
            // sanctioned write path drops it again. Without this the cache is not an
            // optimisation but a bug — `merge --abort` would succeed and the next guard
            // would still believe the merge is running.
            Mutated = InvalidateState,
        };

        Undo = new UndoService(git, Writer);
        Staging = new StagingService(git, Writer);
        Commits = new CommitService(git, Writer, Undo);
    }

    /// <summary>
    /// Registers a repository. Accepts any path inside it — a worktree, a subdirectory —
    /// and normalises to the main worktree so opening a linked worktree and opening the
    /// repo itself converge on the same entry.
    /// </summary>
    public async Task<string?> AddRepoAsync(string anyPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(anyPath)) return null;

        // Through GetWorktreesAsync rather than the lister directly, so opening a repo is
        // also what admits its worktrees to the set the app may write to.
        var worktrees = await GetWorktreesAsync(anyPath, ct).ConfigureAwait(false);
        var main = worktrees.FirstOrDefault(w => w.IsMain);
        if (main is null) return null;

        lock (_repos)
        {
            if (!_repos.Contains(main.Path, StringComparer.OrdinalIgnoreCase))
                _repos.Add(main.Path);
        }

        return main.Path;
    }

    public void RemoveRepo(string repoPath)
    {
        lock (_repos) _repos.RemoveAll(r => string.Equals(r, repoPath, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Worktree>> GetWorktreesAsync(string repoPath, CancellationToken ct = default)
    {
        var worktrees = await _worktrees.ListAsync(repoPath, ct).ConfigureAwait(false);

        // Listing is what admits a worktree to the set the app will write to.
        foreach (var worktree in worktrees) _knownWorktrees[worktree.Path] = 0;

        return worktrees;
    }

    /// <summary>Worktree plus its changed-file set for the requested scope.</summary>
    public async Task<WorktreeChanges> GetChangesAsync(
        Worktree worktree, DiffScope scope = DiffScope.Branch, CancellationToken ct = default)
    {
        // A stale or bare worktree has no working directory to inspect. Return an empty
        // set rather than letting git fail on a missing path.
        if (!worktree.IsUsable)
        {
            return new WorktreeChanges
            {
                Worktree = worktree,
                Base = new DiffBase { Sha = worktree.Head, Description = worktree.IsPrunable ? "unavailable" : "bare" },
                Files = [],
            };
        }

        var diffBase = await _bases.ResolveBaseAsync(worktree.Path, scope, ct).ConfigureAwait(false);
        var files = await _diff.GetChangedFilesAsync(worktree.Path, diffBase, ct).ConfigureAwait(false);

        var changes = new WorktreeChanges { Worktree = worktree, Base = diffBase, Files = files };

        // Always recomputed here — this is the call the UI makes to refresh — and cached so
        // the per-file lookups that follow do not each repeat it.
        _changeCache[(worktree.Path, scope)] = changes;
        return changes;
    }

    /// <summary>Drops cached scans for a worktree, in every scope.</summary>
    public void InvalidateChanges(string worktreePath)
    {
        foreach (var key in _changeCache.Keys)
        {
            if (string.Equals(key.Worktree, worktreePath, StringComparison.OrdinalIgnoreCase))
                _changeCache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Builds both sides of a file's diff around the index.
    ///
    /// The index is a revision like any other to <c>git show</c> — <c>:path</c>, stage zero —
    /// so both halves reduce to the same content read the app already does. What differs is
    /// only which two revisions bracket the comparison.
    /// </summary>
    private async Task<DiffPayload> GetIndexSideDiffAsync(
        string worktreePath, string repoRelativePath, DiffSide side, CancellationToken ct)
    {
        var state = await GetRepositoryStateAsync(worktreePath, ct).ConfigureAwait(false);

        FileContent baseContent;
        FileContent workingContent;

        if (side is DiffSide.Staged)
        {
            // Before the first commit there is no HEAD to compare the index against, and
            // every staged file is an addition against nothing.
            baseContent = state.IsUnborn
                ? FileContent.Empty
                : await _diff.GetContentAtAsync(worktreePath, "HEAD", repoRelativePath, ct).ConfigureAwait(false);

            workingContent = await _diff
                .GetContentAtAsync(worktreePath, "", repoRelativePath, ct).ConfigureAwait(false);
        }
        else
        {
            baseContent = await _diff
                .GetContentAtAsync(worktreePath, "", repoRelativePath, ct).ConfigureAwait(false);

            workingContent = await DiffService
                .GetWorkingContentAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false);
        }

        return new DiffPayload
        {
            Path = repoRelativePath,
            BaseText = baseContent.Text,
            WorkingText = workingContent.Text,
            Language = LanguageMap.ForPath(repoRelativePath),
            IsBinary = baseContent.IsBinary || workingContent.IsBinary,
            Kind = side is DiffSide.Staged ? "staged" : "unstaged",
        };
    }

    /// <summary>Builds both sides of a file's diff, ready for Monaco's diff editor.</summary>
    public async Task<DiffPayload> GetDiffAsync(
        string worktreePath, string repoRelativePath, DiffScope scope = DiffScope.Branch,
        DiffSide side = DiffSide.Combined, CancellationToken ct = default)
    {
        if (side is not DiffSide.Combined)
        {
            return await GetIndexSideDiffAsync(worktreePath, repoRelativePath, side, ct)
                .ConfigureAwait(false);
        }

        // Reuse the scan the file list was built from. The client is looking at a list that
        // came from exactly this data, so recomputing it per click buys nothing.
        if (!_changeCache.TryGetValue((worktreePath, scope), out var changes))
        {
            var freshBase = await _bases.ResolveBaseAsync(worktreePath, scope, ct).ConfigureAwait(false);
            var freshFiles = await _diff.GetChangedFilesAsync(worktreePath, freshBase, ct).ConfigureAwait(false);
            changes = new WorktreeChanges
            {
                Worktree = new Worktree { Path = worktreePath },
                Base = freshBase,
                Files = freshFiles,
            };
            _changeCache[(worktreePath, scope)] = changes;
        }

        var diffBase = changes.Base;
        var file = changes.Files.FirstOrDefault(f => f.Path == repoRelativePath);

        // Requesting a file that is not in the changed set is legitimate — the user can
        // open any file from the tree. Treat it as unchanged: both sides identical.
        var kind = file?.Kind ?? ChangeKind.Modified;
        var basePath = file?.BasePath ?? repoRelativePath;
        var hasBase = file?.HasBaseSide ?? true;
        var hasWorking = file?.HasWorkingSide ?? true;

        var baseContent = hasBase
            ? await _diff.GetContentAtAsync(worktreePath, diffBase.Sha, basePath, ct).ConfigureAwait(false)
            : FileContent.Empty;

        // The right-hand side has to match where the comparison ends, or a "committed
        // only" view would show uncommitted edits it is meant to be excluding.
        var workingContent = hasWorking
            ? diffBase.ToRef is null
                ? await DiffService.GetWorkingContentAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false)
                : await _diff.GetContentAtAsync(worktreePath, diffBase.ToRef, repoRelativePath, ct).ConfigureAwait(false)
            : FileContent.Empty;

        return new DiffPayload
        {
            Path = repoRelativePath,
            OldPath = file?.OldPath,
            BaseText = baseContent.Text,
            WorkingText = workingContent.Text,
            Language = LanguageMap.ForPath(repoRelativePath),
            IsBinary = baseContent.IsBinary || workingContent.IsBinary,
            Kind = kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// File content for the non-diff code view, read from wherever the scope's comparison
    /// ends.
    ///
    /// Always reading the working tree would make Ctrl+D out of a Committed or Last view
    /// show precisely the uncommitted edits that view exists to exclude — and the caret
    /// carried across the switch would land on the wrong line, since the two texts differ.
    /// </summary>
    public async Task<FileContentPayload> GetFileContentAsync(
        string worktreePath, string repoRelativePath, DiffScope scope = DiffScope.Branch, CancellationToken ct = default)
    {
        // Branch and Uncommitted both end at the working tree, so the file can be read
        // straight from disk. Resolving a base first would cost several git processes per
        // call, and this runs once per file when peek previews are materialised.
        var content = scope is DiffScope.Branch or DiffScope.Uncommitted
            ? await DiffService.GetWorkingContentAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false)
            : await ContentAtScopeEndAsync(worktreePath, repoRelativePath, scope, ct).ConfigureAwait(false);

        return new FileContentPayload
        {
            Path = repoRelativePath,
            Text = content.Text,
            Language = LanguageMap.ForPath(repoRelativePath),
            IsBinary = content.IsBinary,
            Encoding = content.Format.Encoding,
            LineEnding = content.Format.LineEnding,
            // Only the working tree can be written back. A file read at a commit is a
            // historical object, and the editor must not offer to save over it.
            //
            // CanRoundTrip is the other half: a file that did not decode cleanly, or whose
            // newlines disagree with each other, cannot be written back as it was found,
            // and offering to edit it is offering to corrupt it.
            IsEditable = scope is DiffScope.Branch or DiffScope.Uncommitted
                         && !content.IsBinary
                         && content.CanRoundTrip,
        };
    }

    /// <summary>
    /// What state the worktree's repository is in — mid-merge, mid-rebase, conflicted.
    ///
    /// Served from the cache when there is one. A reading that failed its probes is never
    /// cached: <see cref="RepositoryState.ProbeFailed"/> makes the guard refuse, and
    /// remembering a transient failure would keep refusing long after the cause was gone.
    /// </summary>
    public async Task<RepositoryState> GetRepositoryStateAsync(
        string worktreePath, CancellationToken ct = default)
    {
        if (_stateCache.TryGetValue(worktreePath, out var cached)) return cached;

        var state = await _state.ReadAsync(worktreePath, ct).ConfigureAwait(false);
        if (!state.ProbeFailed) _stateCache[worktreePath] = state;

        return state;
    }

    /// <summary>
    /// Drops the cached repository state for a worktree. Called when the git directory
    /// changes and after every mutation the app makes, since a commit or a resolved merge
    /// is exactly the kind of thing that invalidates it.
    /// </summary>
    public void InvalidateState(string worktreePath) => _stateCache.TryRemove(worktreePath, out _);

    /// <summary>The uncommitted work split into what a commit would take and what it would leave.</summary>
    public async Task<CommitView> GetCommitViewAsync(string worktreePath, CancellationToken ct = default)
    {
        var repository = await GetRepositoryStateAsync(worktreePath, ct).ConfigureAwait(false);
        return await Staging.ReadAsync(worktreePath, repository, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages, unstages or discards part of a file.
    ///
    /// The diff is re-read here rather than taken from the front-end. What the user selected
    /// is a set of positions in a diff the backend produced moments ago; sending the patch
    /// itself back would let the window write arbitrary content into the index, and would
    /// mean a change made in between is silently overwritten by a stale one. Re-reading also
    /// means the patch matches the file as it is now, which is the only version git will
    /// accept.
    /// </summary>
    public async Task<GitMutation> ApplyPatchAsync(PatchRequest request, CancellationToken ct = default)
    {
        if (!IsKnownWorktree(request.WorktreePath))
            return PatchRefused(request, "that worktree is not open in this window");

        var patch = await PatchBuilder
            .ReadAsync(Git, request.WorktreePath, request.Path, request.Side, ct)
            .ConfigureAwait(false);

        if (patch.IsBinary)
            return PatchRefused(request, "a binary file cannot be staged by hunk");

        if (patch.Hunks.Count == 0)
            return PatchRefused(request, "there is nothing left to apply — it may have changed already");

        // The race this app is built around: an agent can rewrite the file between the diff
        // being rendered and the user clicking a hunk in it. Hunk 2 of the re-read diff is
        // then a different change than the one they approved, and applying it silently is
        // the worst available outcome.
        if (request.Fingerprint.Length > 0
            && !string.Equals(request.Fingerprint, patch.Fingerprint, StringComparison.Ordinal))
        {
            return PatchRefused(request,
                "this file changed since those hunks were shown — look again before staging");
        }

        var lines = new Dictionary<int, HashSet<int>>();
        foreach (var selection in request.Lines)
        {
            if (!lines.TryGetValue(selection.Hunk, out var set))
                lines[selection.Hunk] = set = [];

            set.Add(selection.Line);
        }

        var text = PatchBuilder.Build(
            patch,
            new PatchBuilder.Selection { Hunks = request.Hunks, Lines = lines },
            request.Reverse);

        if (text is null)
            return PatchRefused(request, "nothing was selected");

        return await PatchBuilder
            .ApplyAsync(
                Writer, request.WorktreePath, text,
                DescribePatch(request), request.Reverse, request.ApplyToWorkingTree, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Names the operation the way the user asked for it, for the log and the toast.</summary>
    private static string DescribePatch(PatchRequest request)
    {
        var verb = request.ApplyToWorkingTree
            ? "discard"
            : request.Reverse ? "unstage" : "stage";

        var scale = request.Lines.Count > 0
            ? request.Lines.Count == 1 ? "1 line of" : $"{request.Lines.Count} lines of"
            : request.Hunks.Count == 1 ? "1 hunk of" : $"{request.Hunks.Count} hunks of";

        return $"{verb} {scale} {request.Path}";
    }

    private static GitMutation PatchRefused(PatchRequest request, string reason) => new()
    {
        Operation = DescribePatch(request),
        WorktreePath = request.WorktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.NothingToDo,
        Detail = $"Could not {DescribePatch(request)}: {reason}",
        Attempts = 0,
    };

    /// <summary>
    /// Writes a file back to the working tree, preserving its encoding and line endings.
    ///
    /// The policy lives here rather than in <see cref="WorkingTreeWriter"/>, which is a
    /// mechanism and stays able to create files. This is the bridge-facing path, and what
    /// reaches it is a path and a string chosen by the front-end — so every property the
    /// read side asserted has to be re-established rather than trusted:
    ///
    /// <list type="bullet">
    /// <item>the worktree must be one the app has actually opened, or any directory on the
    /// machine is a write root;</item>
    /// <item>the file must already exist, or saving an untouched empty buffer resurrects a
    /// file an agent deleted, as a zero-byte file, reporting success;</item>
    /// <item>it must not be binary, or a PNG is replaced by the text of whatever was last
    /// in the editor.</item>
    /// </list>
    /// </summary>
    public async Task<SaveResult> SaveFileAsync(
        string worktreePath, string repoRelativePath, string text, CancellationToken ct = default)
    {
        if (!IsKnownWorktree(worktreePath))
            return Refuse(worktreePath, repoRelativePath, "that worktree is not open in this window");

        var existing = await DiffService
            .GetWorkingContentAsync(worktreePath, repoRelativePath, ct)
            .ConfigureAwait(false);

        var absolute = ResolveForWrite(worktreePath, repoRelativePath);

        if (absolute is null || !File.Exists(absolute))
            return Refuse(worktreePath, repoRelativePath, "that file is not in the working tree");

        if (existing.IsBinary)
            return Refuse(worktreePath, repoRelativePath, "that file is binary");

        if (!existing.CanRoundTrip)
            return Refuse(worktreePath, repoRelativePath,
                "that file cannot be saved without reformatting it — its encoding or line endings would change");

        using var scope = Writer.SelfWriteScope?.Invoke(worktreePath);

        var result = await WorkingTreeWriter
            .SaveAsync(worktreePath, repoRelativePath, text, existing.Format, ct)
            .ConfigureAwait(false);

        if (result.Success) InvalidateChanges(worktreePath);

        Log.Append(new OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = "save",
            WorktreePath = worktreePath,
            // Not a git command, but the log is the record of what the app did to the
            // repository, and writing a file is squarely that.
            CommandLine = $"write {repoRelativePath}",
            ExitCode = result.Success ? 0 : 1,
            Detail = result.Error,
            Failure = result.Success ? null : "SaveFailed",
        });

        return result;
    }

    /// <summary>Records a refused save, so the log answers "why did nothing happen".</summary>
    private SaveResult Refuse(string worktreePath, string repoRelativePath, string reason)
    {
        Log.Append(new OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = "save",
            WorktreePath = worktreePath,
            CommandLine = $"write {repoRelativePath}",
            ExitCode = 1,
            Detail = reason,
            Failure = "Refused",
        });

        return SaveResult.Failed(repoRelativePath, reason);
    }

    private static string? ResolveForWrite(string worktreePath, string repoRelativePath)
    {
        if (RepoPaths.EntersGitDirectory(repoRelativePath)) return null;

        try
        {
            return RepoPaths.Resolve(worktreePath, repoRelativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the app has actually opened this worktree.
    ///
    /// The bridge takes a worktree path as a parameter, so without this any directory on
    /// the machine is a valid write root — and the front-end renders content an agent
    /// wrote. Membership is recorded as worktrees are listed rather than re-derived per
    /// call, because the answer is needed on a write and asking git for it there would put
    /// two more processes in front of every save.
    /// </summary>
    public bool IsKnownWorktree(string worktreePath) => _knownWorktrees.ContainsKey(worktreePath);

    /// <summary>
    /// Image extensions the preview will inline. Anything else is refused rather than
    /// guessed at — an unknown type served under a wrong MIME is worse than a placeholder.
    /// </summary>
    private static readonly Dictionary<string, string> InlineableImages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        // Safe here specifically because it is rendered inside <img>, where browsers
        // disable scripting for SVG. Never inline one into the document itself.
        [".svg"] = "image/svg+xml",
    };

    /// <summary>Images above this are linked, not inlined — base64 adds a third again on top.</summary>
    private const long MaxInlineBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads an image referenced by a Markdown file and returns it as a data URI.
    ///
    /// Every failure is reported rather than thrown: a preview with one missing image
    /// should still render, showing a placeholder where that image belongs.
    /// </summary>
    public static async Task<AssetPayload> GetAssetAsync(
        string worktreePath, string repoRelativePath, CancellationToken ct = default)
    {
        string absolute;
        try
        {
            // Throws for anything escaping the worktree — a Markdown file is untrusted
            // input, and `![](../../../../Windows/win.ini)` is a plausible thing to find
            // in a file an agent wrote.
            absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
        }
        catch (ArgumentException)
        {
            return new AssetPayload { Path = repoRelativePath, Reason = "outside the worktree" };
        }

        var extension = Path.GetExtension(absolute);
        if (!InlineableImages.TryGetValue(extension, out var mediaType))
            return new AssetPayload { Path = repoRelativePath, Reason = "unsupported image type" };

        var info = new FileInfo(absolute);
        if (!info.Exists)
            return new AssetPayload { Path = repoRelativePath, Reason = "not found" };

        if (info.Length > MaxInlineBytes)
            return new AssetPayload { Path = repoRelativePath, Reason = "too large to preview" };

        try
        {
            var bytes = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
            return new AssetPayload
            {
                Path = repoRelativePath,
                DataUri = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AssetPayload { Path = repoRelativePath, Reason = "could not be read" };
        }
    }

    private async Task<FileContent> ContentAtScopeEndAsync(
        string worktreePath, string repoRelativePath, DiffScope scope, CancellationToken ct)
    {
        var diffBase = await _bases.ResolveBaseAsync(worktreePath, scope, ct).ConfigureAwait(false);

        return diffBase.ToRef is null
            ? await DiffService.GetWorkingContentAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false)
            : await _diff.GetContentAtAsync(worktreePath, diffBase.ToRef, repoRelativePath, ct).ConfigureAwait(false);
    }
}
