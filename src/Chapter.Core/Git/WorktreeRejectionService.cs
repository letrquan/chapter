using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>One path the rejection preview says will be discarded.</summary>
public sealed record WorktreeRejectionPath
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required ChangeKind Kind { get; init; }
}

/// <summary>
/// A read-only snapshot of what rejecting a linked worktree would do.
///
/// Ignored paths are reported separately because they are easy to miss and permanently
/// deleted by rejection. Execution names only paths in this snapshot rather than running
/// an unscoped clean that could sweep up something an agent creates later.
/// </summary>
public sealed record WorktreeRejectionPreview
{
    public string SourceWorktreePath { get; init; } = "";
    public string TargetWorktreePath { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string SourceHead { get; init; } = "";
    public string BaseBranch { get; init; } = "";
    public string BaseHead { get; init; } = "";
    public IReadOnlyList<WorktreeRejectionPath> Paths { get; init; } = [];
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
    public int CommitCount { get; init; }
    public string SnapshotFingerprint { get; init; } = "";
    internal string TrackedFingerprint { get; init; } = "";
    public GitFailure Failure { get; init; } = GitFailure.None;
    public string Detail { get; init; } = "";

    public bool Success => Failure is GitFailure.None;
    public bool HasChanges => CommitCount > 0 || Paths.Count > 0 || IgnoredPaths.Count > 0;

    public string Message
    {
        get
        {
            if (!Success) return Detail;

            if (!HasChanges) return "There is no work to reject";

            var branch = SourceBranch.Length > 0 ? SourceBranch : "the worktree";
            var baseName = BaseBranch.Length > 0 ? BaseBranch : "the base";
            var ignored = IgnoredPaths.Count == 0
                ? ""
                : $" Ignored content ({IgnoredPaths.Count} path(s)) will also be discarded.";
            return $"Reject {branch} and reset it to {baseName}.{ignored}";
        }
    }
}

/// <summary>
/// The result of rejecting one linked worktree. Cleaning and resetting are separate Git
/// mutations because cleanup is intentionally run before reset, while the source branch's
/// ignore rules still identify ignored build output and credentials for the preview.
/// </summary>
public sealed record WorktreeRejection
{
    public required WorktreeRejectionPreview Preview { get; init; }
    public required GitMutation Cleanup { get; init; }
    public required GitMutation Reset { get; init; }
    public bool Verified { get; init; }

    public string SourceWorktreePath => Preview.SourceWorktreePath;
    public string TargetWorktreePath => Preview.TargetWorktreePath;
    public string SourceBranch => Preview.SourceBranch;

    public bool Success => Cleanup.Success && Reset.Success && Verified;

    public string Message
    {
        get
        {
            if (!Cleanup.Success) return Cleanup.Message;
            if (!Reset.Success) return Reset.Message;
            if (!Verified)
            {
                return $"{Reset.Message} The worktree changed while it was being reset; " +
                       "remaining work was left in place.";
            }

            return Reset.Message;
        }
    }
}

/// <summary>
/// Previews and executes rejection of a linked agent worktree.
///
/// The operation is intentionally narrower than deleting a worktree: the directory and its
/// branch remain available for the next agent, while the branch is moved back to the merge
/// base with the repository's default branch. Ordinary untracked and ignored content is
/// deleted only after it appeared in the preview and confirmation; paths created later are
/// not named by cleanup and make the reset fail closed.
/// </summary>
public sealed class WorktreeRejectionService(
    GitCli git,
    GitWriter writer,
    UndoService undo,
    WorktreeService worktrees)
{
    private readonly BaseBranchResolver _bases = new(git);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<WorktreeRejectionPreview> PreviewAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        CancellationToken ct = default) =>
        ReadPreviewAsync(anyPathInRepo, sourceWorktreePath, ct);

