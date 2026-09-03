using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Chapter.Core.Git;

/// <summary>The safe, marker-free choices the conflict surface can write to a file.</summary>
public enum ConflictResolutionAction
{
    Ours,
    Theirs,
    Both,
    Manual,
}

/// <summary>A region delimited by Git's conflict markers in the working-tree file.</summary>
public sealed record ConflictRegion
{
    public int StartLine { get; init; }
    public int? BaseLine { get; init; }
    public int SeparatorLine { get; init; }
    public int EndLine { get; init; }
    public string OursText { get; init; } = "";
    public string BaseText { get; init; } = "";
    public string TheirsText { get; init; } = "";
}

/// <summary>The three index stages and the current working copy for one conflict.</summary>
public sealed record ConflictFile
{
    public required string Path { get; init; }

    /// <summary>Stage 1, the common ancestor. Null means this stage does not exist.</summary>
    public string? BaseText { get; init; }

    /// <summary>Stage 2, the current side (ours). Null means this side deleted the path.</summary>
    public string? OursText { get; init; }

    /// <summary>Stage 3, the incoming side (theirs). Null means this side deleted the path.</summary>
    public string? TheirsText { get; init; }

    public string WorkingText { get; init; } = "";
    public bool WorkingFileExists { get; init; }
    public bool IsBinary { get; init; }
    /// <summary>Whether the working text can be written back without changing its format.</summary>
    public bool CanRoundTrip { get; init; } = true;
    public IReadOnlyList<ConflictRegion> Regions { get; init; } = [];
    public string Fingerprint { get; init; } = "";

    // Raw stage bytes stay internal to the resolver. They are needed for binary ours/theirs
    // choices, but exposing them over JSON would make the conflict payload needlessly huge.
    internal byte[]? BaseBytes { get; init; }
    internal byte[]? OursBytes { get; init; }
    internal byte[]? TheirsBytes { get; init; }
    internal bool BaseCanRoundTrip { get; init; }
    internal bool OursCanRoundTrip { get; init; }
    internal bool TheirsCanRoundTrip { get; init; }
    internal bool BasePresent { get; init; }
    internal bool OursPresent { get; init; }
    internal bool TheirsPresent { get; init; }

    public bool HasBase => BasePresent || BaseText is not null;
    public bool HasOurs => OursPresent || OursText is not null;
    public bool HasTheirs => TheirsPresent || TheirsText is not null;
}

/// <summary>
/// Operation and conflict information needed by the persistent resolution banner.
///
/// A stash apply/pop has no operation marker when it stops on a conflict. In that case
/// <see cref="Operation"/> remains <see cref="RepositoryOperation.None"/> and
/// <see cref="IsStashRestore"/> tells the UI which special semantics apply.
/// </summary>
public sealed record ConflictState
{
    public required string WorktreePath { get; init; }
    public RepositoryOperation Operation { get; init; }
    public string? Branch { get; init; }
    public string Description { get; init; } = "clean";
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public IReadOnlyList<ConflictFile> Files { get; init; } = [];
    public bool IsStashRestore { get; init; }
    public string? StashVerb { get; init; }
    public string? StashSha { get; init; }
    public string? OriginalHead { get; init; }
    public string? CurrentCommit { get; init; }
    public string? CurrentSubject { get; init; }
    public RebaseAction? CurrentAction { get; init; }
    public int? Step { get; init; }
    public int? TotalSteps { get; init; }
    public bool HasConflicts => ConflictedPaths.Count > 0;
    // A stash restore has no Git marker, so keep its banner alive until the user explicitly
    // continues it. Bisect is a navigation state rather than a paused resolution surface.
    public bool IsPaused => IsStashRestore || HasConflicts ||
        Operation is not (RepositoryOperation.None or RepositoryOperation.Bisect);
    public bool CanContinue { get; init; }
    public bool CanSkip { get; init; }
    public bool CanAbort { get; init; }
    public bool CanMarkResolved => HasConflicts;
}

/// <summary>
/// Reads and resolves Git's unmerged index stages, and clears multi-step operations through
/// their operation-specific commands.
///
/// Git intentionally does not provide one common "continue" command. Keeping the mapping
/// here means the UI can offer one persistent surface without accidentally running
/// <c>cherry-pick --continue</c> for a rebase or offering an impossible merge skip.
/// </summary>
public sealed class ConflictService
{
    private readonly GitCli _git;
    private readonly GitWriter _writer;
    private readonly StagingService _staging;
    private readonly RebaseService _rebases;
    private readonly RepositoryStateReader _stateReader;
    private readonly ConcurrentDictionary<string, StashConflict> _stashConflicts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called after a direct working-tree resolution so workspace caches drop.</summary>
    public Action<string>? Changed { get; set; }

