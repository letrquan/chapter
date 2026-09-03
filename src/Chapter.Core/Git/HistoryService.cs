using System.Globalization;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>Which part of repository history a search query applies to.</summary>
public enum HistorySearchKind
{
    Message,
    Author,
    Path,
    Content,
}

/// <summary>A commit reachable from the worktree's current <c>HEAD</c>.</summary>
public sealed record CommitLogEntry
{
    public required string Sha { get; init; }

    /// <summary>All parents, in git's order. More than one means this is a merge.</summary>
    public required IReadOnlyList<string> Parents { get; init; }

    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset? AuthoredAt { get; init; }

    public string CommitterName { get; init; } = "";
    public string CommitterEmail { get; init; } = "";
    public DateTimeOffset? CommittedAt { get; init; }

    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";

    /// <summary>Short ref decorations such as <c>HEAD -&gt; main, tag: v1.0</c>.</summary>
    public string Decorations { get; init; } = "";

    public bool IsMerge => Parents.Count > 1;
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>A page of commits, ordered newest first.</summary>
public sealed record CommitLogPage
{
    public required IReadOnlyList<CommitLogEntry> Commits { get; init; }
    /// <summary>The HEAD snapshot every page in this browsing session is read from.</summary>
    public required string Anchor { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required bool HasMore { get; init; }
}

/// <summary>A commit together with the files changed against one of its parents.</summary>
public sealed record CommitDetail
{
    public required CommitLogEntry Commit { get; init; }
    public required string ParentSha { get; init; }
    public required int ParentIndex { get; init; }
    public required IReadOnlyList<ChangedFile> Files { get; init; }
}

/// <summary>The two text sides of one file changed by a commit.</summary>
public sealed record CommitFileDiff
{
    public required string CommitSha { get; init; }
    public required string ParentSha { get; init; }
    public required int ParentIndex { get; init; }
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required FileContent BaseContent { get; init; }
    public required FileContent CommitContent { get; init; }

    public bool IsBinary => BaseContent.IsBinary || CommitContent.IsBinary;
}

/// <summary>A page of commits which touched one repository-relative path.</summary>
public sealed record FileHistoryPage
{
    public required IReadOnlyList<CommitLogEntry> Commits { get; init; }
    public required string Anchor { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required bool HasMore { get; init; }
}

/// <summary>One source line and the commit which last attributed it.</summary>
public sealed record BlameLine
{
    public required int LineNumber { get; init; }
    public required string Sha { get; init; }
    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset? AuthoredAt { get; init; }
    public string Subject { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsBoundary { get; init; }
    public bool IsUncommitted => Sha.Length > 0 && Sha.All(c => c == '0');
}

/// <summary>Blame information for a file, optionally capped for very large files.</summary>
public sealed record BlameResult
{
    public required string Path { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<BlameLine> Lines { get; init; }
    public bool IsTruncated { get; init; }
}

/// <summary>
/// Reads the history visible from one worktree.
///
/// The page is deliberately a git query rather than an in-memory repository-wide list:
/// worktrees can point at different commits, and a large agent repository should not make
/// opening one of them allocate its entire history. The field and record separators are
/// control characters emitted by git itself, so spaces, punctuation and the newlines in a
/// commit body do not change the shape of a row.
/// </summary>
public sealed class HistoryService(GitCli git)
{
    /// <summary>
    /// The log-family format language spells a unit separator as <c>%x1f</c>. Keep this
    /// beside the parser: changing one without the other makes every field appear empty.
    /// </summary>
    internal const char FieldSeparator = '\u001f';

    /// <summary>
    /// NUL + record-separator + NUL terminates a record; a single NUL separates fields.
    /// The framed sequence cannot occur in a commit object, and unlike two adjacent NULs
    /// it stays unambiguous when the final body field is empty.
    /// </summary>
    internal const string RecordSeparator = "\0\u001e\0";

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxBlameLines = 50_000;
    public const int MaxSearchQueryLength = 8_192;

    private const string LogFormat =
        "%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%s%x1f%D%x1f%b%x00%x1e%x00";

