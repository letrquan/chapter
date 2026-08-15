namespace Chapter.Core.Git;

/// <summary>
/// What a discard should throw away.
///
/// The two are different commands with different blast radii, and offering one where the
/// user meant the other loses work silently — so the caller has to say which.
/// </summary>
public enum DiscardTarget
{
    /// <summary>
    /// Working-tree edits only, restoring the file from the index. Anything staged survives.
    /// This is what "discard" means next to an unstaged change.
    /// </summary>
    Unstaged,

    /// <summary>
    /// Everything uncommitted about the file — index and working tree both restored from
    /// HEAD. This is what "discard" means in the review view, where the index is not shown.
    /// </summary>
    Everything,
}

/// <summary>
/// The uncommitted work of a worktree, split the way a commit splits it.
///
/// Deliberately not derived from the review scan. That scan answers "what changed on this
/// branch", assembled from a base commit; this answers "what would <c>git commit</c> take,
/// and what would it leave behind", which is a different question with a different source —
/// <c>diff --cached</c> and <c>diff</c> respectively. Deriving one from the other is where a
/// commit UI starts lying: a file staged and then deleted from disk is in neither the
/// branch-wide diff nor the working tree, and committing still includes it.
/// </summary>
public sealed record CommitView
{
    public required string WorktreePath { get; init; }

    /// <summary>HEAD to the index: exactly what a commit right now would contain.</summary>
    public required IReadOnlyList<ChangedFile> Staged { get; init; }

    /// <summary>Index to the working tree, plus untracked files: what a commit would leave.</summary>
    public required IReadOnlyList<ChangedFile> Unstaged { get; init; }

    /// <summary>Merge/rebase state and conflicts, which decide whether committing is legal.</summary>
    public required RepositoryState Repository { get; init; }

    /// <summary>The branch a commit would land on, or null when HEAD is detached.</summary>
    public string? Branch => Repository.Branch;

    public bool IsUnborn => Repository.IsUnborn;

    /// <summary>
    /// Whether a commit may be attempted, and the one sentence to show when it may not.
    ///
    /// Phase 1's guard list, answered before the user types a message rather than after.
    /// Every branch here is a state git would refuse anyway — the value is in refusing
    /// first, and in saying which of the four it is.
    /// </summary>
    public CommitReadiness Readiness => ReadinessFor(amend: false);

    /// <summary>
    /// The same question asked about an amend, which has a different answer in one place.
    ///
    /// An amend needs nothing staged. Rewording the last commit's subject — a typo, a
    /// missing issue number — is the single most common reason to amend anything, and it
    /// stages nothing by definition. Answering it with "Nothing is staged" refuses the one
    /// case the button exists for.
    /// </summary>
    public CommitReadiness AmendReadiness => ReadinessFor(amend: true);

    private CommitReadiness ReadinessFor(bool amend)
    {
        if (Repository.ProbeFailed)
            return CommitReadiness.Blocked("The repository's state could not be read.");

        if (Repository.HasConflicts)
            return CommitReadiness.Blocked(
                $"{Repository.ConflictedPaths.Count} file(s) still have unresolved conflicts. " +
                "Resolve them and stage the results first.");

        // There is no previous commit to replace, so this is a hard no rather than advice.
        if (amend && IsUnborn)
            return CommitReadiness.Blocked("There is no previous commit to amend.");

        // Not a blocker. A merge that has been resolved is concluded *by* committing, so
        // refusing here would leave the user with no way out of the state through the
        // app — the exact mistake WriteKind exists to avoid.
        var note = Repository.IsOperationInProgress
            ? $"This will conclude the {Repository.Description}."
            : Repository.IsDetached
                ? "HEAD is detached — this commit will not be on any branch."
                : null;

        if (!amend && Staged.Count == 0)
            return new CommitReadiness(false, "Nothing is staged.", note);

        // Worth saying, because "amend" with an empty index means the commit keeps its
        // content and only the message changes — which is usually the intent, but not
        // always, and the difference is invisible otherwise.
        if (amend && Staged.Count == 0)
            return new CommitReadiness(true, null, note ?? "Nothing is staged — this rewords the last commit.");

        return new CommitReadiness(true, null, note);
    }
}

