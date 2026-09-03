using System.Text.Json;
using System.Text.Json.Serialization;
using Chapter.Core.AgentSessions;

namespace Chapter.Core.Contracts;

/// <summary>
/// The contract between the C# backend and the Monaco front-end.
///
/// Everything crosses the WebView2 boundary as JSON via PostWebMessageAsJson — a plain
/// request/response protocol with a separate push channel for events. The TypeScript
/// mirror of these shapes lives in <c>Chapter.Web/src/protocol.ts</c>; the two must be
/// changed together.
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>A call from the front-end. <see cref="Id"/> is echoed back on the response.</summary>
public sealed record BridgeRequest
{
    public int Id { get; init; }
    public string Method { get; init; } = "";
    public JsonElement Params { get; init; }

    /// <summary>Deserialises the parameter payload, or returns a default when absent.</summary>
    public T ParamsAs<T>() where T : new() =>
        Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new T()
            : Params.Deserialize<T>(BridgeJson.Options) ?? new T();
}

public sealed record BridgeResponse
{
    public required int Id { get; init; }
    public required bool Ok { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }

    public static BridgeResponse Success(int id, object? result) => new() { Id = id, Ok = true, Result = result };
    public static BridgeResponse Failure(int id, string error) => new() { Id = id, Ok = false, Error = error };
}

/// <summary>An unsolicited push from the backend — file changes, index progress, and so on.</summary>
public sealed record BridgeEvent
{
    public required string Event { get; init; }
    public object? Payload { get; init; }
}

// ---------------------------------------------------------------------------
// Request payloads
// ---------------------------------------------------------------------------

public sealed record RepoRequest
{
    public string RepoPath { get; init; } = "";
}

public sealed record WorktreeRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Which slice of the work to show. Defaults to everything on the branch.</summary>
    public Git.DiffScope Scope { get; init; } = Git.DiffScope.Branch;
}

/// <summary>
/// Marks only the snapshot the front-end actually saw. If an agent writes between the read
/// and this call, the backend refuses instead of blessing unseen work as reviewed.
/// </summary>
public sealed record MarkReviewWatermarkRequest
{
    public string WorktreePath { get; init; } = "";
    public string ExpectedFingerprint { get; init; } = "";
}

/// <summary>Reads the local agent-session metadata associated with a worktree.</summary>
public sealed record AgentSessionsRequest
{
    public string WorktreePath { get; init; } = "";
}

/// <summary>
/// Opens one previously discovered session log through the host shell. The id is resolved
/// again on the backend; a path supplied by a page is never trusted on its own.
/// </summary>
public sealed record OpenAgentSessionRequest
{
    public string WorktreePath { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public string LogPath { get; init; } = "";
    public string Path { get; init; } = "";
}

/// <summary>Metadata for one local agent session; transcript content never crosses this type.</summary>
public sealed record AgentSessionPayload
{
    public required string Provider { get; init; }
    public required string Id { get; init; }
    public required string LogPath { get; init; }
    public string? Name { get; init; }
    public string? Branch { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? MessageCount { get; init; }

    public static AgentSessionPayload From(AgentSession session) => new()
    {
        Provider = session.Provider.ToString().ToLowerInvariant(),
        Id = session.Id,
        LogPath = session.LogPath,
        Name = session.Name,
        Branch = session.Branch,
        StartedAt = session.StartedAt,
        UpdatedAt = session.UpdatedAt,
        MessageCount = session.MessageCount,
    };
}

/// <summary>Session metadata for one worktree, with a stable empty result when none is found.</summary>
public sealed record AgentSessionsPayload
{
    public required string WorktreePath { get; init; }
    public required IReadOnlyList<AgentSessionPayload> Sessions { get; init; }
    public bool Success { get; init; } = true;
    public string Detail { get; init; } = "";
}

/// <summary>Result of asking the host to open a discovered session log.</summary>
public sealed record OpenAgentSessionPayload
{
    public bool Success { get; init; }
    public string WorktreePath { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string? LogPath { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Reads or updates the review watermark for a worktree.</summary>
public sealed record ReviewWatermarkPayload
{
    public required string WorktreePath { get; init; }
    public string Head { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public ReviewWatermark? Watermark { get; init; }
    public bool HasUnreviewedChanges { get; init; }
    public bool Success { get; init; }
    public string Detail { get; init; } = "";

    public static ReviewWatermarkPayload From(ReviewWatermarkStatus status) => new()
    {
        WorktreePath = status.WorktreePath,
        Head = status.Head,
        Fingerprint = status.Fingerprint,
        Watermark = status.Watermark,
        HasUnreviewedChanges = status.HasUnreviewedChanges,
        Success = status.Success,
        Detail = status.Detail,
    };
}

/// <summary>Names two sibling worktrees whose live files should be compared.</summary>
public sealed record WorktreeComparisonRequest
{
    public string LeftWorktreePath { get; init; } = "";
    public string RightWorktreePath { get; init; } = "";
}

/// <summary>Loads one pair of paths from a cross-worktree comparison.</summary>
public sealed record WorktreeComparisonFileRequest
{
    public string LeftWorktreePath { get; init; } = "";
    public string RightWorktreePath { get; init; } = "";

    /// <summary>Empty when the file exists only in the right worktree.</summary>
    public string LeftPath { get; init; } = "";

    /// <summary>Empty when the file exists only in the left worktree.</summary>
    public string RightPath { get; init; } = "";
}

public sealed record FileRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Must match the scope the file list was built with, or the two sides disagree.</summary>
    public Git.DiffScope Scope { get; init; } = Git.DiffScope.Branch;

    /// <summary>
    /// Which half of the uncommitted change to show. <see cref="Git.DiffSide.Combined"/>
    /// defers to <see cref="Scope"/> and is what every review view asks for; the commit view
    /// names a side, because staging a hunk means acting on one comparison specifically.
    /// </summary>
    public Git.DiffSide Side { get; init; } = Git.DiffSide.Combined;
}

public sealed record OpenInEditorRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public int Line { get; init; } = 1;
    public int Column { get; init; } = 1;

    /// <summary>Editor id, e.g. "rider" or "vscode". Empty means the configured default.</summary>
    public string Editor { get; init; } = "";
}

public sealed record NavigationRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed record SearchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Query { get; init; } = "";
    public int Limit { get; init; } = 50;
}

public sealed record SaveFileRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed record OperationLogRequest
{
    public int Limit { get; init; } = 100;
}

/// <summary>Whole-file staging, unstaging and discarding.</summary>
public sealed record StageRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Repo-relative paths, as the file list reports them.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Paths git does not track, which a discard has to delete rather than restore. Sent
    /// separately because the front-end already knows which is which and the backend would
    /// otherwise have to ask git again.
    /// </summary>
    public IReadOnlyList<string> Untracked { get; init; } = [];

    /// <summary>How much of a file's uncommitted state a discard should throw away.</summary>
    public Git.DiscardTarget Target { get; init; } = Git.DiscardTarget.Unstaged;
}

/// <summary>Staging or discarding part of a file, by hunk or by line range.</summary>
public sealed record PatchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Which comparison the line numbers refer to. Mixing the two corrupts the patch.</summary>
    public Git.DiffSide Side { get; init; } = Git.DiffSide.Unstaged;

    /// <summary>
    /// Hunk indices to apply, as ordered in the diff for <see cref="Side"/>. Empty means
    /// every hunk, which is how "stage this whole file's changes" reuses the same path.
    /// </summary>
    public IReadOnlyList<int> Hunks { get; init; } = [];

    /// <summary>
    /// Individual changed lines to apply, when the selection is finer than a hunk.
    /// Takes precedence over <see cref="Hunks"/> when both are present.
    /// </summary>
    public IReadOnlyList<PatchLineSelection> Lines { get; init; } = [];

