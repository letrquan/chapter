using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Chapter.Core.Git;

/// <summary>One path whose live working-tree contents differ between two worktrees.</summary>
public sealed record WorktreeComparisonFile
{
    /// <summary>The right-hand path, except for a deletion where only the left path exists.</summary>
    public required string Path { get; init; }

    /// <summary>The left-hand path for an exact rename; null for every other change.</summary>
    public string? OldPath { get; init; }

    /// <summary>The path to read on the left. Empty when the file exists only on the right.</summary>
    public string LeftPath { get; init; } = "";

    /// <summary>The path to read on the right. Empty when the file exists only on the left.</summary>
    public string RightPath { get; init; } = "";

    public required ChangeKind Kind { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public bool IsBinary { get; init; }
    public int LeftBytes { get; init; }
    public int RightBytes { get; init; }
    public bool LeftExists => LeftPath.Length > 0;
    public bool RightExists => RightPath.Length > 0;
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>The live, read-only comparison of two sibling worktrees.</summary>
public sealed record WorktreeComparison
{
    public required Worktree Left { get; init; }
    public required Worktree Right { get; init; }
    public required IReadOnlyList<WorktreeComparisonFile> Files { get; init; }
    public int TotalAdded => Files.Sum(file => file.LinesAdded);
    public int TotalRemoved => Files.Sum(file => file.LinesRemoved);
}

/// <summary>Both live sides of one cross-worktree file comparison.</summary>
public sealed record WorktreeComparisonContent
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string LeftPath { get; init; }
    public required string RightPath { get; init; }
    public required string LeftText { get; init; }
    public required string RightText { get; init; }
    public bool LeftExists { get; init; }
    public bool RightExists { get; init; }
    public bool IsBinary { get; init; }
    public int LeftBytes { get; init; }
    public int RightBytes { get; init; }
}

/// <summary>
/// Compares the live files in two worktrees of one repository.
///
/// The candidate set is deliberately narrower than "walk both directories". A worktree
/// can contain gigabytes of ignored build output, credentials, package caches and agent
/// scratch files that Git says are not part of the solution. We start with tracked files,
/// add non-ignored untracked files, then inspect only paths whose commits, index or working
/// tree can differ. The final decision is made from the bytes on disk, so staged and
/// unstaged edits are compared together exactly as the user sees them.
/// </summary>
public sealed class WorktreeComparisonService(GitCli git)
{
    private const int MaxParallelReads = 4;

    public async Task<WorktreeComparison> CompareAsync(
        Worktree left, Worktree right, CancellationToken ct = default)
    {
        ValidatePair(left, right);
        await EnsureSameRepositoryAsync(left, right, ct).ConfigureAwait(false);

        var leftSnapshotTask = ReadSnapshotAsync(left, ct);
        var rightSnapshotTask = ReadSnapshotAsync(right, ct);
        var committedTask = ReadCommittedDifferencesAsync(left, right, ct);

        await Task.WhenAll(leftSnapshotTask, rightSnapshotTask, committedTask).ConfigureAwait(false);

        var leftSnapshot = await leftSnapshotTask.ConfigureAwait(false);
        var rightSnapshot = await rightSnapshotTask.ConfigureAwait(false);
        var committed = await committedTask.ConfigureAwait(false);

        var candidates = new HashSet<string>(committed, StringComparer.Ordinal);
        candidates.UnionWith(leftSnapshot.Dirty);
        candidates.UnionWith(rightSnapshot.Dirty);

        // Membership changes catch staged renames and deletions: porcelain names the new
        // path, while the old path is no longer in that worktree's index.
        foreach (var path in leftSnapshot.Paths)
        {
            if (!rightSnapshot.Paths.Contains(path)) candidates.Add(path);
        }

        foreach (var path in rightSnapshot.Paths)
        {
            if (!leftSnapshot.Paths.Contains(path)) candidates.Add(path);
        }

        var drafts = new ConcurrentBag<Draft>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = MaxParallelReads },
            async (path, token) =>
            {
                var leftEntryTask = ReadEntryAsync(
                    left.Path, path, leftSnapshot.Paths.Contains(path), token);
                var rightEntryTask = ReadEntryAsync(
                    right.Path, path, rightSnapshot.Paths.Contains(path), token);
                await Task.WhenAll(leftEntryTask, rightEntryTask).ConfigureAwait(false);

                var draft = BuildDraft(
                    path,
                    await leftEntryTask.ConfigureAwait(false),
                    await rightEntryTask.ConfigureAwait(false));

                if (draft is not null) drafts.Add(draft);
            }).ConfigureAwait(false);