    public ConflictService(
        GitCli git,
        GitWriter writer,
        StagingService staging,
        RebaseService rebases)
    {
        _git = git;
        _writer = writer;
        _staging = staging;
        _rebases = rebases;
        _stateReader = new RepositoryStateReader(git);
    }

    /// <summary>Called by <see cref="StashService"/> when a restore stops on conflicts.</summary>
    public void NoteStashConflict(string worktreePath, string verb, string sha)
    {
        _stashConflicts[worktreePath] = new StashConflict(verb, sha);
    }

    /// <summary>Clears a remembered stash operation after it completes cleanly.</summary>
    public void ClearStashConflict(string worktreePath) => _stashConflicts.TryRemove(worktreePath, out _);

    /// <summary>Reads operation state, index stages, working content and marker regions.</summary>
    public async Task<ConflictState> GetStateAsync(
        string worktreePath, CancellationToken ct = default)
    {
        var repository = await _stateReader.ReadAsync(worktreePath, ct).ConfigureAwait(false);
        var stash = _stashConflicts.TryGetValue(worktreePath, out var remembered) ? remembered : null;
        // Read the index independently of porcelain status. A partially written status
        // record (or a path containing unusual bytes) must not make the three stage blobs
        // disappear from the conflict editor.
        var stages = await ReadStagesAsync(worktreePath, ct).ConfigureAwait(false);

        var paths = repository.ConflictedPaths
            .Concat(stages.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var files = await Task.WhenAll(paths.Select(path =>
            ReadFileAsync(worktreePath, path, stages.TryGetValue(path, out var entry) ? entry : null, ct)))
            .ConfigureAwait(false);
        var hasConflicts = paths.Length > 0;
        var canTrustStatus = !repository.ProbeFailed;

        var operation = repository.Operation;
        RebaseState? rebase = null;
        if (operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
            rebase = await _rebases.GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        var description = operation is RepositoryOperation.None
            ? stash is null
                ? repository.HasConflicts ? "conflicted working tree" : repository.Description
                : $"stash {stash.Verb} stopped on conflicts"
            : repository.Description;

        var canContinue = operation switch
        {
            RepositoryOperation.Merge or
            RepositoryOperation.Rebase or
            RepositoryOperation.RebaseInteractive or
            RepositoryOperation.CherryPick or
            RepositoryOperation.Revert or
            RepositoryOperation.ApplyMailbox => canTrustStatus && !hasConflicts,
            RepositoryOperation.None when stash is not null => canTrustStatus && !hasConflicts,
            _ => false,
        };

        if (operation is RepositoryOperation.Merge or
            RepositoryOperation.Rebase or
            RepositoryOperation.RebaseInteractive or
            RepositoryOperation.CherryPick or
            RepositoryOperation.Revert)
        {
            canContinue = canTrustStatus && !hasConflicts;
        }

        var canSkip = operation is RepositoryOperation.Rebase or
            RepositoryOperation.RebaseInteractive or
            RepositoryOperation.CherryPick or
            RepositoryOperation.Revert or
            RepositoryOperation.ApplyMailbox;

        // A stash restore has no safe Git abort command. `reset --merge` can discard local
        // edits that existed before an apply, so the banner deliberately offers no fake
        // abort; the stash entry remains available for another attempt.
        var canAbort = operation is RepositoryOperation.Merge or
            RepositoryOperation.Rebase or
            RepositoryOperation.RebaseInteractive or
            RepositoryOperation.CherryPick or
            RepositoryOperation.Revert or
            RepositoryOperation.ApplyMailbox;

        if (stash is not null && operation is RepositoryOperation.None)
        {
            canContinue = canTrustStatus && !hasConflicts;
            canSkip = false;
            canAbort = false;
        }

        return new ConflictState
        {
            WorktreePath = worktreePath,
            Operation = operation,
            Branch = repository.Branch,
            Description = description,
            ConflictedPaths = paths,
            Files = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            IsStashRestore = stash is not null,
            StashVerb = stash?.Verb,
            StashSha = stash?.Sha,
            OriginalHead = rebase?.OriginalHead,
            CurrentCommit = rebase?.CurrentCommit,
            CurrentSubject = rebase?.CurrentSubject,
            CurrentAction = rebase?.CurrentAction,
            Step = rebase?.Step ?? repository.Step,
            TotalSteps = rebase?.TotalSteps ?? repository.TotalSteps,
            CanContinue = canContinue,
            CanSkip = canSkip,
            CanAbort = canAbort,
        };
    }

    /// <summary>Alias for hosts that call the payload an operation state.</summary>
    public Task<ConflictState> GetOperationStateAsync(
        string worktreePath, CancellationToken ct = default) => GetStateAsync(worktreePath, ct);

    /// <summary>Reads one conflicted path, including all available index stages.</summary>
    public async Task<ConflictFile?> GetFileAsync(
        string worktreePath, string path, CancellationToken ct = default)
    {
        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        return state.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes one side, both sides, or an explicitly supplied manual result to the working
    /// tree. Writing does not stage automatically: the user must still mark the file
    /// resolved, which keeps an accidental choice reversible until it is inspected.
    /// </summary>
    public async Task<GitMutation> ResolveFileAsync(
        string worktreePath,
        string path,
        ConflictResolutionAction action,
        string manualText = "",
        CancellationToken ct = default,
        int region = -1,
        string expectedFingerprint = "")
    {
        var operation = $"resolve conflict {path}";
        var lease = await RepositoryWriteLock.AcquireAsync(_git, worktreePath, ct)
            .ConfigureAwait(false);
        if (lease is null)
            return Refused(worktreePath, operation, GitFailure.Locked,
                "another Chapter instance is writing this repository — try again");

        using (lease)
        {
            return await ResolveFileUnderLeaseAsync(
                    worktreePath, path, action, manualText, ct, region, expectedFingerprint)
                .ConfigureAwait(false);
        }
    }

    private async Task<GitMutation> ResolveFileUnderLeaseAsync(
        string worktreePath,
        string path,
        ConflictResolutionAction action,
        string manualText,
        CancellationToken ct,
        int region,
        string expectedFingerprint)
    {
        var operation = $"resolve conflict {path}";
        var file = await GetFileAsync(worktreePath, path, ct).ConfigureAwait(false);
        if (file is null)
            return Refused(worktreePath, operation, GitFailure.NotFound, "that path is not conflicted");

        if (expectedFingerprint.Length > 0 &&
            !string.Equals(expectedFingerprint, file.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "the conflict changed since it was shown; read it again before choosing");
        }

        if (action is ConflictResolutionAction.Manual && manualText is null)
            return Refused(worktreePath, operation, GitFailure.NotFound, "manual content was not supplied");

        if (region >= 0)
        {
            return await ResolveRegionAsync(
                    worktreePath, operation, file, action, manualText, region, ct)
                .ConfigureAwait(false);
        }

        if (file.IsBinary && action is ConflictResolutionAction.Both or ConflictResolutionAction.Manual)
        {
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "binary conflicts need an explicit ours or theirs choice");
        }

        if (action is ConflictResolutionAction.Both &&
            (!file.OursCanRoundTrip || !file.TheirsCanRoundTrip))
        {
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "both sides cannot be combined safely because one side's encoding is not lossless");
        }

        string? text = action switch
        {
            ConflictResolutionAction.Ours => file.OursText,
            ConflictResolutionAction.Theirs => file.TheirsText,
            ConflictResolutionAction.Both => JoinBoth(file.OursText, file.TheirsText),
            ConflictResolutionAction.Manual => manualText,
            _ => null,
        };

        var absolute = ResolvePath(worktreePath, path);
        if (absolute is null)
            return Refused(worktreePath, operation, GitFailure.NotFound, "that path is outside the worktree");

        var selectedBytes = action switch
        {
            ConflictResolutionAction.Ours => file.OursBytes,
            ConflictResolutionAction.Theirs => file.TheirsBytes,
            _ => null,
        };

        // A stage can contain an empty (but real) blob. Presence is tracked separately from
        // the decoded text so an empty file is not mistaken for a deletion.
        var selectedPresent = action switch
        {
            ConflictResolutionAction.Ours => file.HasOurs,
            ConflictResolutionAction.Theirs => file.HasTheirs,
            ConflictResolutionAction.Both => file.HasOurs || file.HasTheirs,
            _ => true,
        };

        var bothSidesDeleted = action is ConflictResolutionAction.Both &&
            !file.HasOurs && !file.HasTheirs;
        if ((text is null || bothSidesDeleted) && selectedBytes is null && !selectedPresent)
        {
            using var deleteScope = _writer.SelfWriteScope?.Invoke(worktreePath);
            try
            {
                if (File.Exists(absolute)) File.Delete(absolute);
                Changed?.Invoke(worktreePath);
                return Synthetic(worktreePath, operation,
                    $"Removed {path}; stage it to mark the deletion resolved.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                    $"could not remove the working file: {ex.Message}");
            }
        }

        // Text stages must go through the working file's TextFormat. Writing the raw index
        // blob here is subtly wrong with core.autocrlf: the index is normally LF while the
        // working tree is CRLF, so a harmless conflict choice would rewrite the whole file.
        // A null decoded side is genuinely binary (or an absent side), and only that case
        // takes the raw-byte path.
        var selectedCanRoundTrip = action switch
        {
            ConflictResolutionAction.Ours => file.OursCanRoundTrip,
            ConflictResolutionAction.Theirs => file.TheirsCanRoundTrip,
            _ => true,
        };
        // Prefer the exact stage bytes when decoding was lossy, or when the working file's
        // format itself cannot survive Monaco. A side choice is explicit replacement, so
        // preserving that blob is safer than manufacturing replacement characters or a new
        // newline convention.
        var useRawSide = selectedBytes is not null &&
            action is (ConflictResolutionAction.Ours or ConflictResolutionAction.Theirs) &&
            (text is null || !selectedCanRoundTrip || (file.WorkingFileExists && !file.CanRoundTrip));
        if (useRawSide)
        {
            if (selectedBytes is null)
                return Refused(worktreePath, operation, GitFailure.NotFound,
                    "the selected side is no longer available");
            using var bytesScope = _writer.SelfWriteScope?.Invoke(worktreePath);
            var raw = await WorkingTreeWriter
                .SaveBytesAsyncUnderLease(worktreePath, path, selectedBytes, ct)
                .ConfigureAwait(false);
            return raw.Success
                ? ResolutionWritten(worktreePath, operation,
                    $"Wrote {action.ToString().ToLowerInvariant()} for {path}; stage it to mark resolved.")
                : Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                    raw.Error ?? "the file could not be written");
        }

        text ??= "";

        var existing = file.WorkingFileExists
            ? await DiffService.GetWorkingContentAsync(worktreePath, path, ct).ConfigureAwait(false)
            : FileContent.Empty;
        if (file.WorkingFileExists && !existing.IsBinary && !existing.CanRoundTrip)
        {
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "the working file's encoding or line endings cannot be preserved safely");
        }
        var format = existing.WorkingFormatOrDefault();

        // A deleted working file has no format to preserve. Prefer the common ancestor,
        // then either side, so a BOM survives a take-ours/theirs choice when possible.
        if (!file.WorkingFileExists)
        {
            var sourceBytes = action switch
            {
                ConflictResolutionAction.Ours => file.OursBytes,
                ConflictResolutionAction.Theirs => file.TheirsBytes,
                _ => file.BaseBytes ?? file.OursBytes ?? file.TheirsBytes,
            };
            if (sourceBytes is not null)
            {
                var sourceContent = FileContent.FromBytes(sourceBytes);
                format = sourceContent.CanRoundTrip ? sourceContent.Format : TextFormat.Default;
            }
        }

        using var textScope = _writer.SelfWriteScope?.Invoke(worktreePath);
        var saved = await WorkingTreeWriter
            .SaveAsyncUnderLease(worktreePath, path, text, format, ct)
            .ConfigureAwait(false);
        return saved.Success
            ? ResolutionWritten(worktreePath, operation,
                $"Wrote {action.ToString().ToLowerInvariant()} for {path}; stage it to mark resolved.")
            : Refused(worktreePath, operation, GitFailure.WouldLoseChanges, saved.Error ?? "the file could not be written");
    }