    /// <summary>Reverses the patch — the direction that unstages or discards.</summary>
    public bool Reverse { get; init; }

    /// <summary>Applies to the working tree instead of the index. Only a discard does this.</summary>
    public bool ApplyToWorkingTree { get; init; }

    /// <summary>
    /// The <see cref="Git.FilePatch.Fingerprint"/> the selection was made against.
    ///
    /// Sent back so the backend can tell that the diff it re-reads is the one the user was
    /// looking at. Empty skips the check, which is only right for a whole-file operation
    /// where there are no indices to misinterpret.
    /// </summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>A file's diff as hunks the front-end can render staging controls against.</summary>
public sealed record FilePatchPayload
{
    public required string Path { get; init; }
    public required Git.DiffSide Side { get; init; }
    public required IReadOnlyList<HunkPayload> Hunks { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>Send back with any selection made against these hunks.</summary>
    public required string Fingerprint { get; init; }

    public static FilePatchPayload From(Git.FilePatch patch, string path, Git.DiffSide side) => new()
    {
        Path = path,
        Side = side,
        IsBinary = patch.IsBinary,
        Fingerprint = patch.Fingerprint,
        Hunks = [.. patch.Hunks.Select((h, index) => new HunkPayload
        {
            Index = index,
            Header = h.Header,
            OldStart = h.OldStart,
            OldCount = h.OldCount,
            NewStart = h.NewStart,
            NewCount = h.NewCount,
            Section = h.Section,
            Lines = h.Lines,
            AddedLines = h.AddedLines,
            RemovedLines = h.RemovedLines,
        })],
    };
}

/// <summary>
/// One hunk, as git divided it.
///
/// The front-end must render its staging controls from these boundaries rather than from
/// Monaco's. Monaco computes its own diff, and its change regions are grouped differently —
/// so a button placed on a Monaco region would send an index naming a hunk the user never
/// looked at.
/// </summary>
public sealed record HunkPayload
{
    public required int Index { get; init; }
    public required string Header { get; init; }
    public required int OldStart { get; init; }
    public required int OldCount { get; init; }
    public required int NewStart { get; init; }
    public required int NewCount { get; init; }
    public string Section { get; init; } = "";

    /// <summary>
    /// The hunk's lines with their leading markers. Positions in this list are exactly what
    /// <see cref="PatchLineSelection.Line"/> means, so both sides count the same thing.
    /// </summary>
    public required IReadOnlyList<string> Lines { get; init; }

    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
}

/// <summary>One changed line the user picked out of a hunk.</summary>
public sealed record PatchLineSelection
{
    /// <summary>Index of the hunk this line belongs to.</summary>
    public int Hunk { get; init; }

    /// <summary>
    /// Position of the line within the hunk body, counting every line the hunk contains —
    /// context, additions and deletions alike. Counting only changed lines would make the
    /// index depend on a filter the two sides have to agree on exactly.
    /// </summary>
    public int Line { get; init; }
}

public sealed record CommitCommandRequest
{
    public string WorktreePath { get; init; } = "";
    public string Message { get; init; } = "";
    public bool Amend { get; init; }
    public bool SignOff { get; init; }

    /// <summary>Null defers to the repository's own <c>commit.gpgsign</c>.</summary>
    public bool? Sign { get; init; }

    /// <summary>Co-authors as "Name &lt;email&gt;"; anything unparseable is dropped.</summary>
    public IReadOnlyList<string> CoAuthors { get; init; } = [];
}

public sealed record MessageReviewRequest
{
    public string WorktreePath { get; init; } = "";
    public string Message { get; init; } = "";
}

// ---------------------------------------------------------------------------
// Branches, stash and tags
// ---------------------------------------------------------------------------

/// <summary>Switching this worktree to a branch.</summary>
public sealed record SwitchBranchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Branch { get; init; } = "";

    /// <summary>
    /// What to do about uncommitted work. <see cref="Git.CheckoutStrategy.Carry"/> is the
    /// first attempt every time — git carries changes across whenever it can — and the
    /// stash form is what the UI sends after that attempt is refused.
    /// </summary>
    public Git.CheckoutStrategy Strategy { get; init; } = Git.CheckoutStrategy.Carry;
}

public sealed record CreateBranchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Where the branch begins. Empty means the current HEAD.</summary>
    public string StartPoint { get; init; } = "";

    public bool Checkout { get; init; } = true;
}

public sealed record RenameBranchRequest
{
    public string WorktreePath { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

public sealed record DeleteBranchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// Passes <c>-D</c>, which deletes a branch whose commits are on no other branch. The
    /// UI only sets this after git has refused once and the user has been told what it
    /// means — the refusal is the only thing separating tidying up from abandoning work.
    /// </summary>
    public bool Force { get; init; }
}

public sealed record SetUpstreamRequest
{
    public string WorktreePath { get; init; } = "";
    public string Branch { get; init; } = "";

    /// <summary>Empty removes the tracking configuration rather than setting it to nothing.</summary>
    public string Upstream { get; init; } = "";
}

public sealed record StashPushRequest
{
    public string WorktreePath { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>Sweeps up files git does not track, which it otherwise leaves in the tree.</summary>
    public bool IncludeUntracked { get; init; }

    /// <summary>Stashes everything but leaves the staged changes in place as well.</summary>
    public bool KeepIndex { get; init; }
}

/// <summary>
/// Acting on one stash entry.
///
/// Both fields are required together and neither is sufficient. <see cref="Index"/> is what
/// git's command line takes; <see cref="Sha"/> is what identifies the entry, because the
/// stash is shared across every worktree in the repository and the indices shift whenever
/// any of them stashes. The backend refuses when the entry at that index is no longer the
/// object the UI displayed.
/// </summary>
public sealed record StashEntryRequest
{
    public string WorktreePath { get; init; } = "";
    public int Index { get; init; }
    public string Sha { get; init; } = "";
}

public sealed record CreateTagRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Non-empty makes the tag annotated — git's own rule, since <c>-m</c> implies <c>-a</c>.</summary>
    public string Message { get; init; } = "";

    /// <summary>The revision to tag. Empty means HEAD.</summary>
    public string Target { get; init; } = "";
}

public sealed record DeleteTagRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>One page of the commit log for a worktree.</summary>
public sealed record HistoryRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Number of commits already shown. Pages are newest-first.</summary>
    public int Offset { get; init; }

    /// <summary>Requested page size; the backend clamps it to a safe range.</summary>
    public int Limit { get; init; } = Git.HistoryService.DefaultPageSize;

    /// <summary>
    /// Full object id to keep pagination stable while new commits arrive. Empty anchors the
    /// first page at the worktree's current <c>HEAD</c>.
    /// </summary>
    public string Anchor { get; init; } = "";
}

/// <summary>Searches one field of the commits reachable from a worktree.</summary>
public sealed record HistorySearchRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Message, author, changed path, or exact changed content.</summary>
    public Git.HistorySearchKind Kind { get; init; } = Git.HistorySearchKind.Message;

    public string Query { get; init; } = "";

    /// <summary>Number of matching commits already shown. Pages are newest-first.</summary>
    public int Offset { get; init; }

    public int Limit { get; init; } = Git.HistoryService.DefaultPageSize;

    /// <summary>Full object id captured from the first page, for stable later pages.</summary>
    public string Anchor { get; init; } = "";
}

/// <summary>Loads the files changed by one history entry.</summary>
public sealed record CommitDetailRequest
{
    public string WorktreePath { get; init; } = "";
    public string Sha { get; init; } = "";

    /// <summary>Zero is the first parent; merge commits may choose another parent.</summary>
    public int ParentIndex { get; init; }
}