/// <summary>Whether a commit can proceed, why not, and anything worth saying first.</summary>
public sealed record CommitReadiness(bool CanCommit, string? Reason, string? Note = null)
{
    public static CommitReadiness Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Moves changes between the working tree, the index and nowhere.
///
/// Every mutation goes through <see cref="GitWriter"/> — never <see cref="GitCli"/>
/// directly — so it inherits the write environment, the lock retry, the classified failure
/// and the operation-log line. All of these are <see cref="WriteKind.WorkingTree"/>: they
/// are legal mid-merge, where staging a resolved file is precisely how the merge ends.
/// </summary>
public sealed class StagingService(GitCli git, GitWriter writer)
{
    /// <summary>
    /// Wraps a path so git reads it as a literal name rather than a pattern.
    ///
    /// Paths arrive from git's own output and go straight back to git as pathspecs, where
    /// <c>*</c>, <c>?</c> and <c>[</c> are wildcards. A file genuinely called <c>a[1].txt</c>
    /// then matches nothing and the stage silently does nothing — or, worse, a pattern
    /// matches files the user never selected. The magic prefix turns the pathspec back into
    /// the plain name it was.
    /// </summary>
    internal static string Literal(string path) => $":(literal){path}";

    /// <summary>
    /// Stages whole files. Also the command that marks a conflicted file resolved, which is
    /// why it stays legal while a merge is in progress.
    /// </summary>
    public Task<GitMutation> StageAsync(
        string worktreePath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return Task.FromResult(NoPaths(worktreePath, "stage"));

        // `add` rather than `add --all`: it covers modifications, additions and deletions of
        // the named paths and nothing else. A pathspec-less --all would sweep up whatever
        // the agent is in the middle of writing.
        return writer.RunAsync(
            worktreePath, Describe("stage", paths), WriteKind.WorkingTree, ct,
            ["add", "--", .. paths.Select(Literal)]);
    }

    /// <summary>
    /// Unstages whole files, leaving the working tree untouched.
    ///
    /// Two commands, because <c>restore --staged</c> resolves HEAD and there is not always
    /// one: in a repository whose first commit has not happened yet it exits 128 with
    /// "could not resolve HEAD", and the file stays staged. <c>rm --cached</c> has no such
    /// dependency — it removes the index entry outright, which for a file that exists only
    /// in the index is exactly the same outcome.
    /// </summary>
    public async Task<GitMutation> UnstageAsync(
        string worktreePath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return NoPaths(worktreePath, "unstage");

        var operation = Describe("unstage", paths);
        var spec = paths.Select(Literal).ToArray();

        var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
            .ConfigureAwait(false);

        var hasCommit = head.Success && head.Trimmed.Length > 0;

        return hasCommit
            ? await writer.RunAsync(
                    worktreePath, operation, WriteKind.WorkingTree, ct,
                    ["restore", "--staged", "--", .. spec])
                .ConfigureAwait(false)
            // -q because the porcelain form prints every removed path to stdout, and the
            // operation log wants the command, not a file listing.
            : await writer.RunAsync(
                    worktreePath, operation, WriteKind.WorkingTree, ct,
                    ["rm", "--cached", "-q", "--", .. spec])
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Throws away uncommitted changes to whole files. Destructive and not recoverable —
    /// working-tree content that was never staged is in no git object, so the reflog cannot
    /// bring it back. Callers must confirm first.
    /// </summary>
    /// <param name="untracked">
    /// Paths git does not track. These need deleting rather than restoring: <c>restore</c>
    /// has no source to restore them from and fails with "pathspec did not match", so an
    /// untracked file passed in with the rest would leave the whole command failing and
    /// nothing discarded.
    /// </param>
    public async Task<GitMutation> DiscardAsync(
        string worktreePath,
        IReadOnlyList<string> paths,
        DiscardTarget target = DiscardTarget.Unstaged,
        IReadOnlyList<string>? untracked = null,
        CancellationToken ct = default)
    {
        untracked ??= [];

        if (paths.Count == 0 && untracked.Count == 0) return NoPaths(worktreePath, "discard");

        var operation = Describe("discard", [.. paths, .. untracked]);

        // Deleting untracked files first. If the restore below fails there is no partial
        // state to explain — the two halves are independent — and doing it in this order
        // keeps the reported failure the interesting one.
        var removed = DeleteUntracked(worktreePath, untracked);

        if (paths.Count == 0)
        {
            return removed.Failed.Count == 0
                ? Synthetic(worktreePath, operation, $"Deleted {Count(removed.Deleted.Count, "untracked file")}")
                : Failure(worktreePath, operation,
                    $"Could not delete {string.Join(", ", removed.Failed)}");
        }

        var spec = paths.Select(Literal).ToArray();

        // --source=HEAD is what makes the second form throw away the staged version too;
        // without it, restoring the worktree from the index reinstates precisely the change
        // the user asked to be rid of.
        //
        // Before the first commit there is no HEAD to name, and `restore` exits 128 with
        // "could not resolve HEAD" — the same trap UnstageAsync has to work around. The
        // empty tree is what HEAD would mean here if it existed: every tracked file is an
        // addition, so restoring from it removes them.
        string[] args;

        if (target is DiscardTarget.Everything)
        {
            var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
                .ConfigureAwait(false);

            var source = head.Success && head.Trimmed.Length > 0 ? "HEAD" : EmptyTreeSha;
            args = ["restore", $"--source={source}", "--staged", "--worktree", "--", .. spec];
        }
        else
        {
            args = ["restore", "--worktree", "--", .. spec];
        }

        var mutation = await writer
            .RunAsync(worktreePath, operation, WriteKind.WorkingTree, ct, args)
            .ConfigureAwait(false);

        if (mutation.Success && removed.Failed.Count > 0)
        {
            return mutation with
            {
                ExitCode = 1,
                Detail = $"Restored the tracked files, but could not delete {string.Join(", ", removed.Failed)}",
            };
        }

        return mutation;
    }

    /// <summary>
    /// Removes untracked files from disk, reporting rather than throwing on the ones it
    /// could not remove — a file an agent still holds open is the ordinary case here, and it
    /// must not take the rest of the discard with it.
    /// </summary>
    private static (List<string> Deleted, List<string> Failed) DeleteUntracked(
        string worktreePath, IReadOnlyList<string> untracked)
    {
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var path in untracked)
        {
            // Resolved through RepoPaths so a path that climbs out of the worktree cannot
            // delete something elsewhere on the machine. The paths come from git here, but
            // this method is one bridge parameter away from the front-end.
            string absolute;
            try
            {
                if (RepoPaths.EntersGitDirectory(path)) { failed.Add(path); continue; }
                absolute = RepoPaths.Resolve(worktreePath, path);
            }
            catch (ArgumentException)
            {
                failed.Add(path);
                continue;
            }

            try
            {
                if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
                else if (File.Exists(absolute)) File.Delete(absolute);

                deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(path);
            }
        }

        return (deleted, failed);
    }

