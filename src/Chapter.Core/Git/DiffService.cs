namespace Chapter.Core.Git;

/// <summary>
/// Builds the changed-file set for a worktree and fetches the two sides of a file's diff.
///
/// The set is assembled from two sources, and both are required:
/// <c>git diff &lt;base&gt;</c> covers every tracked change from the base commit through to
/// the working tree (committed, staged and unstaged alike), while untracked files come
/// from <c>git status</c>. Omitting the second source hides brand-new files entirely —
/// which, when reviewing an agent's work, are usually the most important files there are.
/// </summary>
public sealed class DiffService(GitCli git)
{
    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(
        string worktreePath, DiffBase diffBase, CancellationToken ct = default)
    {
        // A comparison ending at a commit takes two revisions; one ending at the working
        // tree takes only the starting revision.
        string[] range = diffBase.ToRef is null ? [diffBase.Sha] : [diffBase.Sha, diffBase.ToRef];

        // Independent probes, each spawning a process, so run them together.
        var nameStatusTask = git.RunAsync(worktreePath, ct, ["diff", "--name-status", "-M", "-z", .. range]);
        var numstatTask = git.RunAsync(worktreePath, ct, ["diff", "--numstat", "-M", "-z", .. range]);

        // Status is still needed even when untracked files are excluded: it is what marks
        // which files in a branch-wide view are still dirty.
        var statusTask = git.RunAsync(worktreePath, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all");

        await Task.WhenAll(nameStatusTask, numstatTask, statusTask).ConfigureAwait(false);

        var files = ParseNameStatus(await nameStatusTask.ConfigureAwait(false));
        var stats = ParseNumstat(await numstatTask.ConfigureAwait(false));
        var working = ParseWorkingState(await statusTask.ConfigureAwait(false));

        var merged = new List<ChangedFile>(files.Count + working.Untracked.Count);

        foreach (var file in files)
        {
            var withStats = stats.TryGetValue(file.Path, out var stat)
                ? file with { LinesAdded = stat.Added, LinesRemoved = stat.Removed, IsBinary = stat.IsBinary }
                : file;

            merged.Add(withStats with
            {
                IsUncommitted = working.Dirty.Contains(file.Path),
                // Guarded because the list is empty in every repository that is not
                // mid-merge, which is nearly all of them.
                IsConflicted = working.Unmerged.Count > 0 && working.Unmerged.Contains(file.Path),
                StagedKind = working.Staged.TryGetValue(file.Path, out var stagedKind) ? stagedKind : null,
                UnstagedKind = working.Unstaged.TryGetValue(file.Path, out var unstagedKind) ? unstagedKind : null,
            });
        }

        if (diffBase.IncludeUntracked)
        {
            // Untracked files have no git-side counterpart, so line counts come from the
            // file itself: every line is an addition.
            var known = merged.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in working.Untracked)
            {
                if (!known.Add(path)) continue;

                var (lines, isBinary) = await CountWorkingLinesAsync(worktreePath, path, ct).ConfigureAwait(false);
                merged.Add(new ChangedFile
                {
                    Path = path,
                    Kind = ChangeKind.Untracked,
                    LinesAdded = lines,
                    IsBinary = isBinary,
                    IsUncommitted = true,
                });
            }
        }

        merged.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return merged;
    }

    /// <summary>
    /// Parses <c>git diff --name-status -M -z</c>. With <c>-z</c> the tab separators become
    /// NULs, so a rename arrives as three consecutive fields rather than one line.
    /// </summary>
    internal static List<ChangedFile> ParseNameStatus(string output)
    {
        var tokens = RepoPaths.SplitNul(output);
        var files = new List<ChangedFile>();

        for (var i = 0; i < tokens.Length;)
        {
            var status = tokens[i++];
            if (status.Length == 0) continue;

            var code = status[0];
            var isRenameOrCopy = code is 'R' or 'C';

            // Renames and copies carry a similarity score (R100, C085) and two paths.
            int? similarity = null;
            if (isRenameOrCopy && status.Length > 1 && int.TryParse(status[1..], out var score))
                similarity = score;

            string path;
            string? oldPath = null;

            if (isRenameOrCopy)
            {
                if (i + 1 >= tokens.Length) break;
                oldPath = tokens[i++];
                path = tokens[i++];
            }
            else
            {
                if (i >= tokens.Length) break;
                path = tokens[i++];
            }

            files.Add(new ChangedFile
            {
                Path = path,
                OldPath = oldPath,
                Kind = MapStatus(code),
                Similarity = similarity,
            });
        }

        return files;
    }