/// <summary>Loads one file's two sides from a historical commit.</summary>
public sealed record CommitFileDiffRequest
{
    public string WorktreePath { get; init; } = "";
    public string Sha { get; init; } = "";
    public string Path { get; init; } = "";
    public int ParentIndex { get; init; }
}

/// <summary>Loads the commits which touched one repository-relative path.</summary>
public sealed record FileHistoryRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public int Offset { get; init; }
    public int Limit { get; init; } = Git.HistoryService.DefaultPageSize;
    public string Anchor { get; init; } = "";
}

/// <summary>Loads line attribution for a working-tree or historical file.</summary>
public sealed record BlameRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Empty means the current working tree; otherwise a full commit id.</summary>
    public string Revision { get; init; } = "";
}

/// <summary>
/// Applies one commit selected from the history overlay.
///
/// <see cref="ParentIndex"/> is zero-based to match the parent picker in the UI. It is
/// only meaningful for a merge commit; the backend translates it to Git's one-based
/// <c>-m</c> argument and rejects an index that is not present.
/// </summary>
public sealed record HistoryMutationRequest
{
    public string WorktreePath { get; init; } = "";
    public string Sha { get; init; } = "";
    public int ParentIndex { get; init; }
}

/// <summary>Cherry-pick request kept as a named protocol shape for API consumers.</summary>
public sealed record CherryPickRequest
{
    public string WorktreePath { get; init; } = "";
    public string Sha { get; init; } = "";
    public int ParentIndex { get; init; }
}

/// <summary>Revert request kept as a named protocol shape for API consumers.</summary>
public sealed record RevertRequest
{
    public string WorktreePath { get; init; } = "";
    public string Sha { get; init; } = "";
    public int ParentIndex { get; init; }
}

/// <summary>Reads the commits after an optional base for the interactive rebase planner.</summary>
public sealed record RebasePlanRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Full commit id. Empty means rebase from the repository root.</summary>
    public string Upstream { get; init; } = "";
}

/// <summary>One row sent back by the rebase planner.</summary>
public sealed record RebaseEntryRequest
{
    public string Sha { get; init; } = "";
    public Git.RebaseAction Action { get; init; } = Git.RebaseAction.Pick;
    public string Message { get; init; } = "";
}

/// <summary>Starts an interactive rebase from a plan captured by the client.</summary>
public sealed record StartRebaseRequest
{
    public string WorktreePath { get; init; } = "";
    public string Upstream { get; init; } = "";
    public string ExpectedHead { get; init; } = "";
    public IReadOnlyList<RebaseEntryRequest> Entries { get; init; } = [];
}

/// <summary>Continues a paused rebase, optionally replacing the next commit message.</summary>
public sealed record ContinueRebaseRequest
{
    public string WorktreePath { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record RebaseStateRequest
{
    public string WorktreePath { get; init; } = "";
}

/// <summary>Reads the shared conflict-resolution surface for a worktree.</summary>
public sealed record ConflictStateRequest
{
    public string WorktreePath { get; init; } = "";
}

/// <summary>Loads the three index stages and marker regions for one path.</summary>
public sealed record ConflictFileRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
}

/// <summary>Chooses a side (or supplies manual text) for one conflicted path.</summary>
public sealed record ResolveConflictRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public Git.ConflictResolutionAction Action { get; init; } = Git.ConflictResolutionAction.Manual;
    public string ManualText { get; init; } = "";
    /// <summary>Optional zero-based marker region. Empty means resolve the whole file.</summary>
    public int Region { get; init; } = -1;
    /// <summary>Fingerprint of the working text that the user saw.</summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>Marks one conflict resolved, or every conflict when <see cref="Path"/> is empty.</summary>
public sealed record MarkResolvedRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
}