        var files = DetectExactRenames(drafts)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        return new WorktreeComparison { Left = left, Right = right, Files = files };
    }

    public async Task<WorktreeComparisonContent> GetFileAsync(
        Worktree left,
        Worktree right,
        string leftPath,
        string rightPath,
        CancellationToken ct = default)
    {
        ValidatePair(left, right);
        await EnsureSameRepositoryAsync(left, right, ct).ConfigureAwait(false);
        leftPath = NormaliseOptionalPath(leftPath);
        rightPath = NormaliseOptionalPath(rightPath);

        if (leftPath.Length == 0 && rightPath.Length == 0)
            throw new ArgumentException("A comparison file needs a path on at least one side.");

        if (RepoPaths.EntersGitDirectory(leftPath) || RepoPaths.EntersGitDirectory(rightPath))
            throw new ArgumentException("Comparison paths cannot enter the git directory.");

        // The file endpoint is intentionally on-demand, but it must still be bounded by
        // the same tracked/non-ignored set as the list endpoint. Otherwise a crafted bridge
        // request could use the comparison as a way to read .git-adjacent files that never
        // appeared in the UI. A file deleted between the two calls remains in Git's index
        // listing, so this check does not make normal races fail.
        var leftSnapshotTask = ReadSnapshotAsync(left, ct);
        var rightSnapshotTask = ReadSnapshotAsync(right, ct);
        await Task.WhenAll(leftSnapshotTask, rightSnapshotTask).ConfigureAwait(false);
        var leftPaths = (await leftSnapshotTask.ConfigureAwait(false)).Paths;
        var rightPaths = (await rightSnapshotTask.ConfigureAwait(false)).Paths;
        if ((leftPath.Length > 0 && !leftPaths.Contains(leftPath)) ||
            (rightPath.Length > 0 && !rightPaths.Contains(rightPath)))
        {
            throw new FileNotFoundException("That path is not tracked or non-ignored in the selected worktree.");
        }

        var leftEntryTask = leftPath.Length == 0
            ? Task.FromResult(Entry.Missing)
            : ReadEntryAsync(left.Path, leftPath, isMember: true, ct);
        var rightEntryTask = rightPath.Length == 0
            ? Task.FromResult(Entry.Missing)
            : ReadEntryAsync(right.Path, rightPath, isMember: true, ct);

        await Task.WhenAll(leftEntryTask, rightEntryTask).ConfigureAwait(false);
        var leftEntry = await leftEntryTask.ConfigureAwait(false);
        var rightEntry = await rightEntryTask.ConfigureAwait(false);
        var path = rightPath.Length > 0 ? rightPath : leftPath;

        return new WorktreeComparisonContent
        {
            Path = path,
            OldPath = leftPath.Length > 0 && rightPath.Length > 0 && leftPath != rightPath ? leftPath : null,
            LeftPath = leftPath,
            RightPath = rightPath,
            LeftText = leftEntry.Content.Text,
            RightText = rightEntry.Content.Text,
            LeftExists = leftEntry.Exists,
            RightExists = rightEntry.Exists,
            IsBinary = leftEntry.IsDirectory || rightEntry.IsDirectory ||
                       leftEntry.Content.IsBinary || rightEntry.Content.IsBinary,
            LeftBytes = leftEntry.ByteLength,
            RightBytes = rightEntry.ByteLength,
        };
    }

    private async Task<Snapshot> ReadSnapshotAsync(Worktree worktree, CancellationToken ct)
    {
        var pathsTask = git.RunAsync(
            worktree.Path, ct,
            "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        var statusTask = git.RunAsync(
            worktree.Path, ct,
            "status", "--porcelain=v2", "-z", "--untracked-files=all");

        await Task.WhenAll(pathsTask, statusTask).ConfigureAwait(false);

        var paths = RepoPaths.SplitNul(await pathsTask.ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var state = DiffService.ParseWorkingState(await statusTask.ConfigureAwait(false));
        var dirty = new HashSet<string>(state.Dirty, StringComparer.Ordinal);
        dirty.UnionWith(state.Untracked);
        dirty.UnionWith(state.Unmerged);

        return new Snapshot(paths, dirty);
    }

    private async Task<HashSet<string>> ReadCommittedDifferencesAsync(
        Worktree left, Worktree right, CancellationToken ct)
    {
        // An unborn repository has no pair of trees to ask Git about. Inspecting the whole
        // tracked/untracked union is the only complete answer there, represented by null.
        if (!IsCommitId(left.Head) || !IsCommitId(right.Head))
        {
            var leftPaths = RepoPaths.SplitNul(await git.RunAsync(
                left.Path, ct,
                "ls-files", "--cached", "--others", "--exclude-standard", "-z")
                .ConfigureAwait(false));
            return new HashSet<string>(leftPaths, StringComparer.Ordinal);
        }

        var result = await git.TryRunAsync(
            left.Path, ct,
            "diff", "--name-only", "--no-renames", "-z", left.Head, right.Head, "--")
            .ConfigureAwait(false);

        if (!result.Success)
            throw new GitException(result.CommandLine, result.ExitCode, result.StandardError);

        return RepoPaths.SplitNul(result.StandardOutput).ToHashSet(StringComparer.Ordinal);
    }

    private async Task EnsureSameRepositoryAsync(
        Worktree left, Worktree right, CancellationToken ct)
    {
        var leftTask = git.TryRunAsync(left.Path, ct, "rev-parse", "--git-common-dir");
        var rightTask = git.TryRunAsync(right.Path, ct, "rev-parse", "--git-common-dir");
        await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);

        var leftResult = await leftTask.ConfigureAwait(false);
        var rightResult = await rightTask.ConfigureAwait(false);
        if (!leftResult.Success || !rightResult.Success)
            throw new InvalidOperationException("The two worktrees are not readable repositories.");

        var leftCommon = ResolveGitCommonDir(left.Path, leftResult.Trimmed);
        var rightCommon = ResolveGitCommonDir(right.Path, rightResult.Trimmed);
        if (!string.Equals(leftCommon, rightCommon, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The two worktrees must belong to the same repository.");
    }

    private static string ResolveGitCommonDir(string worktreePath, string reported)
    {
        if (reported.Length == 0) return "";
        try
        {
            return Path.GetFullPath(reported, worktreePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return reported;
        }
    }

    private static bool IsCommitId(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit) && value.Any(character => character != '0');

    private static async Task<Entry> ReadEntryAsync(
        string worktreePath, string repoRelativePath, bool isMember, CancellationToken ct)
    {
        // Membership comes from Git, not from File.Exists. An ignored file can sit at the
        // same path as a tracked file in the other worktree; including it would make the
        // comparison report data Git explicitly excluded from this worktree.
        if (!isMember) return Entry.Missing;

        var absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);

        try
        {
            if (File.Exists(absolute))
            {
                var bytes = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
                return Entry.File(bytes, FileContent.FromBytes(bytes));
            }

            if (Directory.Exists(absolute)) return Entry.Directory;
            return Entry.Missing;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Agents replace files atomically. Vanishing between Exists and ReadAllBytes is
            // a valid snapshot observation, not a bridge failure.
            return Entry.Missing;
        }
    }

    private static Draft? BuildDraft(string path, Entry left, Entry right)
    {
        if (!left.Exists && !right.Exists) return null;

        if (!left.Exists)
        {
            return Draft.OneSided(path, right, ChangeKind.Added, leftPath: "", rightPath: path);
        }

        if (!right.Exists)
        {
            return Draft.OneSided(path, left, ChangeKind.Deleted, leftPath: path, rightPath: "");
        }

        if (left.IsDirectory || right.IsDirectory)
        {
            // A tracked directory is a submodule. Its checked-out contents are a separate
            // repository, so treating the directory as an ordinary recursive file diff
            // would leak ignored data from inside it. The pointer/state change is still
            // represented as a binary/type change in the parent repository.
            return new Draft
            {
                Path = path,
                LeftPath = path,
                RightPath = path,
                Kind = left.IsDirectory == right.IsDirectory ? ChangeKind.Modified : ChangeKind.TypeChanged,
                IsBinary = true,
                LeftBytes = left.ByteLength,
                RightBytes = right.ByteLength,
            };
        }

        if (left.Bytes.AsSpan().SequenceEqual(right.Bytes)) return null;

        var isBinary = left.Content.IsBinary || right.Content.IsBinary;
        var (added, removed) = isBinary
            ? (0, 0)
            : CountLineChanges(left.Content.Text, right.Content.Text);

        return new Draft
        {
            Path = path,
            LeftPath = path,
            RightPath = path,
            Kind = ChangeKind.Modified,
            LinesAdded = added,
            LinesRemoved = removed,
            IsBinary = isBinary,
            LeftBytes = left.ByteLength,
            RightBytes = right.ByteLength,
        };
    }

    private static IReadOnlyList<WorktreeComparisonFile> DetectExactRenames(IEnumerable<Draft> source)
    {
        var drafts = source.ToList();
        var deleted = drafts
            .Where(file => file.Kind is ChangeKind.Deleted && file.Hash is not null)
            .GroupBy(file => file.Hash!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var added = drafts
            .Where(file => file.Kind is ChangeKind.Added && file.Hash is not null)
            .GroupBy(file => file.Hash!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var matched = new HashSet<Draft>();
        var files = new List<WorktreeComparisonFile>(drafts.Count);

        foreach (var (hash, from) in deleted)
        {
            if (!added.TryGetValue(hash, out var to)) continue;

            matched.Add(from);
            matched.Add(to);
            files.Add(new WorktreeComparisonFile
            {
                Path = to.Path,
                OldPath = from.Path,
                LeftPath = from.Path,
                RightPath = to.Path,
                Kind = ChangeKind.Renamed,
                IsBinary = from.IsBinary || to.IsBinary,
                LeftBytes = from.LeftBytes,
                RightBytes = to.RightBytes,
            });
        }

        files.AddRange(drafts.Where(draft => !matched.Contains(draft)).Select(draft => draft.ToFile()));
        return files;
    }

    /// <summary>
    /// Counts inserted and removed logical lines with Myers' shortest-edit algorithm.
    /// Common prefixes and suffixes are removed first, which is the ordinary source-file
    /// case and keeps a one-line edit in a large file close to linear.
    /// </summary>
    internal static (int Added, int Removed) CountLineChanges(string oldText, string newText)
    {
        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var oldEnd = oldLines.Length;
        var newEnd = newLines.Length;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        var oldCount = oldEnd - prefix;
        var newCount = newEnd - prefix;
        if (oldCount == 0) return (newCount, 0);
        if (newCount == 0) return (0, oldCount);

        var max = oldCount + newCount;
        var offset = max + 1;
        var furthest = new int[(max * 2) + 3];
        furthest[offset + 1] = 0;

        for (var distance = 0; distance <= max; distance++)
        {
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var index = offset + diagonal;
                var x = diagonal == -distance ||
                        (diagonal != distance && furthest[index - 1] < furthest[index + 1])
                    ? furthest[index + 1]
                    : furthest[index - 1] + 1;
                var y = x - diagonal;

                while (x < oldCount && y < newCount && oldLines[prefix + x] == newLines[prefix + y])
                {
                    x++;
                    y++;
                }

                furthest[index] = x;
                if (x < oldCount || y < newCount) continue;

                var removed = (distance + oldCount - newCount) / 2;
                return (distance - removed, removed);
            }
        }

        return (newCount, oldCount);
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];

        var normalised = text.Replace("\r\n", "\n");
        var lines = normalised.Split('\n');
        return normalised.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static string NormaliseOptionalPath(string path) =>
        path.Trim().Replace('\\', '/');

    private static void ValidatePair(Worktree left, Worktree right)
    {
        if (!left.IsUsable || !right.IsUsable)
            throw new InvalidOperationException("Both worktrees must have usable working directories.");

        if (string.Equals(
                Path.GetFullPath(left.Path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right.Path).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose two different worktrees to compare.");
        }
    }

    private sealed record Snapshot(HashSet<string> Paths, HashSet<string> Dirty);

    private sealed record Entry
    {
        public static readonly Entry Missing = new();
        public static readonly Entry Directory = new() { Exists = true, IsDirectory = true };

        public bool Exists { get; init; }
        public bool IsDirectory { get; init; }
        public byte[] Bytes { get; init; } = [];
        public FileContent Content { get; init; } = FileContent.Empty;
        public int ByteLength => Bytes.Length;

        public static Entry File(byte[] bytes, FileContent content) => new()
        {
            Exists = true,
            Bytes = bytes,
            Content = content,
        };
    }

    private sealed record Draft
    {
        public required string Path { get; init; }
        public string LeftPath { get; init; } = "";
        public string RightPath { get; init; } = "";
        public required ChangeKind Kind { get; init; }
        public int LinesAdded { get; init; }
        public int LinesRemoved { get; init; }
        public bool IsBinary { get; init; }
        public int LeftBytes { get; init; }
        public int RightBytes { get; init; }
        public string? Hash { get; init; }

        public static Draft OneSided(
            string path, Entry entry, ChangeKind kind, string leftPath, string rightPath)
        {
            var isBinary = entry.IsDirectory || entry.Content.IsBinary;
            var lines = isBinary ? 0 : Lines(entry.Content.Text).Length;

            return new Draft
            {
                Path = path,
                LeftPath = leftPath,
                RightPath = rightPath,
                Kind = kind,
                LinesAdded = kind is ChangeKind.Added ? lines : 0,
                LinesRemoved = kind is ChangeKind.Deleted ? lines : 0,
                IsBinary = isBinary,
                LeftBytes = leftPath.Length > 0 ? entry.ByteLength : 0,
                RightBytes = rightPath.Length > 0 ? entry.ByteLength : 0,
                Hash = entry.IsDirectory ? null : $"{entry.ByteLength}:{Convert.ToHexString(SHA256.HashData(entry.Bytes))}",
            };
        }

        public WorktreeComparisonFile ToFile() => new()
        {
            Path = Path,
            LeftPath = LeftPath,
            RightPath = RightPath,
            Kind = Kind,
            LinesAdded = LinesAdded,
            LinesRemoved = LinesRemoved,
            IsBinary = IsBinary,
            LeftBytes = LeftBytes,
            RightBytes = RightBytes,
        };
    }
}
