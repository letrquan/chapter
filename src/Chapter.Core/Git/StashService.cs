namespace Chapter.Core.Git;

/// <summary>One entry in the stash.</summary>
public sealed record Stash
{
    /// <summary>
    /// Position in the list, which is what <c>stash@{n}</c> means.
    ///
    /// Deliberately not the identity of the entry — see <see cref="Sha"/>.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// The commit object the entry is, which is its real identity.
    ///
    /// Every command that acts on a stash takes a positional selector, and the positions
    /// shift under any stash made anywhere in the repository. This is what the app checks
    /// the selector against before using it.
    /// </summary>
    public required string Sha { get; init; }

    /// <summary>The message, without git's <c>On &lt;branch&gt;:</c> or <c>WIP on…</c> prefix.</summary>
    public required string Message { get; init; }

    /// <summary>The branch the stash was made on, which git records in the subject.</summary>
    public string? Branch { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string Selector => $"stash@{{{Index}}}";

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>
/// The stash, which in this app is not what it is in other git clients.
///
/// <c>refs/stash</c> is a single ref in the *common* git directory, so every worktree in a
/// repository shares one stash list. A stash made in one worktree appears in all of them,
/// and — the part that matters — <c>stash@{0}</c> renumbers whenever any worktree stashes.
/// An app built around several worktrees being worked in at once therefore cannot treat a
/// positional selector as an identity, which is why every mutation here takes the sha the
/// UI displayed and refuses when the entry at that position is no longer the same object.
/// The alternative is dropping the wrong stash, silently, at exactly the moment the app is
/// most useful.
/// </summary>
public sealed class StashService(GitCli git, GitWriter writer, UndoService undo)
{
    /// <summary>
    /// Separator for the log-family format used by <c>stash list</c> — spelled <c>%x1f</c>
    /// here and <c>%1f</c> in <see cref="BranchService"/>, because <c>for-each-ref</c> is a
    /// different format language. Each spelling prints literally in the other's.
    /// </summary>
    private const char SeparatorChar = '\u001f';

    public async Task<IReadOnlyList<Stash>> ListAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.TryRunAsync(
                worktreePath, ct,
                "stash", "list", "--date=iso-strict", "--format=%H%x1f%gs%x1f%cd")
            .ConfigureAwait(false);

        // A repository with no stash ref at all still exits zero with no output, so a
        // failure here is a real one — and an empty list is the right answer either way.
        return result.Success ? Parse(result.StandardOutput) : [];
    }

    /// <summary>
    /// Parses <c>stash list</c>.
    ///
    /// The subject git stores is <c>On &lt;branch&gt;: &lt;message&gt;</c>, or
    /// <c>WIP on &lt;branch&gt;: &lt;sha&gt; &lt;subject&gt;</c> when no message was given.
    /// Both halves are worth having separately: the branch is how the user tells a stash
    /// from this worktree apart from one an agent left in another.
    /// </summary>
    internal static IReadOnlyList<Stash> Parse(string output)
    {
        var stashes = new List<Stash>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split(SeparatorChar, 3);
            if (fields.Length < 2) continue;

            var (branch, message) = SplitSubject(fields[1]);

            stashes.Add(new Stash
            {
                // Position in the output, which is exactly what stash@{n} counts.
                Index = stashes.Count,
                Sha = fields[0],
                Branch = branch,
                Message = message,
                CreatedAt = fields.Length > 2 ? ParseDate(fields[2]) : null,
            });
        }

        return stashes;
    }

    /// <summary>Splits git's stash subject into the branch it names and the rest.</summary>
    internal static (string? Branch, string Message) SplitSubject(string subject)
    {
        var text = subject;

        // "WIP on main: 1234abc subject" — the auto-generated form, used when `stash push`
        // was given no message.
        var wip = text.StartsWith("WIP on ", StringComparison.Ordinal);
        if (wip) text = text["WIP on ".Length..];
        else if (text.StartsWith("On ", StringComparison.Ordinal)) text = text["On ".Length..];
        else return (null, text);

        var colon = text.IndexOf(':');
        if (colon < 0) return (null, subject);

        var branch = text[..colon];
        var message = text[(colon + 1)..].Trim();

        return (branch.Length > 0 ? branch : null, message);
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when)
            ? when
            : null;