/// <summary>Continues, skips or aborts the operation currently paused in Git.</summary>
public sealed record ConflictOperationRequest
{
    public string WorktreePath { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>Forgets Git's recorded conflict resolutions for the supplied paths.</summary>
public sealed record ForgetRerereRequest
{
    public string WorktreePath { get; init; } = "";
    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>Plan data returned to the history planner.</summary>
public sealed record RebasePlanPayload
{
    public required string WorktreePath { get; init; }
    public required string Upstream { get; init; }
    public required string Head { get; init; }
    public string? Branch { get; init; }
    public required IReadOnlyList<Git.RebaseTodoEntry> Entries { get; init; }
    public bool IsRoot { get; init; }
    public bool ContainsMerges { get; init; }
    public bool HasCommits { get; init; }
    public string? UnavailableReason { get; init; }

    public static RebasePlanPayload From(Git.RebasePlan plan) => new()
    {
        WorktreePath = plan.WorktreePath,
        Upstream = plan.Upstream,
        Head = plan.Head,
        Branch = plan.Branch,
        Entries = plan.Entries,
        IsRoot = plan.IsRoot,
        ContainsMerges = plan.ContainsMerges,
        HasCommits = plan.HasCommits,
        UnavailableReason = plan.UnavailableReason,
    };
}

/// <summary>Detailed state for the persistent paused-rebase controls.</summary>
public sealed record RebaseStatePayload
{
    public required string WorktreePath { get; init; }
    public Git.RepositoryOperation Operation { get; init; }
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public string? OriginalHead { get; init; }
    public string? CurrentCommit { get; init; }
    public string? CurrentSubject { get; init; }
    public Git.RebaseAction? CurrentAction { get; init; }
    public int? Step { get; init; }
    public int? TotalSteps { get; init; }
    public IReadOnlyList<Git.RebaseTodoEntry> Remaining { get; init; } = [];
    public IReadOnlyList<Git.RebaseTodoEntry> Completed { get; init; } = [];
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public bool CanContinue { get; init; }
    public bool CanSkip { get; init; }
    public bool CanAbort { get; init; }
    public bool IsPaused { get; init; }

    public static RebaseStatePayload From(Git.RebaseState state) => new()
    {
        WorktreePath = state.WorktreePath,
        Operation = state.Operation,
        Branch = state.Branch,
        Upstream = state.Upstream,
        OriginalHead = state.OriginalHead,
        CurrentCommit = state.CurrentCommit,
        CurrentSubject = state.CurrentSubject,
        CurrentAction = state.CurrentAction,
        Step = state.Step,
        TotalSteps = state.TotalSteps,
        Remaining = state.Remaining,
        Completed = state.Completed,
        ConflictedPaths = state.ConflictedPaths,
        CanContinue = state.CanContinue,
        CanSkip = state.CanSkip,
        CanAbort = state.CanAbort,
        IsPaused = state.IsPaused,
    };
}

/// <summary>A marker-delimited region in a conflicted working-tree file.</summary>
public sealed record ConflictRegionPayload
{
    public int StartLine { get; init; }
    public int? BaseLine { get; init; }
    public int SeparatorLine { get; init; }
    public int EndLine { get; init; }
    public string OursText { get; init; } = "";
    public string BaseText { get; init; } = "";
    public string TheirsText { get; init; } = "";

    public static ConflictRegionPayload From(Git.ConflictRegion region) => new()
    {
        StartLine = region.StartLine,
        BaseLine = region.BaseLine,
        SeparatorLine = region.SeparatorLine,
        EndLine = region.EndLine,
        OursText = region.OursText,
        BaseText = region.BaseText,
        TheirsText = region.TheirsText,
    };
}

/// <summary>The three index stages and working text for one conflicted path.</summary>
public sealed record ConflictFilePayload
{
    public required string Path { get; init; }
    public required string Language { get; init; }
    public string? BaseText { get; init; }
    public string? OursText { get; init; }
    public string? TheirsText { get; init; }
    public string WorkingText { get; init; } = "";
    public bool WorkingFileExists { get; init; }
    public bool IsBinary { get; init; }
    public bool CanRoundTrip { get; init; }
    public string Fingerprint { get; init; } = "";
    public IReadOnlyList<ConflictRegionPayload> Regions { get; init; } = [];
    public bool HasBase { get; init; }
    public bool HasOurs { get; init; }
    public bool HasTheirs { get; init; }

    public static ConflictFilePayload From(Git.ConflictFile file) => new()
    {
        Path = file.Path,
        Language = LanguageMap.ForPath(file.Path),
        BaseText = file.BaseText,
        OursText = file.OursText,
        TheirsText = file.TheirsText,
        WorkingText = file.WorkingText,
        WorkingFileExists = file.WorkingFileExists,
        IsBinary = file.IsBinary,
        CanRoundTrip = file.CanRoundTrip,
        Fingerprint = file.Fingerprint,
        Regions = file.Regions.Select(ConflictRegionPayload.From).ToArray(),
        HasBase = file.HasBase,
        HasOurs = file.HasOurs,
        HasTheirs = file.HasTheirs,
    };
}

/// <summary>Operation state and all files/actions needed to resolve its conflicts.</summary>
public sealed record ConflictStatePayload
{
    public required string WorktreePath { get; init; }
    public Git.RepositoryOperation Operation { get; init; }
    public string? Branch { get; init; }
    public string Description { get; init; } = "clean";
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];
    public IReadOnlyList<ConflictFilePayload> Files { get; init; } = [];
    public bool IsStashRestore { get; init; }
    public string? StashVerb { get; init; }
    public string? StashSha { get; init; }
    public string? OriginalHead { get; init; }
    public string? CurrentCommit { get; init; }
    public string? CurrentSubject { get; init; }
    public Git.RebaseAction? CurrentAction { get; init; }
    public int? Step { get; init; }
    public int? TotalSteps { get; init; }
    public bool HasConflicts { get; init; }
    public bool IsPaused { get; init; }
    public bool CanContinue { get; init; }
    public bool CanSkip { get; init; }
    public bool CanAbort { get; init; }
    public bool CanMarkResolved { get; init; }

    public static ConflictStatePayload From(Git.ConflictState state) => new()
    {
        WorktreePath = state.WorktreePath,
        Operation = state.Operation,
        Branch = state.Branch,
        Description = state.Description,
        ConflictedPaths = state.ConflictedPaths,
        Files = state.Files.Select(ConflictFilePayload.From).ToArray(),
        IsStashRestore = state.IsStashRestore,
        StashVerb = state.StashVerb,
        StashSha = state.StashSha,
        OriginalHead = state.OriginalHead,
        CurrentCommit = state.CurrentCommit,
        CurrentSubject = state.CurrentSubject,
        CurrentAction = state.CurrentAction,
        Step = state.Step,
        TotalSteps = state.TotalSteps,
        HasConflicts = state.HasConflicts,
        IsPaused = state.IsPaused,
        CanContinue = state.CanContinue,
        CanSkip = state.CanSkip,
        CanAbort = state.CanAbort,
        CanMarkResolved = state.CanMarkResolved,
    };
}

// ---------------------------------------------------------------------------
// Remotes
// ---------------------------------------------------------------------------

public sealed record RemoteNameRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record AddRemoteRequest
{
    public string WorktreePath { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed record RenameRemoteRequest
{
    public string WorktreePath { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

public sealed record FetchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Remote { get; init; } = "";
    public bool Prune { get; init; }
    public bool All { get; init; }
}

public sealed record PullRequest
{
    public string WorktreePath { get; init; } = "";
    public string Strategy { get; init; } = "merge";
    public string Remote { get; init; } = "";
    public string Branch { get; init; } = "";
}

public sealed record PushRequest
{
    public string WorktreePath { get; init; } = "";
    public string Remote { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool ForceWithLease { get; init; }
    public bool SetUpstream { get; init; }
}

public sealed record PushTagRequest
{
    public string WorktreePath { get; init; } = "";
    public string Remote { get; init; } = "";
    public string Tag { get; init; } = "";
}

public sealed record CancelRemoteRequest
{
    public string Id { get; init; } = "";
}

// ---------------------------------------------------------------------------
// Detached repository clones
// ---------------------------------------------------------------------------

public sealed record CloneRequest
{
    /// <summary>A repository URL or local path accepted by <c>git clone</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>New, non-existing destination directory.</summary>
    public string Destination { get; init; } = "";

    public bool Bare { get; init; }

    /// <summary>Whether submodules should be cloned recursively; true by default.</summary>
    public bool Recursive { get; init; } = true;
}

public sealed record CancelCloneRequest
{
    public string Id { get; init; } = "";
}

// ---------------------------------------------------------------------------
// Pull requests (GitHub CLI)
// ---------------------------------------------------------------------------

public sealed record PullRequestListRequest
{
    public string WorktreePath { get; init; } = "";
    public int Limit { get; init; } = 100;
}

public sealed record PullRequestRequest
{
    public string WorktreePath { get; init; } = "";
    /// <summary>Number or canonical GitHub pull-request URL. Empty means the current branch.</summary>
    public string Selector { get; init; } = "";
}

public sealed record CreatePullRequestRequest
{
    public string WorktreePath { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string BaseBranch { get; init; } = "";
    public string HeadBranch { get; init; } = "";
    public bool Draft { get; init; }
}

public sealed record PullRequestPayload
{
    public required int Number { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsDraft { get; init; }
    public string Author { get; init; } = "";
    public string HeadRefName { get; init; } = "";
    public string BaseRefName { get; init; } = "";
    public string HeadRepository { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public static PullRequestPayload From(Git.PullRequest value) => new()
    {
        Number = value.Number,
        Url = value.Url,
        Title = value.Title,
        Body = value.Body,
        State = value.State,
        IsDraft = value.IsDraft,
        Author = value.Author,
        HeadRefName = value.HeadRefName,
        BaseRefName = value.BaseRefName,
        HeadRepository = value.HeadRepository,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt,
    };
}

public sealed record PullRequestListPayload
{
    public required string WorktreePath { get; init; }
    public required IReadOnlyList<PullRequestPayload> PullRequests { get; init; }
    public bool Success { get; init; } = true;
    public string Detail { get; init; } = "";
}

public sealed record PullRequestResultPayload
{
    public required string WorktreePath { get; init; }
    public required string Operation { get; init; }
    public bool Success { get; init; }
    public PullRequestPayload? PullRequest { get; init; }
    public string Url { get; init; } = "";
    public string Message { get; init; } = "";
    public Git.GitFailure Failure { get; init; } = Git.GitFailure.None;
    public string CommandLine { get; init; } = "";
    public int ExitCode { get; init; }

    public static PullRequestResultPayload From(Git.PullRequestResult result) => new()
    {
        WorktreePath = result.WorktreePath,
        Operation = result.Operation,
        Success = result.Success,
        PullRequest = result.PullRequest is null ? null : PullRequestPayload.From(result.PullRequest),
        Url = result.PullRequest?.Url ?? "",
        Message = result.Message,
        Failure = result.Failure,
        CommandLine = result.CommandLine,
        ExitCode = result.ExitCode,
    };
}

// ---------------------------------------------------------------------------
// Worktrees
//
// Every request here carries two paths and they mean different things.
// <c>WorktreePath</c> says which repository is being acted on — it is the worktree the panel
// was opened against, and it is checked against the set the app has actually opened, the
// same way every other mutation checks it. <c>Target</c> is the worktree the action is aimed
// at, which is resolved against that repository's own list rather than trusted.
// ---------------------------------------------------------------------------

public sealed record AddWorktreeRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Where the new worktree goes. Relative paths resolve against the main worktree.</summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// The branch to check out, or the name to create when <see cref="CreateBranch"/> is set.
    /// Empty leaves it to git, which names a new branch after the directory.
    /// </summary>
    public string Branch { get; init; } = "";

    public bool CreateBranch { get; init; }

    /// <summary>Where a newly created branch begins. Empty means the current HEAD.</summary>
    public string StartPoint { get; init; } = "";
}

/// <summary>Acting on one worktree of the repository.</summary>
public sealed record WorktreeTargetRequest
{
    public string WorktreePath { get; init; } = "";
    public string Target { get; init; } = "";

    /// <summary>
    /// Passes a single <c>--force</c> to a removal, which covers a worktree containing
    /// modified or untracked files. Never sent before git has refused once and the user has
    /// been told what would be thrown away.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Where a move puts it. Ignored by everything else.</summary>
    public string Destination { get; init; } = "";

    /// <summary>Why a worktree is locked, recorded with the lock for whoever finds it later.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Where a new worktree should go, given what the repository already does.</summary>
public sealed record SuggestPathRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>The branch or worktree name the directory should be called after.</summary>
    public string Name { get; init; } = "";
}

/// <summary>
/// Accepts a linked worktree's committed branch into the repository main worktree.
///
/// <c>WorktreePath</c> identifies the repository context and <c>Target</c> identifies the
/// source worktree, matching the other worktree actions in this section. The aliases are
/// accepted for callers that use more explicit names over the bridge.
/// </summary>
public sealed record AcceptWorkRequest
{
    public string WorktreePath { get; init; } = "";
    public string Target { get; init; } = "";
    public string SourceWorktreePath { get; init; } = "";
    public string RepositoryWorktreePath { get; init; } = "";
    public string Strategy { get; init; } = "merge";
    public string Mode { get; init; } = "";
    public bool RemoveWorktree { get; init; }
    public bool RemoveAfter { get; init; }
    public bool NoFastForward { get; init; }
    public string ExpectedSourceHead { get; init; } = "";
    public string ExpectedTargetHead { get; init; } = "";
}

/// <summary>Reads what rejecting a linked agent worktree would discard.</summary>
public sealed record RejectWorkPreviewRequest
{
    public string WorktreePath { get; init; } = "";
    public string Target { get; init; } = "";
    public string SourceWorktreePath { get; init; } = "";
    public string RepositoryWorktreePath { get; init; } = "";
}

/// <summary>
/// Rejects a linked agent worktree and resets its branch to the resolved base. Expected
/// values come from the preview so a confirmation cannot act on a newer snapshot.
/// </summary>
public sealed record RejectWorkRequest
{
    public string WorktreePath { get; init; } = "";
    public string Target { get; init; } = "";
    public string SourceWorktreePath { get; init; } = "";
    public string RepositoryWorktreePath { get; init; } = "";
    public string ExpectedSourceHead { get; init; } = "";
    public string ExpectedBaseHead { get; init; } = "";
    public string ExpectedSnapshotFingerprint { get; init; } = "";
}

/// <summary>
/// What <c>prune</c> would forget, before it is run.
///
/// The roadmap's dry-run item, in its first use. Prune is the one worktree operation whose
/// effect is invisible beforehand — it acts on administrative files for directories that are
/// already gone — so a list of what it will touch is the only way to answer "what is this
/// button about to do".
/// </summary>
public sealed record PrunePreviewPayload
{
    public required IReadOnlyList<Git.PrunableEntry> Entries { get; init; }
}

/// <summary>
/// What a worktree directory holds, so its removal dialog can name it.
///
/// The paths are capped on this side of the bridge rather than in the panel: a worktree with
/// a `node_modules` in it produces tens of thousands of ignored entries, and the honest
/// summary — a readable list and a count of the rest — is the same regardless of who renders
/// it.
/// </summary>
public sealed record WorktreeRemovalPreviewPayload
{
    public required string Path { get; init; }
    public bool Ok { get; init; }
    public bool Exists { get; init; }
    public bool IsLocked { get; init; }
    public string Branch { get; init; } = "";
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
    public IReadOnlyList<string> UntrackedPaths { get; init; } = [];
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
    public int ChangedCount { get; init; }
    public int UntrackedCount { get; init; }
    public int IgnoredCount { get; init; }
    public string Message { get; init; } = "";

    private const int MaxPaths = 20;

    public static WorktreeRemovalPreviewPayload From(Git.WorktreeRemovalPreview preview) => new()
    {
        Path = preview.Path,
        Ok = preview.Ok,
        Exists = preview.Exists,
        IsLocked = preview.IsLocked,
        Branch = preview.Branch,
        ChangedPaths = [.. preview.ChangedPaths.Take(MaxPaths)],
        UntrackedPaths = [.. preview.UntrackedPaths.Take(MaxPaths)],
        IgnoredPaths = [.. preview.IgnoredPaths.Take(MaxPaths)],
        ChangedCount = preview.ChangedPaths.Count,
        UntrackedCount = preview.UntrackedPaths.Count,
        IgnoredCount = preview.IgnoredPaths.Count,
        Message = preview.Message,
    };
}

/// <summary>Which tracking refs <c>remote prune --dry-run</c> says have gone from the server.</summary>
public sealed record RemotePrunePreviewPayload
{
    public required string Remote { get; init; }
    public bool Ok { get; init; }
    public IReadOnlyList<string> Refs { get; init; } = [];
    public string Message { get; init; } = "";

    public static RemotePrunePreviewPayload From(Git.RemotePrunePreview preview) => new()
    {
        Remote = preview.Remote,
        Ok = preview.Ok,
        Refs = preview.Refs,
        Message = preview.Message,
    };
}

/// <summary>What a push would do to the remote, before anything is sent.</summary>
public sealed record PushPreviewPayload
{
    public bool Ok { get; init; }
    public IReadOnlyList<PushRefUpdatePayload> Updates { get; init; } = [];
    public string Message { get; init; } = "";

    public static PushPreviewPayload From(Git.PushPreview preview) => new()
    {
        Ok = preview.Ok,
        Updates = [.. preview.Updates.Select(PushRefUpdatePayload.From)],
        Message = preview.Message,
    };
}

public sealed record PushRefUpdatePayload
{
    /// <summary>Named FromRef rather than From: the record also needs a static From factory.</summary>
    public required string FromRef { get; init; }
    public required string ToRef { get; init; }
    public bool IsForced { get; init; }
    public bool IsRejected { get; init; }
    public bool IsDeleted { get; init; }
    public string Summary { get; init; } = "";
    public string OldSha { get; init; } = "";
    public string NewSha { get; init; } = "";
    public IReadOnlyList<string> Dropped { get; init; } = [];
    public bool DroppedUnknown { get; init; }

    public static PushRefUpdatePayload From(Git.PushRefUpdate update) => new()
    {
        FromRef = update.From,
        ToRef = update.To,
        IsForced = update.IsForced,
        IsRejected = update.IsRejected,
        IsDeleted = update.IsDeleted,
        Summary = update.Summary,
        OldSha = update.OldSha,
        NewSha = update.NewSha,
        Dropped = update.Dropped,
        DroppedUnknown = update.DroppedUnknown,
    };
}

/// <summary>What a branch delete would leave unreachable.</summary>
public sealed record BranchDeletionPreviewPayload
{
    public required string Branch { get; init; }
    public bool Ok { get; init; }
    public string Tip { get; init; } = "";
    public IReadOnlyList<string> UnreachableCommits { get; init; } = [];
    public string Message { get; init; } = "";

    public static BranchDeletionPreviewPayload From(Git.BranchDeletionPreview preview) => new()
    {
        Branch = preview.Branch,
        Ok = preview.Ok,
        Tip = preview.Tip,
        UnreachableCommits = preview.UnreachableCommits,
        Message = preview.Message,
    };
}

/// <summary>
/// Everything the ref panel renders, in one call.
///
/// One round trip rather than three, because the panel shows all of it at once and every
/// mutation refreshes the lot: a branch delete changes the branch list, a stash-and-switch
/// changes the stash list too, and reconciling three independently-timed replies against one
/// panel is how a list ends up disagreeing with itself.
/// </summary>
public sealed record RefsPayload
{
    public required string WorktreePath { get; init; }
    public required IReadOnlyList<Git.Branch> Branches { get; init; }
    public required IReadOnlyList<Git.Stash> Stashes { get; init; }
    public required IReadOnlyList<Git.Tag> Tags { get; init; }

    public required IReadOnlyList<Git.Remote> Remotes { get; init; }

    /// <summary>
    /// The repository's worktrees, so the panel's fourth section reads from the same call as
    /// the other three.
    ///
    /// The rail asks <c>getWorktrees</c> for the same list, which looks like duplication and
    /// is not: they are two views refreshed at different moments, and the alternative — the
    /// panel reading the rail's copy — would leave it showing worktrees that were removed
    /// through it seconds earlier.
    /// </summary>
    public required IReadOnlyList<Git.Worktree> Worktrees { get; init; }

    /// <summary>
    /// Metadata-only links to local agent sessions. The refs panel uses this to explain which
    /// agent likely produced a worktree; session transcripts remain on disk and are opened
    /// through the host only after a fresh backend lookup.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AgentSessionPayload>> AgentSessions { get; init; } =
        new Dictionary<string, IReadOnlyList<AgentSessionPayload>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The branch this worktree is on, or null when HEAD is detached.</summary>
    public string? Current { get; init; }

    /// <summary>
    /// Whether starting something new is legal right now.
    ///
    /// A switch is <see cref="Git.WriteKind.StartsOperation"/>, so a merge or rebase in
    /// progress forbids it. Answered here so the buttons can be disabled with a reason
    /// rather than each failing individually when pressed.
    /// </summary>
    public bool CanSwitch { get; init; } = true;

    public string? BlockedReason { get; init; }
}

/// <summary>One commit row in the history overlay.</summary>
public sealed record CommitLogEntryPayload
{
    public required string Sha { get; init; }
    public required IReadOnlyList<string> Parents { get; init; }
    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset? AuthoredAt { get; init; }
    public string CommitterName { get; init; } = "";
    public string CommitterEmail { get; init; } = "";
    public DateTimeOffset? CommittedAt { get; init; }
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Decorations { get; init; } = "";
    public bool IsMerge { get; init; }
    public string ShortSha { get; init; } = "";

    public static CommitLogEntryPayload From(Git.CommitLogEntry entry) => new()
    {
        Sha = entry.Sha,
        Parents = entry.Parents,
        AuthorName = entry.AuthorName,
        AuthorEmail = entry.AuthorEmail,
        AuthoredAt = entry.AuthoredAt,
        CommitterName = entry.CommitterName,
        CommitterEmail = entry.CommitterEmail,
        CommittedAt = entry.CommittedAt,
        Subject = entry.Subject,
        Body = entry.Body,
        Decorations = entry.Decorations,
        IsMerge = entry.IsMerge,
        ShortSha = entry.ShortSha,
    };
}

/// <summary>A page of history, with one extra bit of state for a Load more control.</summary>
public sealed record CommitLogPagePayload
{
    public required string WorktreePath { get; init; }
    public required IReadOnlyList<CommitLogEntryPayload> Commits { get; init; }
    public required string Anchor { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required bool HasMore { get; init; }

    public static CommitLogPagePayload From(string worktreePath, Git.CommitLogPage page) => new()
    {
        WorktreePath = worktreePath,
        Commits = page.Commits.Select(CommitLogEntryPayload.From).ToArray(),
        Anchor = page.Anchor,
        Offset = page.Offset,
        Limit = page.Limit,
        HasMore = page.HasMore,
    };
}

/// <summary>The commit metadata and file list shown below a history row.</summary>
public sealed record CommitDetailPayload
{
    public required string WorktreePath { get; init; }
    public required CommitLogEntryPayload Commit { get; init; }
    public required string ParentSha { get; init; }
    public required int ParentIndex { get; init; }
    public required IReadOnlyList<Git.ChangedFile> Files { get; init; }

    public static CommitDetailPayload From(string worktreePath, Git.CommitDetail detail) => new()
    {
        WorktreePath = worktreePath,
        Commit = CommitLogEntryPayload.From(detail.Commit),
        ParentSha = detail.ParentSha,
        ParentIndex = detail.ParentIndex,
        Files = detail.Files,
    };
}

/// <summary>The text sides of a file as it changed in a commit.</summary>
public sealed record CommitFileDiffPayload
{
    public required string WorktreePath { get; init; }
    public required string CommitSha { get; init; }
    public required string ParentSha { get; init; }
    public required int ParentIndex { get; init; }
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string BaseText { get; init; }
    public required string CommitText { get; init; }
    public required string Language { get; init; }
    public bool IsBinary { get; init; }

    public static CommitFileDiffPayload From(
        string worktreePath, Git.CommitFileDiff diff) => new()
    {
        WorktreePath = worktreePath,
        CommitSha = diff.CommitSha,
        ParentSha = diff.ParentSha,
        ParentIndex = diff.ParentIndex,
        Path = diff.Path,
        OldPath = diff.OldPath,
        BaseText = diff.BaseContent.Text,
        CommitText = diff.CommitContent.Text,
        Language = LanguageMap.ForPath(diff.Path),
        IsBinary = diff.IsBinary,
    };
}

/// <summary>A page of commits which touched one path.</summary>
public sealed record FileHistoryPagePayload
{
    public required string WorktreePath { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<CommitLogEntryPayload> Commits { get; init; }
    public required string Anchor { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required bool HasMore { get; init; }

    public static FileHistoryPagePayload From(
        string worktreePath, string path, Git.FileHistoryPage page) => new()
    {
        WorktreePath = worktreePath,
        Path = path,
        Commits = page.Commits.Select(CommitLogEntryPayload.From).ToArray(),
        Anchor = page.Anchor,
        Offset = page.Offset,
        Limit = page.Limit,
        HasMore = page.HasMore,
    };
}

/// <summary>One line of blame attribution.</summary>
public sealed record BlameLinePayload
{
    public required int LineNumber { get; init; }
    public required string Sha { get; init; }
    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset? AuthoredAt { get; init; }
    public string Subject { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsBoundary { get; init; }
    public bool IsUncommitted { get; init; }

    public static BlameLinePayload From(Git.BlameLine line) => new()
    {
        LineNumber = line.LineNumber,
        Sha = line.Sha,
        AuthorName = line.AuthorName,
        AuthorEmail = line.AuthorEmail,
        AuthoredAt = line.AuthoredAt,
        Subject = line.Subject,
        Text = line.Text,
        IsBoundary = line.IsBoundary,
        IsUncommitted = line.IsUncommitted,
    };
}

/// <summary>Blame attribution for one file.</summary>
public sealed record BlamePayload
{
    public required string WorktreePath { get; init; }
    public required string Path { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<BlameLinePayload> Lines { get; init; }
    public bool IsTruncated { get; init; }

    public static BlamePayload From(string worktreePath, Git.BlameResult result) => new()
    {
        WorktreePath = worktreePath,
        Path = result.Path,
        Revision = result.Revision,
        Lines = result.Lines.Select(BlameLinePayload.From).ToArray(),
        IsTruncated = result.IsTruncated,
    };
}

/// <summary>An accepted fetch, pull or push; progress and completion arrive as events.</summary>
public sealed record RemoteOperationStartedPayload
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }
    public required string Operation { get; init; }
}

/// <summary>A live line of transfer progress, or the final mutation result.</summary>
public sealed record RemoteProgressPayload
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }
    public required string Operation { get; init; }
    public required string State { get; init; }
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Percent { get; init; }
    public MutationPayload? Result { get; init; }
}

/// <summary>An accepted clone; progress and completion arrive as events.</summary>
public sealed record CloneOperationStartedPayload
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
}

/// <summary>A live clone line, or the final clone mutation result.</summary>
public sealed record CloneProgressPayload
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required string State { get; init; }
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public int? Percent { get; init; }
    public MutationPayload? Result { get; init; }
    public string? RepositoryPath { get; init; }
}

/// <summary>Asks Claude for a commit message describing what is staged.</summary>
public sealed record GenerateMessageRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Describe the commit an amend would produce, not just what has been staged since.</summary>
    public bool Amend { get; init; }

    /// <summary>
    /// How many alternatives to ask for. One streams; more than one arrives in a single reply,
    /// because three messages appearing a character at a time in three boxes is not a thing
    /// anybody wants to watch.
    /// </summary>
    public int Count { get; init; } = 1;
}

public sealed record CancelGenerationRequest
{
    /// <summary>The id <c>generateCommitMessage</c> returned.</summary>
    public string Id { get; init; } = "";
}

/// <summary>
/// Stores or clears the Claude API key.
///
/// The one message in this protocol carrying a secret. It goes straight into DPAPI-encrypted
/// storage and is never echoed back, never logged, and never written to
/// <c>settings.json</c> — <see cref="Ai.ApiKeyStore"/> has the reasoning.
/// </summary>
public sealed record ApiKeyRequest
{
    /// <summary>Empty forgets the stored key rather than storing an empty one.</summary>
    public string Key { get; init; } = "";
}

/// <summary>The outcome of storing a key — never the key itself.</summary>
public sealed record ApiKeyPayload
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public required Ai.AiAvailability Status { get; init; }
}

/// <summary>An accepted generation. The text arrives as events against this id.</summary>
public sealed record GenerationStartedPayload
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }
}

public sealed record SetThemeRequest
{
    /// <summary>"dark", "light" or "system".</summary>
    public string Theme { get; init; } = "system";
}

// ---------------------------------------------------------------------------
// Response payloads
// ---------------------------------------------------------------------------

/// <summary>The two sides of a file's diff, ready to hand straight to Monaco.</summary>
public sealed record DiffPayload
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string BaseText { get; init; }
    public required string WorkingText { get; init; }

    /// <summary>Monaco language id inferred from the file extension.</summary>
    public required string Language { get; init; }

    public bool IsBinary { get; init; }
    public string Kind { get; init; } = "modified";
}

/// <summary>File metadata for a live comparison between two worktrees.</summary>
public sealed record WorktreeComparisonFilePayload
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string LeftPath { get; init; }
    public required string RightPath { get; init; }
    public Git.ChangeKind Kind { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public bool IsBinary { get; init; }
    public int LeftBytes { get; init; }
    public int RightBytes { get; init; }
    public bool LeftExists { get; init; }
    public bool RightExists { get; init; }
    public required string FileName { get; init; }

    public static WorktreeComparisonFilePayload From(Git.WorktreeComparisonFile file) => new()
    {
        Path = file.Path,
        OldPath = file.OldPath,
        LeftPath = file.LeftPath,
        RightPath = file.RightPath,
        Kind = file.Kind,
        LinesAdded = file.LinesAdded,
        LinesRemoved = file.LinesRemoved,
        IsBinary = file.IsBinary,
        LeftBytes = file.LeftBytes,
        RightBytes = file.RightBytes,
        LeftExists = file.LeftExists,
        RightExists = file.RightExists,
        FileName = file.FileName,
    };
}

/// <summary>The complete changed-file list for two live worktree snapshots.</summary>
public sealed record WorktreeComparisonPayload
{
    public required Git.Worktree Left { get; init; }
    public required Git.Worktree Right { get; init; }
    public required IReadOnlyList<WorktreeComparisonFilePayload> Files { get; init; }
    public int TotalAdded { get; init; }
    public int TotalRemoved { get; init; }

    public static WorktreeComparisonPayload From(Git.WorktreeComparison comparison) => new()
    {
        Left = comparison.Left,
        Right = comparison.Right,
        Files = comparison.Files.Select(WorktreeComparisonFilePayload.From).ToArray(),
        TotalAdded = comparison.TotalAdded,
        TotalRemoved = comparison.TotalRemoved,
    };
}

/// <summary>Both text sides of one cross-worktree file.</summary>
public sealed record WorktreeComparisonContentPayload
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string LeftPath { get; init; }
    public required string RightPath { get; init; }
    public required string LeftText { get; init; }
    public required string RightText { get; init; }
    public required string Language { get; init; }
    public bool LeftExists { get; init; }
    public bool RightExists { get; init; }
    public bool IsBinary { get; init; }
    public int LeftBytes { get; init; }
    public int RightBytes { get; init; }

    public static WorktreeComparisonContentPayload From(Git.WorktreeComparisonContent content) => new()
    {
        Path = content.Path,
        OldPath = content.OldPath,
        LeftPath = content.LeftPath,
        RightPath = content.RightPath,
        LeftText = content.LeftText,
        RightText = content.RightText,
        Language = LanguageMap.ForPath(content.Path),
        LeftExists = content.LeftExists,
        RightExists = content.RightExists,
        IsBinary = content.IsBinary,
        LeftBytes = content.LeftBytes,
        RightBytes = content.RightBytes,
    };
}

/// <summary>The integration and optional cleanup results of accepting an agent worktree.</summary>
public sealed record AcceptWorkPayload
{
    public required string SourceWorktreePath { get; init; }
    public required string TargetWorktreePath { get; init; }
    public required string SourceBranch { get; init; }
    public required string Strategy { get; init; }
    public required MutationPayload Integration { get; init; }
    public MutationPayload? Removal { get; init; }
    public bool RemoveRequested { get; init; }
    public bool Removed { get; init; }
    public bool Ok { get; init; }
    public required string Message { get; init; }