    /// <summary>Marks one path (or every conflicted path when empty) resolved in the index.</summary>
    public async Task<GitMutation> MarkResolvedAsync(
        string worktreePath, string path = "", CancellationToken ct = default)
    {
        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        var paths = path.Length == 0 ? state.ConflictedPaths : [path];
        if (paths.Count == 0)
            return Refused(worktreePath, "mark resolved", GitFailure.NothingToDo, "there are no conflicted paths");

        if (path.Length > 0 && !state.ConflictedPaths.Contains(path, StringComparer.Ordinal))
            return Refused(worktreePath, "mark resolved", GitFailure.NotFound, "that path is not conflicted");

        foreach (var conflictedPath in paths)
        {
            var file = state.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, conflictedPath, StringComparison.Ordinal));
            if (file?.Regions.Count > 0)
            {
                return Refused(worktreePath, "mark resolved", GitFailure.Conflict,
                    $"{conflictedPath} still contains {file.Regions.Count} conflict marker region(s)");
            }
        }

        return await _staging.StageAsync(worktreePath, paths, ct).ConfigureAwait(false);
    }

    /// <summary>Continues the operation currently recorded by Git.</summary>
    public async Task<GitMutation> ContinueAsync(
        string worktreePath, string message = "", CancellationToken ct = default)
    {
        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        if (state.Operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
            return await _rebases.ContinueAsync(worktreePath, message, ct).ConfigureAwait(false);

        if (state.Operation is RepositoryOperation.None && state.IsStashRestore)
        {
            if (state.HasConflicts)
                return Refused(worktreePath, "continue stash restore", GitFailure.Conflict,
                    "stage every conflicted path first");

            ClearStashConflict(worktreePath);
            return Synthetic(worktreePath, "continue stash restore",
                "The conflict is resolved; the stash entry was kept.");
        }

        return await RunSequencerAsync(worktreePath, state.Operation, "continue", message, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Skips the current sequencer item when that operation supports skipping.</summary>
    public async Task<GitMutation> SkipAsync(string worktreePath, CancellationToken ct = default)
    {
        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        if (state.Operation is not (RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive
            or RepositoryOperation.CherryPick or RepositoryOperation.Revert or RepositoryOperation.ApplyMailbox))
        {
            return Refused(worktreePath, "skip operation", GitFailure.OperationInProgress,
                "this operation has no skip command");
        }

        if (state.Operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
            return await _rebases.SkipAsync(worktreePath, ct).ConfigureAwait(false);

        return await RunSequencerAsync(worktreePath, state.Operation, "skip", "", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Aborts the current merge, rebase, cherry-pick, revert or mailbox apply.</summary>
    public async Task<GitMutation> AbortAsync(string worktreePath, CancellationToken ct = default)
    {
        var state = await GetStateAsync(worktreePath, ct).ConfigureAwait(false);
        if (state.Operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive)
            return await _rebases.AbortAsync(worktreePath, ct).ConfigureAwait(false);

        if (state.Operation is RepositoryOperation.None && state.IsStashRestore)
        {
            return Refused(worktreePath, "abort stash restore", GitFailure.WouldLoseChanges,
                "stash restore has no safe abort command; the stash entry is still available");
        }

        return await RunSequencerAsync(worktreePath, state.Operation, "abort", "", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Turns on Git's recorded-resolution database for this repository.</summary>
    public Task<GitMutation> EnableRerereAsync(
        string worktreePath, CancellationToken ct = default) =>
        _writer.RunAsync(worktreePath, "enable rerere", WriteKind.WorkingTree, ct,
            "config", "rerere.enabled", "true");

    /// <summary>Asks Git to apply any recorded resolutions to the current conflict.</summary>
    public Task<GitMutation> ApplyRerereAsync(
        string worktreePath, CancellationToken ct = default) =>
        _writer.RunAsync(worktreePath, "apply rerere", WriteKind.ResolvesOperation, ct, "rerere");

    /// <summary>Lists paths for which Git has a recorded rerere resolution.</summary>
    public async Task<IReadOnlyList<string>> RerereStatusAsync(
        string worktreePath, CancellationToken ct = default)
    {
        var result = await _git.TryRunAsync(worktreePath, ct, "rerere", "status").ConfigureAwait(false);
        if (!result.Success) return [];
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Forgets recorded resolutions for the supplied paths.</summary>
    public Task<GitMutation> ForgetRerereAsync(
        string worktreePath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return Task.FromResult(Refused(worktreePath, "forget rerere", GitFailure.NothingToDo,
                "no paths were supplied"));

        return _writer.RunAsync(worktreePath, "forget rerere", WriteKind.ResolvesOperation, ct,
            ["rerere", "forget", "--", .. paths.Select(StagingService.Literal)]);
    }

    private async Task<GitMutation> RunSequencerAsync(
        string worktreePath,
        RepositoryOperation operation,
        string verb,
        string message,
        CancellationToken ct)
    {
        var (command, noun) = operation switch
        {
            RepositoryOperation.Merge => ("merge", "merge"),
            RepositoryOperation.CherryPick => ("cherry-pick", "cherry-pick"),
            RepositoryOperation.Revert => ("revert", "revert"),
            RepositoryOperation.ApplyMailbox => ("am", "patch application"),
            _ => ("", "operation"),
        };

        if (command.Length == 0)
            return Refused(worktreePath, $"{verb} operation", GitFailure.OperationInProgress,
                "there is no supported operation in progress");

        using var editor = await ContinuationEditor.CreateAsync(message, ct).ConfigureAwait(false);
        return await _writer.RunWithEnvironmentAsync(
                worktreePath, $"{command} --{verb}", WriteKind.ResolvesOperation, GitIntent.Write,
                editor.Environment, ct, [command, $"--{verb}"])
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<string, StageEntries>> ReadStagesAsync(
        string worktreePath, CancellationToken ct)
    {
        var result = await _git.TryRunAsync(worktreePath, ct, "ls-files", "-u", "-z")
            .ConfigureAwait(false);
        var stages = new Dictionary<string, StageEntries>(StringComparer.Ordinal);
        if (!result.Success) return stages;

        foreach (var token in RepoPaths.SplitNul(result.StandardOutput))
        {
            var tab = token.IndexOf('\t');
            if (tab < 0) continue;

            var header = token[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 3 || !int.TryParse(header[2], out var stage)) continue;
            if (stage is < 1 or > 3) continue;

            var path = token[(tab + 1)..];
            if (!stages.TryGetValue(path, out var entry)) entry = new StageEntries();
            entry = stage switch
            {
                1 => entry with { BaseSha = header[1] },
                2 => entry with { OursSha = header[1] },
                3 => entry with { TheirsSha = header[1] },
                _ => entry,
            };
            stages[path] = entry;
        }

        return stages;
    }

    private async Task<ConflictFile> ReadFileAsync(
        string worktreePath,
        string path,
        StageEntries? stages,
        CancellationToken ct)
    {
        var baseContent = await ReadStageAsync(worktreePath, path, stages?.BaseSha, ct).ConfigureAwait(false);
        var oursContent = await ReadStageAsync(worktreePath, path, stages?.OursSha, ct).ConfigureAwait(false);
        var theirsContent = await ReadStageAsync(worktreePath, path, stages?.TheirsSha, ct).ConfigureAwait(false);
        var working = await DiffService.GetWorkingContentAsync(worktreePath, path, ct).ConfigureAwait(false);
        var workingPath = ResolvePath(worktreePath, path);
        byte[]? workingBytes = null;
        var workingExists = false;
        if (workingPath is not null && File.Exists(workingPath))
        {
            workingExists = true;
            try
            {
                workingBytes = await File.ReadAllBytesAsync(workingPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The content read above is still useful for display. Fall back to its
                // decoded representation for the optimistic-concurrency fingerprint.
            }
        }

        var binary = (baseContent?.IsBinary ?? false) ||
            (oursContent?.IsBinary ?? false) ||
            (theirsContent?.IsBinary ?? false) || working.IsBinary;

        return new ConflictFile
        {
            Path = path,
            BaseText = baseContent?.IsBinary == true ? null : baseContent?.Text,
            OursText = oursContent?.IsBinary == true ? null : oursContent?.Text,
            TheirsText = theirsContent?.IsBinary == true ? null : theirsContent?.Text,
            WorkingText = working.IsBinary ? "" : working.Text,
            WorkingFileExists = workingExists || working.ByteLength > 0,
            IsBinary = binary,
            CanRoundTrip = !working.IsBinary && working.CanRoundTrip,
            Regions = working.IsBinary ? [] : ParseRegions(working.Text),
            Fingerprint = workingBytes is not null
                ? Fingerprint(workingBytes, exists: true)
                : Fingerprint(working, workingExists || working.ByteLength > 0),
            BaseBytes = baseContent?.Bytes,
            OursBytes = oursContent?.Bytes,
            TheirsBytes = theirsContent?.Bytes,
            BaseCanRoundTrip = baseContent?.Content.CanRoundTrip ?? true,
            OursCanRoundTrip = oursContent?.Content.CanRoundTrip ?? true,
            TheirsCanRoundTrip = theirsContent?.Content.CanRoundTrip ?? true,
            BasePresent = baseContent is not null,
            OursPresent = oursContent is not null,
            TheirsPresent = theirsContent is not null,
        };
    }

    private async Task<GitMutation> ResolveRegionAsync(
        string worktreePath,
        string operation,
        ConflictFile file,
        ConflictResolutionAction action,
        string manualText,
        int regionIndex,
        CancellationToken ct)
    {
        if (file.IsBinary)
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "binary conflicts do not contain text regions");
        if (!file.CanRoundTrip)
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "the working file's encoding or line endings cannot be preserved safely");
        if (regionIndex < 0 || regionIndex >= file.Regions.Count)
            return Refused(worktreePath, operation, GitFailure.NotFound,
                "that conflict region is no longer present");

        var region = file.Regions[regionIndex];
        var replacement = action switch
        {
            ConflictResolutionAction.Ours => region.OursText,
            ConflictResolutionAction.Theirs => region.TheirsText,
            ConflictResolutionAction.Both => JoinBoth(region.OursText, region.TheirsText),
            ConflictResolutionAction.Manual => manualText,
            _ => "",
        };

        var normalized = file.WorkingText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').ToList();
        var start = region.StartLine - 1;
        var count = region.EndLine - region.StartLine + 1;
        if (start < 0 || start + count > lines.Count)
            return Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                "the conflict markers no longer match the displayed region");

        lines.RemoveRange(start, count);
        var insert = replacement.Split('\n').ToList();
        // Empty means accept an empty side, not insert one phantom blank line.
        if (replacement.Length == 0) insert.Clear();
        lines.InsertRange(start, insert);

        var existing = await DiffService.GetWorkingContentAsync(worktreePath, file.Path, ct)
            .ConfigureAwait(false);
        using var regionScope = _writer.SelfWriteScope?.Invoke(worktreePath);
        var saved = await WorkingTreeWriter
            .SaveAsyncUnderLease(worktreePath, file.Path, string.Join("\n", lines), existing.Format, ct)
            .ConfigureAwait(false);

        return saved.Success
            ? ResolutionWritten(worktreePath, operation,
                $"Resolved conflict region {regionIndex + 1} in {file.Path} with {action.ToString().ToLowerInvariant()}.")
            : Refused(worktreePath, operation, GitFailure.WouldLoseChanges,
                saved.Error ?? "the file could not be written");
    }

    private async Task<StageContent?> ReadStageAsync(
        string worktreePath, string path, string? sha, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sha)) return null;
        var result = await _git.RunBytesAsync(worktreePath, ct, "cat-file", "blob", sha)
            .ConfigureAwait(false);
        return result.Success
            ? new StageContent(FileContent.FromBytes(result.StandardOutput), result.StandardOutput)
            : null;
    }

    private static IReadOnlyList<ConflictRegion> ParseRegions(string text)
    {
        if (text.Length == 0) return [];

        // Normalize only for parsing. The source text itself is returned unchanged, and
        // writes later go through TextFormat so CRLF files keep their original convention.
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var regions = new List<ConflictRegion>();
        var start = -1;
        var baseLine = (int?)null;
        var separator = -1;
        var ours = new List<string>();
        var theirs = new List<string>();
        var common = new List<string>();
        var mode = 0; // 1 ours, 2 base, 3 theirs

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                start = index + 1;
                baseLine = null;
                separator = -1;
                ours = [];
                theirs = [];
                common = [];
                mode = 1;
                continue;
            }

            if (start < 0) continue;

            if (mode == 1 && line.StartsWith("|||||||", StringComparison.Ordinal))
            {
                baseLine = index + 1;
                mode = 2;
                continue;
            }

            if (line.StartsWith("=======", StringComparison.Ordinal) && mode is 1 or 2)
            {
                separator = index + 1;
                mode = 3;
                continue;
            }

            if (mode == 3 && line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                regions.Add(new ConflictRegion
                {
                    StartLine = start,
                    BaseLine = baseLine,
                    SeparatorLine = separator < 0 ? index + 1 : separator,
                    EndLine = index + 1,
                    OursText = string.Join("\n", ours),
                    BaseText = string.Join("\n", common),
                    TheirsText = string.Join("\n", theirs),
                });
                start = -1;
                mode = 0;
                continue;
            }

            switch (mode)
            {
                case 1: ours.Add(line); break;
                case 2: common.Add(line); break;
                case 3: theirs.Add(line); break;
            }
        }

        return regions;
    }

    private static string JoinBoth(string? ours, string? theirs)
    {
        if (string.IsNullOrEmpty(ours)) return theirs ?? "";
        if (string.IsNullOrEmpty(theirs)) return ours;
        return ours.EndsWith('\n') ? ours + theirs : ours + "\n" + theirs;
    }

    private static string Fingerprint(FileContent content, bool exists)
    {
        return Fingerprint(
            content.IsBinary ? BitConverter.GetBytes(content.ByteLength) : Encoding.UTF8.GetBytes(content.Text),
            exists);
    }

    private static string Fingerprint(byte[] bytes, bool exists)
    {
        // Include existence in the digest so an empty file and a deleted file cannot share a
        // fingerprint and accidentally pass a stale-resolution check.
        var input = new byte[bytes.Length + 1];
        input[0] = exists ? (byte)1 : (byte)0;
        Buffer.BlockCopy(bytes, 0, input, 1, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string? ResolvePath(string worktreePath, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || RepoPaths.EntersGitDirectory(path)) return null;
        try { return RepoPaths.Resolve(worktreePath, path); }
        catch (ArgumentException) { return null; }
    }

    private GitMutation Synthetic(string worktreePath, string operation, string detail) => RecordDirect(new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = $"chapter {operation}",
        ExitCode = 0,
        Detail = detail,
        Attempts = 0,
    });

    private GitMutation ResolutionWritten(string worktreePath, string operation, string detail)
    {
        Changed?.Invoke(worktreePath);
        return Synthetic(worktreePath, operation, detail);
    }

    private GitMutation Refused(
        string worktreePath, string operation, GitFailure failure, string reason) => RecordDirect(new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = $"chapter {operation}",
        ExitCode = -1,
        Failure = failure,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    });

    private GitMutation RecordDirect(GitMutation mutation)
    {
        _writer.Log.Append(new Chapter.Core.Diagnostics.OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = mutation.Operation,
            WorktreePath = mutation.WorktreePath,
            CommandLine = mutation.CommandLine,
            ExitCode = mutation.ExitCode,
            Attempts = mutation.Attempts,
            Failure = mutation.Failure is GitFailure.None ? null : mutation.Failure.ToString(),
            Detail = mutation.Message,
        });
        return mutation;
    }

    private sealed record StageEntries
    {
        public string? BaseSha { get; init; }
        public string? OursSha { get; init; }
        public string? TheirsSha { get; init; }
    }

    private sealed record StashConflict(string Verb, string Sha);

    private sealed record StageContent(FileContent Content, byte[] Bytes)
    {
        public bool IsBinary => Content.IsBinary;
        public string Text => Content.Text;
    }

    /// <summary>A tiny editor that preserves Git's existing message, or replaces it.</summary>
    private sealed class ContinuationEditor : IDisposable
    {
        private readonly string _directory;
        public required IReadOnlyDictionary<string, string?> Environment { get; init; }

        private ContinuationEditor(string directory) => _directory = directory;

        public static async Task<ContinuationEditor> CreateAsync(
            string message, CancellationToken ct)
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "chapter-conflict-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var messagePath = Path.Combine(directory, "message.txt");
                if (message.Length > 0)
                    await File.WriteAllTextAsync(messagePath, message, new UTF8Encoding(false), ct)
                        .ConfigureAwait(false);

                var windows = OperatingSystem.IsWindows();
                var script = Path.Combine(directory, windows ? "editor.cmd" : "editor.sh");
                var target = windows ? "%~1" : "\"$1\"";
                var content = windows
                    ? message.Length > 0
                        ? $"@echo off\r\ncopy /Y \"{messagePath}\" \"{target}\" >nul\r\nexit /b 0\r\n"
                        : "@echo off\r\nexit /b 0\r\n"
                    : message.Length > 0
                        ? $"#!/bin/sh\ncp -- {ShellQuote(messagePath)} {target}\nexit 0\n"
                        : "#!/bin/sh\nexit 0\n";
                await File.WriteAllTextAsync(script, content, Encoding.ASCII, ct).ConfigureAwait(false);
                if (!windows)
                {
                    try
                    {
                        File.SetUnixFileMode(script,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (PlatformNotSupportedException) { }
                }

                return new ContinuationEditor(directory)
                {
                    Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["GIT_EDITOR"] = QuoteForEditor(script),
                        ["GIT_SEQUENCE_EDITOR"] = QuoteForEditor(script),
                    },
                };
            }
            catch
            {
                TryDelete(directory);
                throw;
            }
        }

        public void Dispose() => TryDelete(_directory);

        private static string QuoteForEditor(string path) => OperatingSystem.IsWindows()
            ? $"\"{path}\""
            : ShellQuote(path);

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal static class ConflictFileContentExtensions
{
    public static TextFormat WorkingFormatOrDefault(this FileContent content) =>
        content.ByteLength > 0 ? content.Format : TextFormat.Default;
}