    /// <summary>Compatibility overload for callers that do not need an anchor.</summary>
    public Task<CommitLogPage> ListAsync(
        string worktreePath, int offset, int limit, CancellationToken ct) =>
        ListAsync(worktreePath, offset, limit, "", ct);

    /// <summary>
    /// Reads one page, plus one sentinel row to answer <see cref="CommitLogPage.HasMore"/>
    /// without a separate count query.
    /// </summary>
    public async Task<CommitLogPage> ListAsync(
        string worktreePath,
        int offset = 0,
        int limit = DefaultPageSize,
        string anchor = "",
        CancellationToken ct = default)
    {
        anchor ??= "";
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (anchor.Length > 0 && !IsObjectId(anchor))
            throw new ArgumentException("History anchor must be a full git object id.", nameof(anchor));

        if (anchor.Length == 0)
        {
            var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
                .ConfigureAwait(false);

            if (!head.Success || !IsObjectId(head.Trimmed))
            {
                return new CommitLogPage
                {
                    Commits = [],
                    Anchor = "",
                    Offset = offset,
                    Limit = limit,
                    HasMore = false,
                };
            }

            anchor = head.Trimmed;
        }
        else
        {
            await EnsureReachableAsync(worktreePath, anchor, "History anchor", ct)
                .ConfigureAwait(false);
        }

        // Body is last on purpose. Parse() can then take the remainder of a row without
        // having to give multi-line commit text any quoting or escaping rules of its own.
        var result = await git.TryRunAsync(
                worktreePath, ct,
                "log",
                $"--skip={offset}",
                $"--max-count={limit + 1}",
                "--topo-order",
                "--date=iso-strict",
                "--decorate=short",
                $"--format={LogFormat}",
                anchor,
                "--")
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new CommitLogPage
            {
                Commits = [],
                Anchor = anchor,
                Offset = offset,
                Limit = limit,
                HasMore = false,
            };
        }

        var commits = Parse(result.StandardOutput);
        var hasMore = commits.Count > limit;
        if (hasMore) commits = commits.Take(limit).ToArray();

        return new CommitLogPage
        {
            Commits = commits,
            Anchor = anchor,
            Offset = offset,
            Limit = limit,
            HasMore = hasMore,
        };
    }

    /// <summary>
    /// Searches commits reachable from one stable worktree tip.
    ///
    /// Message and author searches are literal and case-insensitive: a person looking for
    /// <c>[parser]</c> should not accidentally hand Git an invalid regular expression.
    /// Content uses Git's pickaxe semantics, so a commit matches when the number of exact
    /// string occurrences changes. Path search builds one escaped glob pathspec, giving a
    /// case-insensitive substring match without allowing query metacharacters to widen it.
    /// </summary>
    public async Task<CommitLogPage> SearchAsync(
        string worktreePath,
        HistorySearchKind kind,
        string query,
        int offset = 0,
        int limit = DefaultPageSize,
        string anchor = "",
        CancellationToken ct = default)
    {
        query = NormalizeSearchQuery(query);
        anchor ??= "";
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (anchor.Length > 0 && !IsObjectId(anchor))
            throw new ArgumentException("History anchor must be a full git object id.", nameof(anchor));

        if (anchor.Length == 0)
        {
            var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
                .ConfigureAwait(false);

            if (!head.Success || !IsObjectId(head.Trimmed))
            {
                return new CommitLogPage
                {
                    Commits = [],
                    Anchor = "",
                    Offset = offset,
                    Limit = limit,
                    HasMore = false,
                };
            }

            anchor = head.Trimmed;
        }
        else
        {
            await EnsureReachableAsync(worktreePath, anchor, "History anchor", ct)
                .ConfigureAwait(false);
        }

        var args = new List<string>
        {
            "log",
            $"--skip={offset}",
            $"--max-count={limit + 1}",
            "--topo-order",
            "--date=iso-strict",
            "--decorate=short",
            $"--format={LogFormat}",
        };

        switch (kind)
        {
            case HistorySearchKind.Message:
                args.Add("--regexp-ignore-case");
                args.Add("--fixed-strings");
                args.Add($"--grep={query}");
                break;

            case HistorySearchKind.Author:
                args.Add("--regexp-ignore-case");
                args.Add("--fixed-strings");
                args.Add($"--author={query}");
                break;

            case HistorySearchKind.Content:
                // -S is intentionally one argument. A query beginning with '-' remains
                // pickaxe text rather than becoming another command-line option.
                args.Add($"-S{query}");
                break;

            case HistorySearchKind.Path:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown history search kind.");
        }

        args.Add(anchor);
        args.Add("--");
        if (kind is HistorySearchKind.Path) args.Add(BuildPathSearchPathspec(query));

        var result = await git.TryRunAsync(worktreePath, ct, [.. args]).ConfigureAwait(false);
        if (!result.Success)
        {
            return new CommitLogPage
            {
                Commits = [],
                Anchor = anchor,
                Offset = offset,
                Limit = limit,
                HasMore = false,
            };
        }

        var commits = Parse(result.StandardOutput);
        var hasMore = commits.Count > limit;
        if (hasMore) commits = commits.Take(limit).ToArray();

        return new CommitLogPage
        {
            Commits = commits,
            Anchor = anchor,
            Offset = offset,
            Limit = limit,
            HasMore = hasMore,
        };
    }