    public static AcceptWorkPayload From(Git.WorktreeAcceptance result) => new()
    {
        SourceWorktreePath = result.SourceWorktreePath,
        TargetWorktreePath = result.TargetWorktreePath,
        SourceBranch = result.SourceBranch,
        Strategy = StrategyName(result.Strategy),
        Integration = MutationPayload.From(result.Integration),
        Removal = result.Removal is null ? null : MutationPayload.From(result.Removal),
        RemoveRequested = result.RemoveRequested,
        Removed = result.Removed,
        Ok = result.Success,
        Message = result.Message,
    };

    private static string StrategyName(Git.WorktreeAcceptStrategy strategy) => strategy switch
    {
        Git.WorktreeAcceptStrategy.CherryPick => "cherryPick",
        _ => "merge",
    };
}

/// <summary>What rejection cleaned and where it reset the source branch.</summary>
public sealed record RejectWorkPayload
{
    public required string SourceWorktreePath { get; init; }
    public required string TargetWorktreePath { get; init; }
    public required string SourceBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string BaseHead { get; init; }
    public required MutationPayload Cleanup { get; init; }
    public required MutationPayload Reset { get; init; }
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
    public bool Ok { get; init; }
    public bool Verified { get; init; }
    public required string Message { get; init; }

    public static RejectWorkPayload From(Git.WorktreeRejection result) => new()
    {
        SourceWorktreePath = result.SourceWorktreePath,
        TargetWorktreePath = result.TargetWorktreePath,
        SourceBranch = result.SourceBranch,
        BaseBranch = result.Preview.BaseBranch,
        BaseHead = result.Preview.BaseHead,
        Cleanup = MutationPayload.From(result.Cleanup),
        Reset = MutationPayload.From(result.Reset),
        IgnoredPaths = result.Preview.IgnoredPaths,
        Ok = result.Success,
        Verified = result.Verified,
        Message = result.Message,
    };
}

/// <summary>Read-only details shown before rejecting a worktree.</summary>
public sealed record RejectWorkPreviewPayload
{
    public required string SourceWorktreePath { get; init; }
    public required string TargetWorktreePath { get; init; }
    public required string SourceBranch { get; init; }
    public required string SourceHead { get; init; }
    public required string BaseBranch { get; init; }
    public required string BaseHead { get; init; }
    public required IReadOnlyList<Git.WorktreeRejectionPath> Paths { get; init; }
    public required IReadOnlyList<string> IgnoredPaths { get; init; }
    public int CommitCount { get; init; }
    public required string SnapshotFingerprint { get; init; }
    public bool Ok { get; init; }
    public required string Message { get; init; }

