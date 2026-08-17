using System.Text.Json;
using System.Text.Json.Serialization;

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