    private static ChangeKind MapStatus(char code) => code switch
    {
        'A' => ChangeKind.Added,
        'M' => ChangeKind.Modified,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        _ => ChangeKind.Modified,
    };

    /// <summary>
    /// Parses <c>git diff --numstat -M -z</c>, keyed by new path.
    ///
    /// The rename form is the awkward one: git emits <c>add\tdel\t\0oldpath\0newpath\0</c>,
    /// so the path field of the first record is empty and the two real paths follow as
    /// separate NUL-terminated fields. Treating every record as self-contained silently
    /// mis-associates line counts from the first rename onwards.
    /// </summary>
    internal static Dictionary<string, (int Added, int Removed, bool IsBinary)> ParseNumstat(string output)
    {
        var tokens = RepoPaths.SplitNul(output);
        var stats = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);

        for (var i = 0; i < tokens.Length;)
        {
            var record = tokens[i++];
            var parts = record.Split('\t');
            if (parts.Length < 3) continue;

            // Git writes "-" for both counts on binary files.
            var isBinary = parts[0] == "-" || parts[1] == "-";
            var added = isBinary ? 0 : ParseIntOrZero(parts[0]);
            var removed = isBinary ? 0 : ParseIntOrZero(parts[1]);

            string path;
            if (parts[2].Length == 0)
            {
                // Rename or copy: the following two fields are old path then new path.
                if (i + 1 >= tokens.Length) break;
                i++;                 // old path — the name-status pass already recorded it
                path = tokens[i++];  // new path is what we key on
            }
            else
            {
                path = parts[2];
            }

            stats[path] = (added, removed, isBinary);
        }