    public static RejectWorkPreviewPayload From(Git.WorktreeRejectionPreview preview) => new()
    {
        SourceWorktreePath = preview.SourceWorktreePath,
        TargetWorktreePath = preview.TargetWorktreePath,
        SourceBranch = preview.SourceBranch,
        SourceHead = preview.SourceHead,
        BaseBranch = preview.BaseBranch,
        BaseHead = preview.BaseHead,
        Paths = preview.Paths,
        IgnoredPaths = preview.IgnoredPaths,
        CommitCount = preview.CommitCount,
        SnapshotFingerprint = preview.SnapshotFingerprint,
        Ok = preview.Success,
        Message = preview.Message,
    };
}

public sealed record FileContentPayload
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public required string Language { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>The file's encoding on disk, which a save has to reproduce.</summary>
    public Git.FileEncoding Encoding { get; init; } = Git.FileEncoding.Utf8;

    public Git.LineEnding LineEnding { get; init; } = Git.LineEnding.Lf;

    /// <summary>
    /// Whether this content can be written back. False for anything read at a commit and
    /// for binary files — the editor must not offer to save over history.
    /// </summary>
    public bool IsEditable { get; init; }
}

/// <summary>The result of writing a file back to the working tree.</summary>
public sealed record SavePayload
{
    public required string Path { get; init; }
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public int BytesWritten { get; init; }
}

/// <summary>
/// The outcome of a mutation, as the UI needs it: whether it worked, one sentence about
/// why not, and enough classification to decide what to offer next.
/// </summary>
public sealed record MutationPayload
{
    public required string Operation { get; init; }
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public Git.GitFailure Failure { get; init; } = Git.GitFailure.None;
    public string CommandLine { get; init; } = "";
    public int ExitCode { get; init; }
    public int Attempts { get; init; }
    public long ElapsedMs { get; init; }