    /// <summary>Lists commits which changed one path, following renames where possible.</summary>
    public async Task<FileHistoryPage> ListFileAsync(
        string worktreePath,
        string repoRelativePath,
        int offset = 0,
        int limit = DefaultPageSize,
        string anchor = "",
        CancellationToken ct = default)
    {
        anchor ??= "";
        repoRelativePath = NormalizeRelativePath(repoRelativePath);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (anchor.Length > 0 && !IsObjectId(anchor))
            throw new ArgumentException("History anchor must be a full git object id.", nameof(anchor));

        if (anchor.Length == 0)
        {
            var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
                .ConfigureAwait(false);
            if (!head.Success || !IsObjectId(head.Trimmed))
            {
                return new FileHistoryPage
                {
                    Commits = [],
                    Anchor = "",
                    Offset = offset,
                    Limit = limit,
                    HasMore = false,
                };
            }

            anchor = head.Trimmed;
        }
        else
        {
            await EnsureReachableAsync(worktreePath, anchor, "History anchor", ct)
                .ConfigureAwait(false);
        }

        // There cannot be a useful page once the requested row plus the sentinel would
        // overflow Git's integer --max-count parser. Returning an empty terminal page is
        // both quicker and more honest than launching a command Git will reject.
        if ((long)offset > int.MaxValue - limit - 1)
        {
            return new FileHistoryPage
            {
                Commits = [],
                Anchor = anchor,
                Offset = offset,
                Limit = limit,
                HasMore = false,
            };
        }

        // `--skip` is not safe with `--follow`: Git applies the skip before it has
        // traversed a rename, so a page can incorrectly become empty at that boundary.
        // Read through the requested row and page in-process instead.
        var maxCount = offset + limit + 1;
        var result = await git.TryRunAsync(
                worktreePath, ct,
                "--literal-pathspecs",
                "log",
                "--follow",
                "--topo-order",
                $"--max-count={maxCount}",
                "--date=iso-strict",
                "--decorate=short",
                $"--format={LogFormat}",
                anchor,
                "--",
                repoRelativePath)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new FileHistoryPage
            {
                Commits = [],
                Anchor = anchor,
                Offset = offset,
                Limit = limit,
                HasMore = false,
            };
        }

        var all = Parse(result.StandardOutput);
        var hasMore = all.Count > (long)offset + limit;
        var commits = all.Skip(offset).Take(limit).ToArray();

        return new FileHistoryPage
        {
            Commits = commits,
            Anchor = anchor,
            Offset = offset,
            Limit = limit,
            HasMore = hasMore,
        };
    }