    /// <summary>
    /// Rejects the source using the caller's captured snapshot when supplied. Empty expected
    /// values are accepted for non-UI callers, but the desktop surface always sends all three
    /// so a stale confirmation cannot discard a newer agent change.
    /// </summary>
    public Task<WorktreeRejection> RejectAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        string expectedSourceHead = "",
        string expectedBaseHead = "",
        string expectedSnapshotFingerprint = "",
        CancellationToken ct = default)
    {
        var key = anyPathInRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        return RunSerializedAsync(
            gate,
            anyPathInRepo,
            sourceWorktreePath,
            expectedSourceHead,
            expectedBaseHead,
            expectedSnapshotFingerprint,
            ct);
    }

    /// <summary>Named alias for hosts that use the agent-oriented verb.</summary>
    public Task<WorktreeRejection> RejectWorktreeAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        string expectedSourceHead = "",
        string expectedBaseHead = "",
        string expectedSnapshotFingerprint = "",
        CancellationToken ct = default) =>
        RejectAsync(anyPathInRepo, sourceWorktreePath, expectedSourceHead, expectedBaseHead,
            expectedSnapshotFingerprint, ct);

    private async Task<WorktreeRejection> RunSerializedAsync(
        SemaphoreSlim gate,
        string anyPathInRepo,
        string sourceWorktreePath,
        string expectedSourceHead,
        string expectedBaseHead,
        string expectedSnapshotFingerprint,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RejectCoreAsync(
                anyPathInRepo,
                sourceWorktreePath,
                expectedSourceHead,
                expectedBaseHead,
                expectedSnapshotFingerprint,
                ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorktreeRejection> RejectCoreAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        string expectedSourceHead,
        string expectedBaseHead,
        string expectedSnapshotFingerprint,
        CancellationToken ct)
    {
        var preview = await ReadPreviewAsync(anyPathInRepo, sourceWorktreePath, ct)
            .ConfigureAwait(false);

        if (!preview.Success)
            return Refused(preview);

        if (expectedSourceHead.Length > 0 && !string.Equals(
                expectedSourceHead, preview.SourceHead, StringComparison.OrdinalIgnoreCase))
            return Refused(preview with
            {
                Failure = GitFailure.WouldLoseChanges,
                Detail = "Could not reject the worktree: it changed since the preview was opened; refresh and try again",
            });

        if (expectedBaseHead.Length > 0 && !string.Equals(
                expectedBaseHead, preview.BaseHead, StringComparison.OrdinalIgnoreCase))
            return Refused(preview with
            {
                Failure = GitFailure.WouldLoseChanges,
                Detail = "Could not reject the worktree: its base changed since the preview was opened; refresh and try again",
            });

        if (expectedSnapshotFingerprint.Length > 0 && !string.Equals(
                expectedSnapshotFingerprint, preview.SnapshotFingerprint, StringComparison.Ordinal))
            return Refused(preview with
            {
                Failure = GitFailure.WouldLoseChanges,
                Detail = "Could not reject the worktree: its files changed since the preview was opened; refresh and try again",
            });

        if (!preview.HasChanges)
        {
            var noOp = NoOp(preview.SourceWorktreePath, $"reject {preview.SourceBranch}", preview.Message);
            return new WorktreeRejection
            {
                Preview = preview,
                Cleanup = noOp,
                Reset = noOp,
                Verified = true,
            };
        }

        // Clean while the source branch's ignore rules still exist. That is the only point
        // at which Git can distinguish ordinary untracked work from the ignored content the
        // confirmation names separately.
        var cleanup = await CleanPreviewedPathsAsync(preview, ct).ConfigureAwait(false);

        if (!cleanup.Success)
        {
            return new WorktreeRejection
            {
                Preview = preview,
                Cleanup = cleanup,
                Reset = NoOp(preview.SourceWorktreePath, $"reject {preview.SourceBranch}",
                    "The branch was not reset because cleanup did not finish"),
                Verified = false,
            };
        }

        // `git clean` is restricted to the previewed literal paths. Re-read before reset so
        // a new file or edit made while cleanup ran is left intact rather than being swept
        // up by reset --hard without ever appearing in the confirmation.
        var afterCleanup = await ReadSnapshotAsync(preview.SourceWorktreePath, ct).ConfigureAwait(false);
        var headAfterCleanup = await ReadHeadAsync(preview.SourceWorktreePath, ct).ConfigureAwait(false);
        if (!afterCleanup.Success || afterCleanup.Untracked.Count > 0 ||
            afterCleanup.Ignored.Count > 0 ||
            !string.Equals(headAfterCleanup, preview.SourceHead, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(afterCleanup.TrackedFingerprint, preview.TrackedFingerprint, StringComparison.Ordinal))
        {
            return new WorktreeRejection
            {
                Preview = preview,
                Cleanup = cleanup,
                Reset = new GitMutation
                {
                    Operation = $"reject {preview.SourceBranch}",
                    WorktreePath = preview.SourceWorktreePath,
                    CommandLine = "",
                    ExitCode = -1,
                    Failure = GitFailure.WouldLoseChanges,
                    Detail = "Could not reset the branch: the worktree changed while cleanup was running; refresh and try again",
                    Attempts = 0,
                },
                Verified = false,
            };
        }

        var previousHead = preview.SourceHead;
        var reset = await writer.RunAsync(
            preview.SourceWorktreePath,
            $"reject {preview.SourceBranch}",
            WriteKind.StartsOperation,
            ct,
            ["reset", "--hard", preview.BaseHead])
            .ConfigureAwait(false);

        var verified = reset.Success && await VerifyAsync(preview, ct).ConfigureAwait(false);

        // Record only after the post-reset check. If an agent committed while reset was
        // running, the check fails and the undo stack must not acquire an inverse that could
        // later rewind that newer commit. A leftover file is different: HEAD is still the
        // rejected base, so restoring the committed tip remains safe even though the overall
        // rejection is reported as incomplete.
        if (reset.Success)
        {
            var headAfterReset = await ReadHeadAsync(preview.SourceWorktreePath, ct)
                .ConfigureAwait(false);
            if (string.Equals(headAfterReset, preview.BaseHead, StringComparison.OrdinalIgnoreCase))
            {
                // A reset is a history rewrite. Its inverse restores committed content, while
                // the warning makes the permanent half — uncommitted and untracked bytes —
                // explicit in the undo dialog.
                await undo.RecordHistoryRewriteAsync(
                    preview.SourceWorktreePath,
                    previousHead,
                    "reject",
                    preview.SourceBranch,
                    ct,
                    isDestructive: true,
                    warning: "This restores the rejected branch's committed files and tip. " +
                             "Uncommitted, untracked, and ignored files discarded by rejection cannot be recovered.",
                    expectedNewHead: headAfterReset)
                    .ConfigureAwait(false);
            }
        }

        if (verified) return new WorktreeRejection
        {
            Preview = preview,
            Cleanup = cleanup,
            Reset = reset,
            Verified = true,
        };

        return new WorktreeRejection
        {
            Preview = preview,
            Cleanup = cleanup,
            Reset = reset with
            {
                ExitCode = 1,
                Failure = GitFailure.WouldLoseChanges,
                Detail = "The branch was reset, but the worktree changed during rejection; remaining work was left in place",
            },
            Verified = false,
        };
    }

    private async Task<WorktreeRejectionPreview> ReadPreviewAsync(
        string anyPathInRepo,
        string sourceWorktreePath,
        CancellationToken ct)
    {
        var source = sourceWorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (source.Length == 0)
            return RefusedPreview("a source worktree is required", GitFailure.NotFound);

        IReadOnlyList<Worktree> listed;
        try
        {
            listed = await worktrees.ListAsync(anyPathInRepo, ct).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return RefusedPreview(
                ex.StandardError.Trim().Length > 0 ? ex.StandardError.Trim() : "the repository could not be read",
                GitFailure.NotFound);
        }

        var target = listed.FirstOrDefault(worktree => worktree.IsMain);
        var sourceEntry = listed.FirstOrDefault(worktree =>
            string.Equals(
                worktree.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                source,
                StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return RefusedPreview("this repository has no main worktree", GitFailure.NotFound);

        var basePreview = new WorktreeRejectionPreview
        {
            SourceWorktreePath = sourceEntry?.Path ?? source,
            TargetWorktreePath = target.Path,
            SourceBranch = sourceEntry?.Branch ?? "",
        };

        if (sourceEntry is null)
            return RefusedPreview(basePreview, "that source worktree is not part of this repository", GitFailure.NotFound);

        if (sourceEntry.IsMain)
            return RefusedPreview(basePreview, "the main worktree cannot be rejected", GitFailure.WouldLoseChanges);

        if (!sourceEntry.IsUsable || sourceEntry.IsBare)
            return RefusedPreview(basePreview, "the source worktree has no usable working directory", GitFailure.NotFound);

        if (sourceEntry.IsLocked)
            return RefusedPreview(basePreview, "the source worktree is locked — unlock it before rejecting", GitFailure.WouldLoseChanges);

        if (string.IsNullOrWhiteSpace(sourceEntry.Branch))
            return RefusedPreview(basePreview, "the source worktree is detached and has no branch to reset", GitFailure.NotFound);

        var repository = await new RepositoryStateReader(git)
            .ReadAsync(sourceEntry.Path, ct)
            .ConfigureAwait(false);
        if (repository.ProbeFailed)
            return RefusedPreview(basePreview, "the source repository state could not be read safely", GitFailure.Unknown);

        if (repository.IsOperationInProgress || repository.HasConflicts)
            return RefusedPreview(
                basePreview,
                $"the source worktree has {repository.Description}; finish or abort it first",
                GitFailure.OperationInProgress);

        var sourceHead = await ReadHeadAsync(sourceEntry.Path, ct).ConfigureAwait(false);
        if (sourceHead is null)
            return RefusedPreview(basePreview, "the source branch has no commit yet", GitFailure.NotFound);

        var branchTip = await git.TryRunAsync(
            sourceEntry.Path,
            ct,
            "show-ref",
            "--verify",
            "--hash",
            $"refs/heads/{sourceEntry.Branch}")
            .ConfigureAwait(false);
        if (!branchTip.Success || !string.Equals(branchTip.Trimmed, sourceHead, StringComparison.OrdinalIgnoreCase))
            return RefusedPreview(basePreview,
                "the source branch moved while it was being read; refresh and try again",
                GitFailure.WouldLoseChanges);

        var defaultBranch = await _bases.ResolveDefaultBranchAsync(sourceEntry.Path, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(defaultBranch))
            return RefusedPreview(basePreview,
                "the repository's base branch could not be determined safely",
                GitFailure.NotFound);

        var baseTip = await git.TryRunAsync(
            sourceEntry.Path,
            ct,
            "rev-parse",
            "--verify",
            "--quiet",
            $"{defaultBranch}^{{commit}}")
            .ConfigureAwait(false);
        if (!baseTip.Success || !IsObjectId(baseTip.Trimmed))
            return RefusedPreview(basePreview,
                "the repository's base branch could not be read safely",
                GitFailure.Unknown);

        var mergeBaseResult = await git.TryRunAsync(
            sourceEntry.Path,
            ct,
            "merge-base",
            defaultBranch,
            sourceHead)
            .ConfigureAwait(false);
        if (!mergeBaseResult.Success)
            return RefusedPreview(basePreview,
                mergeBaseResult.StandardError.Trim().Length > 0
                    ? "the source and base history could not be read safely"
                    : "the source branch has no common history with the repository base",
                mergeBaseResult.StandardError.Trim().Length > 0 ? GitFailure.Unknown : GitFailure.NotFound);

        var mergeBase = mergeBaseResult.Trimmed;
        if (!IsObjectId(mergeBase))
            return RefusedPreview(basePreview, "git returned an invalid base object", GitFailure.Unknown);

        var status = await ReadSnapshotAsync(sourceEntry.Path, ct).ConfigureAwait(false);
        if (!status.Success)
            return RefusedPreview(basePreview, "the source worktree status could not be read safely", GitFailure.Unknown);

        var diff = await git.TryRunAsync(
            sourceEntry.Path,
            ct,
            "diff",
            "--name-status",
            "-M",
            "-z",
            mergeBase,
            "--")
            .ConfigureAwait(false);
        if (!diff.Success)
            return RefusedPreview(basePreview, "the source changes could not be read safely", GitFailure.Unknown);

        var paths = DiffService.ParseNameStatus(diff.StandardOutput)
            .Select(file => new WorktreeRejectionPath
            {
                Path = file.Path,
                OldPath = file.OldPath,
                Kind = file.Kind,
            })
            .ToList();

        var known = paths.Select(path => path.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var path in status.Untracked)
        {
            if (known.Add(path))
                paths.Add(new WorktreeRejectionPath { Path = path, Kind = ChangeKind.Untracked });
        }

        var countResult = await git.TryRunAsync(
            sourceEntry.Path,
            ct,
            "rev-list",
            "--count",
            $"{mergeBase}..{sourceHead}")
            .ConfigureAwait(false);
        if (!countResult.Success || !int.TryParse(countResult.Trimmed, out var commitCount) || commitCount < 0)
            return RefusedPreview(basePreview, "the source commit count could not be read safely", GitFailure.Unknown);

        return new WorktreeRejectionPreview
        {
            SourceWorktreePath = sourceEntry.Path,
            TargetWorktreePath = target.Path,
            SourceBranch = sourceEntry.Branch,
            SourceHead = sourceHead,
            BaseBranch = defaultBranch,
            BaseHead = mergeBase,
            Paths = paths
                .OrderBy(path => path.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path.Path, StringComparer.Ordinal)
                .ToArray(),
            IgnoredPaths = status.Ignored,
            CommitCount = commitCount,
            SnapshotFingerprint = status.Fingerprint,
            TrackedFingerprint = status.TrackedFingerprint,
            Failure = GitFailure.None,
        };
    }

    private async Task<GitMutation> CleanPreviewedPathsAsync(
        WorktreeRejectionPreview preview, CancellationToken ct)
    {
        var untracked = preview.Paths
            .Where(path => path.Kind is ChangeKind.Untracked)
            .Select(path => path.Path)
            .Concat(preview.IgnoredPaths)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var operation = $"reject {preview.SourceBranch} cleanup";

        if (untracked.Length == 0)
            return NoOp(preview.SourceWorktreePath, operation, "No untracked or ignored files to remove");

        // Keep below Windows' process command-line ceiling without reverting to an unscoped
        // `git clean`. A new path that appears after the preview is therefore never named by
        // this operation and survives for the post-cleanup stale check above.
        const int maxArgumentCharacters = 20_000;
        var batches = new List<List<string>>();
        var current = new List<string>();
        var length = 0;

        foreach (var path in untracked)
        {
            var literal = StagingService.Literal(path);
            if (current.Count > 0 && length + literal.Length + 1 > maxArgumentCharacters)
            {
                batches.Add(current);
                current = [];
                length = 0;
            }

            current.Add(literal);
            length += literal.Length + 1;
        }

        if (current.Count > 0) batches.Add(current);

        GitMutation? latest = null;
        foreach (var batch in batches)
        {
            latest = await writer.RunAsync(
                preview.SourceWorktreePath,
                operation,
                WriteKind.StartsOperation,
                ct,
                ["clean", "-f", "-d", "-x", "--", .. batch])
                .ConfigureAwait(false);
            if (!latest.Success) return latest;
        }

        return latest! with
        {
            Operation = operation,
            Detail = $"Removed {untracked.Length} untracked or ignored path(s)",
        };
    }

    private async Task<bool> VerifyAsync(WorktreeRejectionPreview preview, CancellationToken ct)
    {
        var head = await ReadHeadAsync(preview.SourceWorktreePath, ct).ConfigureAwait(false);
        if (!string.Equals(head, preview.BaseHead, StringComparison.OrdinalIgnoreCase)) return false;

        var status = await git.TryRunAsync(
            preview.SourceWorktreePath,
            ct,
            "status",
            "--porcelain=v2",
            "-z",
            "--untracked-files=all",
            "--ignored")
            .ConfigureAwait(false);
        if (!status.Success) return false;

        var remaining = RepoPaths.SplitNul(status.StandardOutput)
            .Where(record => record.StartsWith("? ", StringComparison.Ordinal) ||
                             record.StartsWith("1 ", StringComparison.Ordinal) ||
                             record.StartsWith("2 ", StringComparison.Ordinal) ||
                             record.StartsWith("u ", StringComparison.Ordinal))
            .Select(RepoPaths.PathFromStatusRecord)
            .Where(path => path.Length > 0)
            .ToArray();

        return remaining.Length == 0;
    }

    private async Task<Snapshot> ReadSnapshotAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var statusTask = git.TryRunAsync(
                worktreePath,
                ct,
                "status",
                "--porcelain=v2",
                "-z",
                "--untracked-files=all",
                "--ignored");
            var diffTask = git.RunBytesAsync(
                worktreePath,
                ct,
                "diff",
                "--binary",
                "--full-index",
                "--no-ext-diff",
                "--no-textconv",
                "HEAD",
                "--");

            await Task.WhenAll(statusTask, diffTask).ConfigureAwait(false);
            var result = await statusTask.ConfigureAwait(false);
            var diff = await diffTask.ConfigureAwait(false);
            if (!result.Success || !diff.Success) return Snapshot.Failed;

            var records = RepoPaths.SplitNul(result.StandardOutput);
            var untracked = new List<string>();
            var ignored = new List<string>();

            foreach (var record in records)
            {
                if (record.StartsWith("? ", StringComparison.Ordinal))
                    untracked.Add(record[2..]);
                else if (record.StartsWith("! ", StringComparison.Ordinal))
                    ignored.Add(record[2..]);
            }

            using var trackedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var record in records.Where(record =>
                         !record.StartsWith("? ", StringComparison.Ordinal) &&
                         !record.StartsWith("! ", StringComparison.Ordinal)))
                AppendText(trackedHash, record);
            trackedHash.AppendData(diff.StandardOutput);
            var trackedFingerprint = Convert.ToHexString(trackedHash.GetHashAndReset());

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendText(hash, trackedFingerprint);
            AppendText(hash, result.StandardOutput);

            foreach (var path in untracked
                         .Concat(ignored)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                AppendText(hash, path);
                if (!AppendFileBytes(hash, worktreePath, path)) return Snapshot.Failed;
            }

            return new Snapshot(
                true,
                untracked,
                ignored,
                Convert.ToHexString(hash.GetHashAndReset()),
                trackedFingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitException)
        {
            return Snapshot.Failed;
        }
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BitConverter.TryWriteBytes(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool AppendFileBytes(IncrementalHash hash, string worktreePath, string path)
    {
        string absolute;
        try
        {
            if (RepoPaths.EntersGitDirectory(path)) return false;
            absolute = RepoPaths.Resolve(worktreePath, path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            if (File.Exists(absolute))
            {
                using var stream = File.OpenRead(absolute);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
                return true;
            }

            // With --untracked-files=all Git normally emits every file. A nested repository
            // or an unreadable directory can still arrive as one directory record; include a
            // stable marker rather than recursively walking outside the worktree.
            if (Directory.Exists(absolute))
            {
                AppendText(hash, "directory");
                AppendText(hash, Directory.GetLastWriteTimeUtc(absolute).Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }

            // The file vanished while the preview was being read. Treat that as a distinct
            // snapshot; the status fingerprint will make a later action re-read it.
            AppendText(hash, "missing");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> ReadHeadAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
            worktreePath,
            ct,
            "rev-parse",
            "--verify",
            "--quiet",
            "HEAD")
            .ConfigureAwait(false);
        return result.Success && IsObjectId(result.Trimmed) ? result.Trimmed : null;
    }

    private static WorktreeRejection Refused(WorktreeRejectionPreview preview)
    {
        var failure = new GitMutation
        {
            Operation = $"reject {preview.SourceBranch}".TrimEnd(),
            WorktreePath = preview.SourceWorktreePath,
            CommandLine = "",
            ExitCode = -1,
            Failure = preview.Failure,
            Detail = preview.Detail,
            Attempts = 0,
        };

        return new WorktreeRejection
        {
            Preview = preview,
            Cleanup = failure,
            Reset = failure,
            Verified = false,
        };
    }

    private static WorktreeRejectionPreview RefusedPreview(string detail, GitFailure failure) => new()
    {
        Failure = failure,
        Detail = $"Could not reject the worktree: {detail}",
    };

    private static WorktreeRejectionPreview RefusedPreview(
        WorktreeRejectionPreview preview,
        string detail,
        GitFailure failure) => preview with
        {
            Failure = failure,
            Detail = $"Could not reject the worktree: {detail}",
        };

    private static GitMutation NoOp(string worktreePath, string operation, string detail) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = 0,
        Failure = GitFailure.None,
        Detail = detail,
        Attempts = 0,
    };

    private static bool IsObjectId(string value) =>
        value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);

    private sealed record Snapshot(
        bool Success,
        IReadOnlyList<string> Untracked,
        IReadOnlyList<string> Ignored,
        string Fingerprint,
        string TrackedFingerprint)
    {
        public static Snapshot Failed { get; } = new(false, [], [], "", "");
    }
}