        return stats;
    }

    private static int ParseIntOrZero(string s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>
    /// Working-tree state extracted from a single <c>git status</c> run.
    /// </summary>
    internal sealed record WorkingState
    {
        /// <summary>Paths git does not track, in the order it reported them.</summary>
        public List<string> Untracked { get; init; } = [];

        /// <summary>Tracked paths that differ from HEAD, staged or not.</summary>
        public HashSet<string> Dirty { get; init; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Paths with an unresolved merge conflict. Empty in any repository that is not
        /// part-way through a merge, rebase, cherry-pick or revert.
        /// </summary>
        public List<string> Unmerged { get; init; } = [];

        /// <summary>How the index differs from HEAD, per path — porcelain's X column.</summary>
        public Dictionary<string, ChangeKind> Staged { get; init; } = new(StringComparer.Ordinal);

        /// <summary>How the working tree differs from the index, per path — porcelain's Y column.</summary>
        public Dictionary<string, ChangeKind> Unstaged { get; init; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Maps one half of porcelain v2's <c>XY</c> field onto a change kind.
    ///
    /// '.' means "no change on this side" and is the caller's signal to record nothing at
    /// all, which is why it returns null rather than a kind: the difference between "not
    /// staged" and "staged as a modification" is the entire point of reading these two
    /// columns separately.
    /// </summary>
    private static ChangeKind? MapPorcelainState(char code) => code switch
    {
        'M' => ChangeKind.Modified,
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        _ => null,
    };

    /// <summary>
    /// Extracts working-tree state from <c>git status --porcelain=v2 -z</c>: the untracked
    /// paths, the tracked paths that differ from HEAD, and the unmerged ones.
    ///
    /// Every entry is NUL-terminated, but rename entries (type <c>2</c>) are followed by a
    /// second NUL-terminated field holding the original path. That trailing field has to be
    /// consumed explicitly or it gets read as the start of the next entry.
    /// </summary>
    internal static WorkingState ParseWorkingState(string output)
    {
        var tokens = RepoPaths.SplitNul(output);
        var state = new WorkingState();

        for (var i = 0; i < tokens.Length;)
        {
            var entry = tokens[i++];
            if (entry.Length < 2) continue;

            switch (entry[0])
            {
                case '?':
                {
                    var path = entry[2..];

                    // The app's own atomic-write scratch file. It exists for milliseconds
                    // beside the file being saved, and listing it would show a phantom
                    // untracked file — and invite an agent's `git add -A` to commit it.
                    if (path.EndsWith(WorkingTreeWriter.TempSuffix, StringComparison.OrdinalIgnoreCase)) break;

                    state.Untracked.Add(path);
                    break;
                }

                case '1':
                case '2':
                {
                    // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>", with an extra rename
                    // score field for type 2. X is index-vs-HEAD, Y is worktree-vs-index;
                    // either being set means the file is not fully committed.
                    var isRename = entry[0] == '2';
                    var path = FieldAfter(entry, isRename ? 9 : 8);

                    if (path is not null && entry.Length > 3)
                    {
                        // Read as two independent answers rather than one. `MM` is a file
                        // staged and then edited again, and it has to reach the commit view
                        // as both a staged change and an unstaged one — the user is choosing
                        // between two versions of it.
                        var staged = MapPorcelainState(entry[2]);
                        var unstaged = MapPorcelainState(entry[3]);

                        if (staged is not null) state.Staged[path] = staged.Value;
                        if (unstaged is not null) state.Unstaged[path] = unstaged.Value;

                        if (staged is not null || unstaged is not null) state.Dirty.Add(path);
                    }

                    // Renamed/copied entry: consume its origin-path field too.
                    if (isRename && i < tokens.Length) i++;
                    break;
                }

                case 'u':
                {
                    // "u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>" — ten fields
                    // before the path, three of them the stage-1/2/3 blob hashes that make
                    // an unmerged entry longer than an ordinary one.
                    //
                    // These used to be skipped entirely, which meant a conflicted file was
                    // reported as neither changed nor dirty: invisible in the very state
                    // where it is the only thing that matters.
                    var path = FieldAfter(entry, 10);
                    if (path is null) break;

                    state.Unmerged.Add(path);
                    state.Dirty.Add(path);
                    break;
                }
            }
        }

        return state;
    }

    /// <summary>
    /// Returns everything after the first <paramref name="count"/> space-separated fields.
    /// Split-on-space would corrupt any path containing a space, and those exist.
    /// </summary>
    private static string? FieldAfter(string entry, int count)
    {
        var position = 0;
        for (var field = 0; field < count; field++)
        {
            var space = entry.IndexOf(' ', position);
            if (space < 0) return null;
            position = space + 1;
        }

        return position < entry.Length ? entry[position..] : null;
    }

    /// <summary>File content as it exists at a revision.</summary>
    public async Task<FileContent> GetContentAtAsync(
        string worktreePath, string revision, string repoRelativePath, CancellationToken ct = default)
    {
        var result = await git.RunBytesAsync(worktreePath, ct, "show", $"{revision}:{repoRelativePath}")
            .ConfigureAwait(false);

        // A non-zero exit here means the path did not exist at that revision, which is the
        // normal case for an added file rather than an error worth surfacing.
        return result.Success ? FileContent.FromBytes(result.StandardOutput) : FileContent.Empty;
    }

    /// <summary>File content as it exists at the comparison base.</summary>
    public Task<FileContent> GetBaseContentAsync(
        string worktreePath, string baseSha, string repoRelativePath, CancellationToken ct = default) =>
        GetContentAtAsync(worktreePath, baseSha, repoRelativePath, ct);

    /// <summary>File content as it currently exists on disk.</summary>
    public static async Task<FileContent> GetWorkingContentAsync(
        string worktreePath, string repoRelativePath, CancellationToken ct = default)
    {
        var absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
        if (!File.Exists(absolute)) return FileContent.Empty;

        var bytes = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
        return FileContent.FromBytes(bytes);
    }

    private static async Task<(int Lines, bool IsBinary)> CountWorkingLinesAsync(
        string worktreePath, string repoRelativePath, CancellationToken ct)
    {
        var content = await GetWorkingContentAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false);
        if (content.IsBinary) return (0, true);
        if (content.Text.Length == 0) return (0, false);

        var lines = content.Text.AsSpan().Count('\n');
        // A final line without a trailing newline still counts.
        if (!content.Text.EndsWith('\n')) lines++;
        return (lines, false);
    }
}