    /// <summary>
    /// Reads the index and working tree as two separate change sets.
    ///
    /// Three diffs rather than one, run together: what is staged, what is not, and the
    /// untracked files that belong with the second. <c>--no-renames</c> is deliberate on the
    /// unstaged side — rename detection across the index boundary reports a file as renamed
    /// when only half the rename is staged, and the staging buttons then act on a path pair
    /// the user never saw.
    /// </summary>
    public async Task<CommitView> ReadAsync(
        string worktreePath, RepositoryState repository, CancellationToken ct = default)
    {
        // An unborn HEAD has nothing to diff --cached against; `--cached` alone compares the
        // index to the empty tree only if a revision is supplied, so the empty-tree hash is
        // named explicitly. Every file in the index of a fresh repository is an addition.
        string[] stagedArgs = repository.IsUnborn
            ? ["diff", "--cached", "--name-status", "-M", "-z", EmptyTreeSha]
            : ["diff", "--cached", "--name-status", "-M", "-z"];

        string[] stagedNumstatArgs = repository.IsUnborn
            ? ["diff", "--cached", "--numstat", "-M", "-z", EmptyTreeSha]
            : ["diff", "--cached", "--numstat", "-M", "-z"];

        var stagedTask = git.TryRunAsync(worktreePath, ct, stagedArgs);
        var unstagedTask = git.TryRunAsync(worktreePath, ct, "diff", "--name-status", "--no-renames", "-z");
        var statusTask = git.TryRunAsync(
            worktreePath, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all");

        var numstatStagedTask = git.TryRunAsync(worktreePath, ct, stagedNumstatArgs);

        var numstatUnstagedTask = git.TryRunAsync(
            worktreePath, ct, "diff", "--numstat", "--no-renames", "-z");

        await Task.WhenAll(stagedTask, unstagedTask, statusTask, numstatStagedTask, numstatUnstagedTask)
            .ConfigureAwait(false);

        var status = await statusTask.ConfigureAwait(false);
        var working = status.Success
            ? DiffService.ParseWorkingState(status.StandardOutput)
            : new DiffService.WorkingState();

        var conflicted = working.Unmerged.ToHashSet(StringComparer.Ordinal);

        var staged = Build(
            await stagedTask.ConfigureAwait(false),
            await numstatStagedTask.ConfigureAwait(false),
            conflicted,
            isStagedSide: true);

        var unstaged = Build(
            await unstagedTask.ConfigureAwait(false),
            await numstatUnstagedTask.ConfigureAwait(false),
            conflicted,
            isStagedSide: false);

        // Untracked files are working-tree-only by definition, so they join the unstaged
        // side. Line counts come from the file itself: every line is an addition.
        foreach (var path in working.Untracked)
        {
            var (lines, isBinary) = await CountLinesAsync(worktreePath, path, ct).ConfigureAwait(false);
            unstaged.Add(new ChangedFile
            {
                Path = path,
                Kind = ChangeKind.Untracked,
                LinesAdded = lines,
                IsBinary = isBinary,
                IsUncommitted = true,
                UnstagedKind = null,
            });
        }

        // A conflicted file appears in git's unstaged diff as a modification against a stage
        // that does not mean what the UI would show. Listing it once, marked, is the honest
        // rendering; Phase 6 gives it real actions.
        foreach (var path in conflicted)
        {
            if (unstaged.Any(f => f.Path == path)) continue;

            unstaged.Add(new ChangedFile
            {
                Path = path,
                Kind = ChangeKind.Modified,
                IsUncommitted = true,
                IsConflicted = true,
            });
        }

        staged.Sort(ByPath);
        unstaged.Sort(ByPath);

        return new CommitView
        {
            WorktreePath = worktreePath,
            Staged = staged,
            Unstaged = unstaged,
            Repository = repository,
        };
    }

    /// <summary>
    /// Git's hash of the empty tree — a constant of the object format, not of any
    /// repository. Naming it is how <c>diff --cached</c> is given something to compare
    /// against before the first commit exists.
    /// </summary>
    internal const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private static readonly Comparison<ChangedFile> ByPath =
        (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    private static List<ChangedFile> Build(
        GitResult nameStatus, GitResult numstat, HashSet<string> conflicted, bool isStagedSide)
    {
        // A failed diff yields an empty side rather than an exception: half a commit view is
        // still worth rendering, and the repository state carries the real alarm.
        if (!nameStatus.Success) return [];

        var files = DiffService.ParseNameStatus(nameStatus.StandardOutput);
        var stats = numstat.Success
            ? DiffService.ParseNumstat(numstat.StandardOutput)
            : new Dictionary<string, (int Added, int Removed, bool IsBinary)>(StringComparer.Ordinal);

        var built = new List<ChangedFile>(files.Count);

        foreach (var file in files)
        {
            var withStats = stats.TryGetValue(file.Path, out var stat)
                ? file with { LinesAdded = stat.Added, LinesRemoved = stat.Removed, IsBinary = stat.IsBinary }
                : file;

            built.Add(withStats with
            {
                IsUncommitted = true,
                IsConflicted = conflicted.Count > 0 && conflicted.Contains(file.Path),
                StagedKind = isStagedSide ? file.Kind : null,
                UnstagedKind = isStagedSide ? null : file.Kind,
            });
        }

        return built;
    }

    private static async Task<(int Lines, bool IsBinary)> CountLinesAsync(
        string worktreePath, string path, CancellationToken ct)
    {
        var content = await DiffService.GetWorkingContentAsync(worktreePath, path, ct).ConfigureAwait(false);
        if (content.IsBinary) return (0, true);
        if (content.Text.Length == 0) return (0, false);

        var lines = content.Text.AsSpan().Count('\n');
        if (!content.Text.EndsWith('\n')) lines++;
        return (lines, false);
    }

    // -----------------------------------------------------------------------
    // Outcomes that never reached git
    // -----------------------------------------------------------------------

    private static string Describe(string verb, IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"{verb} {paths[0]}" : $"{verb} {paths.Count} files";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static GitMutation NoPaths(string worktreePath, string verb) => new()
    {
        Operation = verb,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.NothingToDo,
        Detail = $"Nothing selected to {verb}",
        Attempts = 0,
    };

    /// <summary>An outcome the app produced without running git — deleting untracked files.</summary>
    private static GitMutation Synthetic(string worktreePath, string operation, string detail) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = detail,
        ExitCode = 0,
        Attempts = 0,
    };

    private static GitMutation Failure(string worktreePath, string operation, string detail) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = 1,
        Failure = GitFailure.Unknown,
        Detail = detail,
        Attempts = 0,
    };
}
