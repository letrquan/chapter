using System.Globalization;
using System.Collections.Concurrent;

namespace Chapter.Core.Git;

/// <summary>
/// Applies one commit from the history view to the current worktree.
///
/// Cherry-pick and revert cross the boundary between a read-only history list and a
/// repository write. This service validates the displayed commit, uses the guarded writer,
/// and records a safe undo point only when a new commit was actually made.
/// </summary>
public sealed class HistoryMutationService(
    GitWriter writer,
    UndoService undo,
    HistoryService history)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compatibility constructor for hosts that keep the CLI alongside every git service.
    /// The writer and history service already retain the same instance, so the CLI itself is
    /// not needed here; accepting it keeps construction explicit at that seam.
    /// </summary>
    public HistoryMutationService(
        GitCli _,
        GitWriter writer,
        UndoService undo,
        HistoryService history)
        : this(writer, undo, history)
    {
    }

    /// <summary>
    /// Convenience constructor for hosts that have not already registered a history reader.
    /// The workspace uses the overload above so all history calls share its reader, while
    /// this form keeps the service usable on its own in small hosts and tests.
    /// </summary>
    public HistoryMutationService(GitCli git, GitWriter writer, UndoService undo)
        : this(writer, undo, new HistoryService(git))
    {
    }

    /// <summary>Replays one reachable commit on top of the current tip.</summary>
    public Task<GitMutation> CherryPickAsync(
        string worktreePath,
        string sha,
        int parentIndex = 0,
        CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath, sha, parentIndex, cherryPick: true, ct);

    /// <summary>Creates the inverse of one reachable commit on top of the current tip.</summary>
    public Task<GitMutation> RevertAsync(
        string worktreePath,
        string sha,
        int parentIndex = 0,
        CancellationToken ct = default) =>
        RunSerializedAsync(worktreePath, sha, parentIndex, cherryPick: false, ct);

    private async Task<GitMutation> RunSerializedAsync(
        string worktreePath,
        string sha,
        int parentIndex,
        bool cherryPick,
        CancellationToken ct)
    {
        // Two WebView requests can overlap. Serialising per worktree keeps both the HEAD
        // snapshot and the undo-stack order tied to the command that follows them.
        var gate = _gates.GetOrAdd(worktreePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await ApplyAsync(worktreePath, sha, parentIndex, cherryPick, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GitMutation> ApplyAsync(
        string worktreePath,
        string sha,
        int parentIndex,
        bool cherryPick,
        CancellationToken ct)
    {
        var verb = cherryPick ? "cherry-pick" : "revert";
        var requested = $"{verb} {Abbreviate(sha)}";

        CommitLogEntry commit;
        try
        {
            // Do not pass the UI's hash straight to git. This confirms the full object is in
            // this worktree's history before any command can mutate the repository.
            commit = await history.ValidateCommitAsync(worktreePath, sha, ct)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Refused(worktreePath, requested, GitFailure.NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Refused(worktreePath, requested, GitFailure.NotFound, ex.Message);
        }

        int? mainline;
        try
        {
            // The UI numbers parents from zero; Git's -m numbering starts at one. For a
            // normal (or root) commit there is one implicit/default comparison and no -m.
            mainline = HistoryService.MergeMainline(commit, parentIndex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Refused(worktreePath, requested, GitFailure.NotFound, ex.Message);
        }

        // Capture before starting the operation. If it stops on conflicts, no undo point is
        // recorded and the marker files remain for the conflict-resolution phase to handle.
        var previousHead = await undo.CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);
        if (previousHead is null)
        {
            return Refused(
                worktreePath,
                requested,
                GitFailure.WouldLoseChanges,
                "the current HEAD could not be read safely");
        }

        var args = new List<string> { verb, "--no-edit" };
        if (mainline is not null)
        {
            args.Add("-m");
            args.Add(mainline.Value.ToString(CultureInfo.InvariantCulture));
        }
        args.Add(commit.Sha);

        var operation = $"{verb} {commit.ShortSha}";
        var mutation = await writer
            .RunAsync(worktreePath, operation, WriteKind.StartsOperation, ct, [.. args])
            .ConfigureAwait(false);

        if (!mutation.Success) return mutation;

        // A successful command normally creates exactly one commit. Verify that HEAD moved
        // before adding an inverse, so an unusual no-op success cannot reset a later tip.
        await undo.RecordCommitOperationAsync(
            worktreePath, previousHead, verb, commit.Subject, ct).ConfigureAwait(false);

        return mutation;
    }

    private static GitMutation Refused(
        string worktreePath,
        string operation,
        GitFailure failure,
        string reason) => new()
        {
            Operation = operation,
            WorktreePath = worktreePath,
            CommandLine = "",
            ExitCode = -1,
            Failure = failure,
            Detail = $"Could not {operation}: {reason}",
            Attempts = 0,
        };

    private static string Abbreviate(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "commit"
            : sha.Length >= 7 ? sha[..7] : sha;
}