    /// <summary>
    /// Reads blame's line-oriented porcelain format. Unlike the human table, porcelain
    /// repeats the metadata keys needed for a gutter marker and leaves source text after a
    /// tab, so paths and lines containing spaces remain lossless.
    /// </summary>
    public async Task<BlameResult> BlameAsync(
        string worktreePath,
        string repoRelativePath,
        string revision = "",
        CancellationToken ct = default)
    {
        revision ??= "";
        repoRelativePath = NormalizeRelativePath(repoRelativePath);

        // Pathspec wildcards are useful at a shell prompt but surprising for a file-level
        // view: a name such as `src/[old].cs` must not pull attribution from sibling files.
        // This is a global git option, so it has to precede the subcommand.
        var args = new List<string> { "--literal-pathspecs", "blame", "--line-porcelain" };
        if (revision.Length > 0)
        {
            if (!IsObjectId(revision))
                throw new ArgumentException("Blame revision must be a full git object id.", nameof(revision));

            await EnsureReachableAsync(worktreePath, revision, "Blame revision", ct)
                .ConfigureAwait(false);

            args.Add(revision);
        }
        args.Add("--");
        args.Add(repoRelativePath);

        var result = await git.TryRunAsync(worktreePath, ct, [.. args]).ConfigureAwait(false);
        if (!result.Success)
        {
            // Git cannot blame a path which has never existed in HEAD (an untracked file,
            // or a staged addition in an unborn repository). Those lines are still useful
            // to the editor: attribute them explicitly as uncommitted instead of turning a
            // perfectly normal new file into an error toast. A path which does exist in
            // HEAD keeps git's failure, so binary files and real repository errors are not
            // misrepresented as user edits.
            if (revision.Length == 0 &&
                !await ExistsAtHeadAsync(worktreePath, repoRelativePath, ct).ConfigureAwait(false))
            {
                var absolute = RepoPaths.Resolve(worktreePath, repoRelativePath);
                if (File.Exists(absolute))
                {
                    var content = FileContent.FromBytes(await File.ReadAllBytesAsync(absolute, ct)
                        .ConfigureAwait(false));
                    if (!content.IsBinary)
                    {
                        var uncommittedLines = BuildUncommittedLines(content.Text);
                        var uncommittedTruncated = uncommittedLines.Count > MaxBlameLines;
                        if (uncommittedTruncated)
                            uncommittedLines = uncommittedLines.Take(MaxBlameLines).ToArray();

                        return new BlameResult
                        {
                            Path = repoRelativePath,
                            Revision = revision,
                            Lines = uncommittedLines,
                            IsTruncated = uncommittedTruncated,
                        };
                    }
                }
            }

            throw new GitException(result.CommandLine, result.ExitCode, result.StandardError);
        }

        var lines = ParseBlame(result.StandardOutput);
        var truncated = lines.Count > MaxBlameLines;
        if (truncated) lines = lines.Take(MaxBlameLines).ToArray();

        return new BlameResult
        {
            Path = repoRelativePath,
            Revision = revision,
            Lines = lines,
            IsTruncated = truncated,
        };
    }

    private async Task<bool> ExistsAtHeadAsync(
        string worktreePath,
        string repoRelativePath,
        CancellationToken ct)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct,
                "--literal-pathspecs",
                "cat-file",
                "-e",
                $"HEAD:{repoRelativePath}")
            .ConfigureAwait(false);
        return result.Success;
    }

    private static IReadOnlyList<BlameLine> BuildUncommittedLines(string text)
    {
        if (text.Length == 0) return [];

        var raw = text.Split('\n').ToList();
        if (text.EndsWith('\n')) raw.RemoveAt(raw.Count - 1);

        const string zeroSha = "0000000000000000000000000000000000000000";
        return raw.Select((line, index) => new BlameLine
        {
            LineNumber = index + 1,
            Sha = zeroSha,
            AuthorName = "Not Committed Yet",
            AuthorEmail = "not.committed.yet",
            Subject = "Uncommitted changes",
            Text = line.EndsWith('\r') ? line[..^1] : line,
        }).ToArray();
    }