    // -----------------------------------------------------------------------
    // Mutations
    // -----------------------------------------------------------------------

    /// <summary>Stashes the worktree's uncommitted work.</summary>
    /// <param name="includeUntracked">
    /// Sweeps up files git does not track. Off by default, because it is the option that
    /// surprises people: an untracked file removed from the tree by a stash looks exactly
    /// like a file the app deleted.
    /// </param>
    /// <param name="keepIndex">
    /// Leaves the staged changes in place as well as stashing them, so a partially staged
    /// commit can be tested against a clean tree.
    /// </param>
    public Task<GitMutation> PushAsync(
        string worktreePath, string message = "", bool includeUntracked = false, bool keepIndex = false,
        CancellationToken ct = default)
    {
        List<string> args = ["stash", "push"];

        if (includeUntracked) args.Add("--include-untracked");
        if (keepIndex) args.Add("--keep-index");

        // -m last and its value straight after: a message beginning with a dash is
        // otherwise read as an option.
        if (message.Trim().Length > 0)
        {
            args.Add("-m");
            args.Add(message.Trim());
        }

        // StartsOperation, so the guard refuses mid-merge before git does. Git's own
        // refusal there is "error: could not write index", which says nothing about the
        // merge that caused it and nothing about what to do next.
        return writer.RunAsync(
            worktreePath, message.Trim().Length > 0 ? $"stash \"{Shorten(message.Trim())}\"" : "stash changes",
            WriteKind.StartsOperation, ct, [.. args]);
    }

    /// <summary>
    /// Restores a stash, leaving it in the list.
    ///
    /// Apply rather than pop is the safe half of the pair: a conflicted restore keeps the
    /// entry either way, but apply keeps it after a *clean* one too, so nothing is lost if
    /// the result turns out to be wrong.
    /// </summary>
    public Task<GitMutation> ApplyAsync(
        string worktreePath, int index, string expectedSha, CancellationToken ct = default) =>
        RunOnEntryAsync(worktreePath, index, expectedSha, "apply", WriteKind.StartsOperation, ct);

    /// <summary>Restores a stash and drops it, unless restoring hit a conflict.</summary>
    public Task<GitMutation> PopAsync(
        string worktreePath, int index, string expectedSha, CancellationToken ct = default) =>
        RunOnEntryAsync(worktreePath, index, expectedSha, "pop", WriteKind.StartsOperation, ct);

