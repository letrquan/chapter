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
            // This costs four git processes per mutation, which is nothing for a commit and
            // will not be nothing for Phase 1's per-hunk staging. Cache it there, against
            // the watcher's git-state signal, rather than dropping the check.
            Guard = async (worktree, kind, ct) =>
                (await _state.ReadAsync(worktree, ct).ConfigureAwait(false)).CanWrite(kind),
        };

        Undo = new UndoService(git, Writer);
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

    /// <summary>Builds both sides of a file's diff, ready for Monaco's diff editor.</summary>
    public async Task<DiffPayload> GetDiffAsync(
        string worktreePath, string repoRelativePath, DiffScope scope = DiffScope.Branch, CancellationToken ct = default)
    {
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

    /// <summary>What state the worktree's repository is in — mid-merge, mid-rebase, conflicted.</summary>
    public Task<RepositoryState> GetRepositoryStateAsync(string worktreePath, CancellationToken ct = default) =>
        _state.ReadAsync(worktreePath, ct);

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