    /// <summary>
    /// Reads one commit and the files changed against the selected parent. A commit shown
    /// by the history list is always a full object id, but the checks here also protect the
    /// bridge from being used to inspect an arbitrary revision expression.
    /// </summary>
    public async Task<CommitDetail> GetDetailAsync(
        string worktreePath,
        string sha,
        int parentIndex = 0,
        CancellationToken ct = default)
    {
        var commit = await ValidateCommitAsync(worktreePath, sha, ct).ConfigureAwait(false);
        var parent = await ResolveParentAsync(worktreePath, commit, parentIndex, ct)
            .ConfigureAwait(false);
        var files = await ReadCommitFilesAsync(worktreePath, parent, commit.Sha, ct)
            .ConfigureAwait(false);

        return new CommitDetail
        {
            Commit = commit,
            ParentSha = parent,
            ParentIndex = parentIndex,
            Files = files,
        };
    }

    /// <summary>
    /// Validates and reads a commit id before a history mutation uses it.
    ///
    /// The bridge receives a hash from a page that may have been open for a while. A full
    /// id is still not enough on its own: an object from another branch or an unrelated
    /// repository can be supplied just as easily as one from this timeline. Keeping the
    /// reachability check here makes cherry-pick and revert use the same boundary as the
    /// detail view, rather than trusting the window's copy of the list.
    /// </summary>
    public Task<CommitLogEntry> ValidateCommitAsync(
        string worktreePath, string sha, CancellationToken ct = default) =>
        ReadCommitAsync(worktreePath, sha, ct);

    /// <summary>Alias for callers that need the validated metadata as well as the check.</summary>
    public Task<CommitLogEntry> GetCommitAsync(
        string worktreePath, string sha, CancellationToken ct = default) =>
        ValidateCommitAsync(worktreePath, sha, ct);

    /// <summary>
    /// Checks the zero-based parent choice used by the bridge and returns Git's one-based
    /// <c>-m</c> value. Non-merge commits have one implicit choice (the default), while a
    /// root commit has no parent and therefore also keeps the default command form.
    /// </summary>
    public static int? MergeMainline(CommitLogEntry commit, int parentIndex)
    {
        var parentCount = commit.Parents.Count;
        if (parentIndex < 0 || parentIndex >= Math.Max(1, parentCount))
            throw new ArgumentOutOfRangeException(nameof(parentIndex),
                "That parent does not exist on the commit.");

        return parentCount > 1 ? parentIndex + 1 : null;
    }

    /// <summary>Reads one changed file from a commit/parent comparison.</summary>
    public async Task<CommitFileDiff> GetFileDiffAsync(
        string worktreePath,
        string sha,
        string repoRelativePath,
        int parentIndex = 0,
        CancellationToken ct = default)
    {
        repoRelativePath = NormalizeRelativePath(repoRelativePath);

        var detail = await GetDetailAsync(worktreePath, sha, parentIndex, ct).ConfigureAwait(false);
        var file = detail.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, repoRelativePath, StringComparison.Ordinal));

        if (file is null)
            throw new InvalidOperationException($"'{repoRelativePath}' was not changed by commit {detail.Commit.ShortSha}.");

        var baseContent = file.HasBaseSide
            ? await ReadContentAtAsync(worktreePath, detail.ParentSha, file.BasePath, ct).ConfigureAwait(false)
            : FileContent.Empty;
        var commitContent = file.HasWorkingSide
            ? await ReadContentAtAsync(worktreePath, detail.Commit.Sha, file.Path, ct).ConfigureAwait(false)
            : FileContent.Empty;