    public static MutationPayload From(Git.GitMutation mutation) => new()
    {
        Operation = mutation.Operation,
        Ok = mutation.Success,
        Message = mutation.Message,
        Failure = mutation.Failure,
        CommandLine = mutation.CommandLine,
        ExitCode = mutation.ExitCode,
        Attempts = mutation.Attempts,
        ElapsedMs = mutation.ElapsedMs,
    };
}

/// <summary>
/// The commit view: what a commit would take, what it would leave, and whether it may
/// happen at all.
/// </summary>
public sealed record CommitViewPayload
{
    public required string WorktreePath { get; init; }
    public required IReadOnlyList<Git.ChangedFile> Staged { get; init; }
    public required IReadOnlyList<Git.ChangedFile> Unstaged { get; init; }
    public required Git.RepositoryState Repository { get; init; }

    public string? Branch { get; init; }
    public bool IsUnborn { get; init; }

    public bool CanCommit { get; init; }

    /// <summary>Why a commit is refused, or null when it is not.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>Something true and worth saying that is not a refusal — a detached HEAD.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The same three fields answered for an amend, which differs in one place: an amend
    /// needs nothing staged. Both are sent because the amend toggle is client-side, and
    /// asking the backend again on every flip would put a round-trip inside a checkbox.
    /// </summary>
    public bool CanAmend { get; init; }