    /// <summary>
    /// Restores a particular stash object, wherever it has since moved to in the list.
    ///
    /// For callers that made a stash themselves and want that one back rather than whatever
    /// is on top now — which is not the same question, because the list is shared with every
    /// other worktree and reorders under them. <c>git stash pop</c> with no argument answers
    /// the second question, and answering it where the first was meant restores somebody
    /// else's work and drops their entry.
    /// </summary>
    public async Task<GitMutation> PopBySha(string worktreePath, string sha, CancellationToken ct = default)
    {
        var entries = await ListAsync(worktreePath, ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(s => string.Equals(s.Sha, sha, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return Refused(worktreePath, $"restore stash {Abbreviate(sha)}",
                "it is no longer in the stash — something else may have applied or dropped it");
        }

        return await PopAsync(worktreePath, entry.Index, entry.Sha, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The entry a <see cref="PushAsync"/> just created, or null when it created none.
    ///
    /// Needed because a push that stashes nothing is *not* a failure: on a clean tree
    /// <c>git stash push</c> prints "No local changes to save" and exits zero, so a caller
    /// checking only the exit code believes it has a stash waiting. Identified by being
    /// absent from the list beforehand and carrying the message the caller chose, so an
    /// entry another worktree added in the same moment is not mistaken for it.
    /// </summary>
    public static Stash? NewEntry(
        IReadOnlyList<Stash> before, IReadOnlyList<Stash> after, string message)
    {
        var seen = before.Select(s => s.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return after.FirstOrDefault(s =>
            !seen.Contains(s.Sha) && string.Equals(s.Message, message, StringComparison.Ordinal));
    }

    private static string Abbreviate(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>
    /// Removes a stash without restoring it.
    ///
    /// Destructive, and the confirmation says so — but *not* permanently, which is the
    /// distinction the confirmation dialog exists to make. A dropped stash is only
    /// unreferenced, not gone: the commit survives until it is garbage-collected, and
    /// <c>stash store</c> puts the entry back with its contents intact. So the sha is kept
    /// on an undo point and the dialog is allowed to promise recovery.
    /// </summary>
    public Task<GitMutation> DropAsync(
        string worktreePath, int index, string expectedSha, CancellationToken ct = default) =>
        RunOnEntryAsync(worktreePath, index, expectedSha, "drop", WriteKind.WorkingTree, ct);

    /// <summary>
    /// Runs a stash command against one entry, after checking the entry is still the one
    /// the user was shown.
    ///
    /// The check is the whole reason this method exists. <c>stash@{2}</c> is a position, and
    /// positions shift under every stash made in *any* worktree of the repository — so
    /// between the list being rendered and a button being pressed, the entry at that index
    /// can be a different piece of work entirely. Dropping the wrong one is unrecoverable
    /// from the UI's point of view, because nothing on screen would show it had happened.
    /// </summary>
    private async Task<GitMutation> RunOnEntryAsync(
        string worktreePath, int index, string expectedSha, string verb, WriteKind kind, CancellationToken ct)
    {
        var operation = $"{verb} stash@{{{index}}}";

        if (index < 0) return Refused(worktreePath, operation, "that is not a stash entry");

        var entries = await ListAsync(worktreePath, ct).ConfigureAwait(false);

        if (index >= entries.Count)
        {
            return Refused(worktreePath, operation,
                entries.Count == 0
                    ? "the stash is empty — it may have been used already"
                    : $"there are only {entries.Count} entries in the stash now");
        }

        var entry = entries[index];

        // Empty skips the check, which is only ever right for a caller that has just read
        // the list itself. Nothing on the bridge does.
        if (expectedSha.Length > 0 && !string.Equals(entry.Sha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(worktreePath, operation,
                "the stash has changed since that list was shown — another worktree may have stashed. " +
                "Look again before choosing.");
        }

        // `apply` is named by sha, which closes the last gap in the check above: even if
        // something stashes between the verification and the command, the object restored is
        // the one that was verified.
        //
        // `pop` and `drop` cannot be. Both remove an entry from the list, which is
        // inherently positional, and git rejects a raw commit for either —
        // "error: '<sha>' is not a stash reference". So they run against the selector, and
        // the sha check above is the whole of their protection rather than a belt to its
        // braces. That is the reason the check re-reads the list immediately beforehand.
        var target = verb == "apply" ? entry.Sha : entry.Selector;

        var mutation = await writer
            .RunAsync(worktreePath, DescribeEntry(verb, entry), kind, ct, ["stash", verb, target])
            .ConfigureAwait(false);

        // A dropped stash is unreferenced rather than gone, and `stash store` re-references
        // it by sha. Recorded only for drop: apply changes nothing about the list, and pop
        // restored the work into the tree, where undoing it would mean discarding what the
        // user just asked to have back.
        if (mutation.Success && verb == "drop")
        {
            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = DescribeEntry("drop", entry),
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                InverseCommand =
                [
                    "stash", "store",
                    "-m", entry.Message.Length > 0 ? entry.Message : $"restored {entry.ShortSha}",
                    entry.Sha,
                ],
                IsDestructive = false,
                // The stash is a ref of its own; HEAD has nothing to do with whether
                // putting it back is correct.
                VerifiesHead = false,
            });
        }

        return mutation;
    }

    private static string DescribeEntry(string verb, Stash entry) =>
        entry.Message.Length > 0 ? $"{verb} stash \"{Shorten(entry.Message)}\"" : $"{verb} {entry.Selector}";

    private static string Shorten(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 40 ? line : line[..37] + "…";
    }

    private static GitMutation Refused(string worktreePath, string operation, string reason) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.NotFound,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    };
}