        return new CommitFileDiff
        {
            CommitSha = detail.Commit.Sha,
            ParentSha = detail.ParentSha,
            ParentIndex = detail.ParentIndex,
            Path = file.Path,
            OldPath = file.OldPath,
            BaseContent = baseContent,
            CommitContent = commitContent,
        };
    }

    private async Task<CommitLogEntry> ReadCommitAsync(
        string worktreePath, string sha, CancellationToken ct)
    {
        if (!IsObjectId(sha))
            throw new ArgumentException("Commit id must be a full git object id.", nameof(sha));

        // `git log <sha>` would happily start at an unrelated object supplied by a caller.
        // History is scoped to the worktree's current tip, so reject commits outside that
        // ancestry before reading their metadata or file contents.
        var reachable = await git.TryRunAsync(
                worktreePath, ct, "merge-base", "--is-ancestor", sha, "HEAD")
            .ConfigureAwait(false);
        if (!reachable.Success)
            throw new InvalidOperationException($"Commit '{sha}' is not reachable from this worktree.");

        var result = await git.TryRunAsync(
                worktreePath, ct,
                "log",
                "--max-count=1",
                "--topo-order",
                "--date=iso-strict",
                "--decorate=short",
                $"--format={LogFormat}",
                sha,
                "--")
            .ConfigureAwait(false);

        var commit = result.Success ? Parse(result.StandardOutput).FirstOrDefault() : null;
        if (commit is null || !string.Equals(commit.Sha, sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Commit '{sha}' is not reachable from this worktree.");
        }

        return commit;
    }

    private async Task EnsureReachableAsync(
        string worktreePath,
        string revision,
        string description,
        CancellationToken ct)
    {
        var reachable = await git.TryRunAsync(
                worktreePath, ct, "merge-base", "--is-ancestor", revision, "HEAD")
            .ConfigureAwait(false);
        if (!reachable.Success)
            throw new InvalidOperationException($"{description} '{revision}' is not reachable from this worktree.");
    }

    private async Task<string> ResolveParentAsync(
        string worktreePath, CommitLogEntry commit, int parentIndex, CancellationToken ct)
    {
        if (parentIndex < 0 || parentIndex >= Math.Max(1, commit.Parents.Count))
            throw new ArgumentOutOfRangeException(nameof(parentIndex), "That parent does not exist on the commit.");

        if (commit.Parents.Count > 0) return commit.Parents[parentIndex];

        // A root commit is compared with git's empty tree, not with an invalid HEAD~1.
        return await EmptyTreeShaAsync(worktreePath, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChangedFile>> ReadCommitFilesAsync(
        string worktreePath, string parentSha, string commitSha, CancellationToken ct)
    {
        var names = await git.TryRunAsync(
                worktreePath, ct, "diff", "--name-status", "-M", "-z", parentSha, commitSha, "--")
            .ConfigureAwait(false);
        var numbers = await git.TryRunAsync(
                worktreePath, ct, "diff", "--numstat", "-M", "-z", parentSha, commitSha, "--")
            .ConfigureAwait(false);

        if (!names.Success)
            throw new GitException(names.CommandLine, names.ExitCode, names.StandardError);
        if (!numbers.Success)
            throw new GitException(numbers.CommandLine, numbers.ExitCode, numbers.StandardError);

        var stats = DiffService.ParseNumstat(numbers.StandardOutput);
        return DiffService.ParseNameStatus(names.StandardOutput)
            .Select(file => stats.TryGetValue(file.Path, out var stat)
                ? file with { LinesAdded = stat.Added, LinesRemoved = stat.Removed, IsBinary = stat.IsBinary }
                : file)
            .ToArray();
    }

    private async Task<FileContent> ReadContentAtAsync(
        string worktreePath, string revision, string repoRelativePath, CancellationToken ct)
    {
        repoRelativePath = NormalizeRelativePath(repoRelativePath);
        var result = await git.RunBytesAsync(worktreePath, ct, "show", $"{revision}:{repoRelativePath}")
            .ConfigureAwait(false);

        // A missing side is expected for additions/deletions. The path came from git's
        // own diff, so another failure is still represented as an empty side for parity
        // with the normal diff reader.
        return result.Success ? FileContent.FromBytes(result.StandardOutput) : FileContent.Empty;
    }

    private async Task<string> EmptyTreeShaAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "hash-object", "-t", "tree", "--stdin")
            .ConfigureAwait(false);
        return result.Success && IsObjectId(result.Trimmed)
            ? result.Trimmed
            : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    }

    internal static void ValidateRelativePath(string path) => _ = NormalizeRelativePath(path);

    /// <summary>
    /// Validates and converts a path from the bridge to Git's repository-relative form.
    ///
    /// Git accepts a surprising number of path-looking strings as revision expressions or
    /// absolute paths. Keeping this check beside the history commands means a caller cannot
    /// turn a read into an arbitrary file probe, and all commands agree on slash direction.
    /// </summary>
    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || RepoPaths.EntersGitDirectory(path))
            throw new ArgumentException("Commit file path is not a repository-relative file.", nameof(path));

        var platform = RepoPaths.ToPlatform(path);
        if (Path.IsPathRooted(platform))
            throw new ArgumentException("Commit file path is not a repository-relative file.", nameof(path));

        var normalized = RepoPaths.ToGit(path);
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Commit file path is not a repository-relative file.", nameof(path));

        // A drive-qualified path may not be recognised as rooted on every platform. Git
        // pathspecs also treat a colon in the first component specially, so reject it here.
        if (segments[0].Contains(':'))
            throw new ArgumentException("Commit file path is not a repository-relative file.", nameof(path));

        return normalized;
    }

    internal static string BuildPathSearchPathspec(string query)
    {
        query = NormalizeSearchQuery(query).Replace('\\', '/');
        var escaped = new StringBuilder(query.Length + 16);

        foreach (var value in query)
        {
            // These are Git glob metacharacters. Escaping them makes path search a literal
            // substring search even for real names such as `src/[old]*.cs`.
            if (value is '\\' or '*' or '?' or '[' or ']') escaped.Append('\\');
            escaped.Append(value);
        }

        // In Git's glob pathspec, **/ matches zero or more directories. That matters for a
        // top-level file: a plain */ prefix would silently exclude it.
        return $":(top,glob,icase)**/*{escaped}*";
    }

    private static string NormalizeSearchQuery(string? query)
    {
        query ??= "";
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("History search query cannot be empty.", nameof(query));
        if (query.Length > MaxSearchQueryLength)
            throw new ArgumentException(
                $"History search query cannot exceed {MaxSearchQueryLength} characters.", nameof(query));
        if (query.Contains('\0') || query.Contains('\r') || query.Contains('\n'))
            throw new ArgumentException("History search query must be one line.", nameof(query));

        return query;
    }

    internal static IReadOnlyList<BlameLine> ParseBlame(string output)
    {
        var lines = new List<BlameLine>();
        var current = new BlameMetadata();
        var pendingLine = 0;
        var pendingCount = 0;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (TryParseBlameHeader(line, out var sha, out var finalLine, out var count, out var boundary))
            {
                current = new BlameMetadata
                {
                    Sha = sha,
                    IsBoundary = boundary,
                };
                pendingLine = finalLine;
                pendingCount = Math.Max(1, count);
                continue;
            }

            if (line.Length > 0 && line[0] == '\t' && pendingCount > 0)
            {
                lines.Add(new BlameLine
                {
                    LineNumber = pendingLine,
                    Sha = current.Sha,
                    AuthorName = current.AuthorName,
                    AuthorEmail = current.AuthorEmail,
                    AuthoredAt = current.AuthoredAt,
                    Subject = current.Subject,
                    Text = line[1..],
                    IsBoundary = current.IsBoundary,
                });
                pendingLine++;
                pendingCount--;
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal)) current.AuthorName = line[7..];
            else if (line.StartsWith("author-mail ", StringComparison.Ordinal)) current.AuthorEmail = UnwrapMail(line[12..]);
            else if (line.StartsWith("author-time ", StringComparison.Ordinal) &&
                     long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                current.AuthorSeconds = seconds;
                current.RefreshAuthoredAt();
            }
            else if (line.StartsWith("author-tz ", StringComparison.Ordinal))
            {
                current.AuthorOffset = ParseBlameOffset(line[10..]);
                current.RefreshAuthoredAt();
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal)) current.Subject = line[8..];
        }

        return lines;
    }

    private sealed class BlameMetadata
    {
        public string Sha { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AuthorEmail { get; set; } = "";
        public DateTimeOffset? AuthoredAt { get; set; }
        public long? AuthorSeconds { get; set; }
        public TimeSpan? AuthorOffset { get; set; }
        public string Subject { get; set; } = "";
        public bool IsBoundary { get; set; }

        public void RefreshAuthoredAt()
        {
            if (!AuthorSeconds.HasValue) return;

            try
            {
                var utc = DateTimeOffset.FromUnixTimeSeconds(AuthorSeconds.Value);
                AuthoredAt = AuthorOffset.HasValue ? utc.ToOffset(AuthorOffset.Value) : utc;
            }
            catch (ArgumentException)
            {
                AuthoredAt = null;
            }
        }
    }

    private static bool TryParseBlameHeader(
        string line,
        out string sha,
        out int finalLine,
        out int count,
        out bool boundary)
    {
        sha = "";
        finalLine = 0;
        count = 0;
        boundary = false;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var candidate = parts[0];
        if (candidate.Length > 0 && candidate[0] == '^')
        {
            boundary = true;
            candidate = candidate[1..];
        }

        if (!IsObjectId(candidate) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out finalLine))
            return false;
        if (parts.Length > 3 &&
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return false;
        sha = candidate;
        return true;
    }

    private static string UnwrapMail(string value) =>
        value.Length >= 2 && value[0] == '<' && value[^1] == '>' ? value[1..^1] : value;

    private static TimeSpan? ParseBlameOffset(string value)
    {
        if (value.Length != 5 || (value[0] != '+' && value[0] != '-') ||
            !int.TryParse(value[1..3], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(value[3..5], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes > 59 || hours > 14 || (hours == 14 && minutes > 0))
            return null;

        var offset = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? -offset : offset;
    }

    /// <summary>Parses the format emitted by <see cref="ListAsync"/>.</summary>
    internal static IReadOnlyList<CommitLogEntry> Parse(string output)
    {
        var commits = new List<CommitLogEntry>();

        // Git emits the record separator even for an empty body. Splitting with no
        // RemoveEmptyEntries keeps malformed rows visible to the fixed-field check while
        // the final empty item is harmlessly ignored.
        foreach (var raw in output.Split(RecordSeparator, StringSplitOptions.None))
        {
            // Pretty-format adds a newline between records even though the format already
            // supplied its own terminator. Strip that framing from both ends, not from the
            // middle where it belongs to the commit body.
            var record = raw.Trim('\r', '\n');
            if (record.Length == 0) continue;

            // There are ten fixed fields before the body. Split only those separators;
            // the remainder is the body, which may contain arbitrary newlines.
            var fields = SplitFirst(record, FieldSeparator, 10);
            if (fields.Count < 11) continue;

            var sha = fields[0];
            if (sha.Length == 0) continue;

            var parents = fields[1]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            commits.Add(new CommitLogEntry
            {
                Sha = sha,
                Parents = parents,
                AuthorName = fields[2],
                AuthorEmail = fields[3],
                AuthoredAt = ParseDate(fields[4]),
                CommitterName = fields[5],
                CommitterEmail = fields[6],
                CommittedAt = ParseDate(fields[7]),
                Subject = fields[8],
                Decorations = fields[9],
                Body = fields[10].TrimEnd('\r', '\n'),
            });
        }

        return commits;
    }

    /// <summary>
    /// Splits at most <paramref name="separators"/> times. The final item is the untouched
    /// remainder, which is what lets a commit body contain arbitrary newlines.
    /// </summary>
    private static List<string> SplitFirst(string text, char separator, int separators)
    {
        var fields = new List<string>(separators + 1);
        var start = 0;

        for (var i = 0; i < separators; i++)
        {
            var at = text.IndexOf(separator, start);
            if (at < 0)
            {
                fields.Add(text[start..]);
                return fields;
            }

            fields.Add(text[start..at]);
            start = at + 1;
        }

        fields.Add(text[start..]);
        return fields;
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when
            : null;

    /// <summary>Whether a value is a complete SHA-1 or SHA-256 object id.</summary>
    internal static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(IsAsciiHex);

    private static bool IsAsciiHex(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