    public string? AmendBlockedReason { get; init; }

    public string? AmendNote { get; init; }

    /// <summary>Who git will record as the author, so the box can show it before the fact.</summary>
    public string? AuthorName { get; init; }

    public string? AuthorEmail { get; init; }

    /// <summary>Present only for an amend, where the previous message is the starting point.</summary>
    public string? HeadMessage { get; init; }

    public static CommitViewPayload From(Git.CommitView state) => new()
    {
        WorktreePath = state.WorktreePath,
        Staged = state.Staged,
        Unstaged = state.Unstaged,
        Repository = state.Repository,
        Branch = state.Branch,
        IsUnborn = state.IsUnborn,
        CanCommit = state.Readiness.CanCommit,
        BlockedReason = state.Readiness.Reason,
        Note = state.Readiness.Note,
        CanAmend = state.AmendReadiness.CanCommit,
        AmendBlockedReason = state.AmendReadiness.Reason,
        AmendNote = state.AmendReadiness.Note,
    };
}

/// <summary>What is wrong with a commit message, if anything.</summary>
public sealed record MessageReviewPayload
{
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<Git.MessageProblem> Problems { get; init; } = [];
    public string? Type { get; init; }
    public string? Scope { get; init; }
    public bool IsBreaking { get; init; }
    public bool IsEmpty { get; init; }
    public bool HasErrors { get; init; }

    /// <summary>The repository's recent subjects, so the box can show the house style.</summary>
    public IReadOnlyList<string> RecentSubjects { get; init; } = [];

    public static MessageReviewPayload From(
        Git.CommitMessageReview review, IReadOnlyList<string> recentSubjects) => new()
    {
        Subject = review.Subject,
        Body = review.Body,
        Problems = review.Problems,
        Type = review.Type,
        Scope = review.Scope,
        IsBreaking = review.IsBreaking,
        IsEmpty = review.IsEmpty,
        HasErrors = review.HasErrors,
        RecentSubjects = recentSubjects,
    };
}

/// <summary>What undo would do next, so the UI can label the action rather than guess.</summary>
public sealed record UndoPayload
{
    /// <summary>Null when there is nothing recorded for this worktree.</summary>
    public string? Label { get; init; }

    public bool IsDestructive { get; init; }
    public string? Warning { get; init; }

    /// <summary>Recent HEAD movements, which outlive the undo stack and the app itself.</summary>
    public IReadOnlyList<Git.ReflogEntry> Reflog { get; init; } = [];
}

/// <summary>
/// An image referenced by a Markdown document, inlined for the preview.
///
/// The page is served from a virtual host with a strict CSP, so it cannot read files off
/// disk — the backend has to hand the bytes over. <see cref="DataUri"/> is null when the
/// asset could not be supplied, with <see cref="Reason"/> saying why so the preview can
/// render an honest placeholder rather than a broken image.
/// </summary>
public sealed record AssetPayload
{
    public required string Path { get; init; }
    public string? DataUri { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A place in the code — the unit of every navigation result.</summary>
public sealed record SymbolLocation
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }

    /// <summary>Display name, e.g. <c>AgentTurnRunner.RunAsync</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Symbol kind as a Monaco-friendly string: class, method, property…</summary>
    public string Kind { get; init; } = "";

    public string? ContainerName { get; init; }

    /// <summary>Source line text, for preview rows in search and reference lists.</summary>
    public string? Preview { get; init; }
}

public sealed record EditorInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
}

public sealed record IndexStatusPayload
{
    public required string WorktreePath { get; init; }
    public required string State { get; init; }
    public int FilesIndexed { get; init; }
    public int SymbolCount { get; init; }
    public long ElapsedMs { get; init; }
}
