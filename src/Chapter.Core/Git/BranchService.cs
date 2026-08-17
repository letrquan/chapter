namespace Chapter.Core.Git;

/// <summary>One ref under <c>refs/heads</c> or <c>refs/remotes</c>.</summary>
public sealed record Branch
{
    /// <summary>Short name — <c>main</c>, or <c>origin/main</c> for a remote-tracking ref.</summary>
    public required string Name { get; init; }

    public required string Sha { get; init; }

    public bool IsRemote { get; init; }

    /// <summary>Checked out in the worktree the list was read from.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// The worktree that has this branch checked out, when any does.
    ///
    /// This is the field that makes a branch list worth showing in this app rather than in
    /// any other: switching to a branch another worktree holds is refused by git, and the
    /// useful response is to go to that worktree instead. Knowing it up front means the UI
    /// can offer that rather than discovering it from a failure.
    /// </summary>
    public string? CheckedOutIn { get; init; }

    /// <summary>Upstream's short name, e.g. <c>origin/main</c>. Null when untracked.</summary>
    public string? Upstream { get; init; }

    /// <summary>
    /// Commits this branch has that its upstream does not, and vice versa.
    ///
    /// Both null when there is no upstream, and both zero when the two agree. These come
    /// from the last fetch rather than from the network — nothing in this phase talks to a
    /// remote, so the counts are as old as the last time something did.
    /// </summary>
    public int? Ahead { get; init; }

    public int? Behind { get; init; }

    /// <summary>The upstream is configured but no longer exists — a deleted remote branch.</summary>
    public bool IsUpstreamGone { get; init; }

    /// <summary>Subject of the commit at the tip, for telling similar branches apart.</summary>
    public string Subject { get; init; } = "";

    public DateTimeOffset? CommittedAt { get; init; }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>Another worktree holds this branch, so this one cannot check it out.</summary>
    public bool IsCheckedOutElsewhere => CheckedOutIn is not null && !IsCurrent;
}

/// <summary>What a switch should do about work that is not committed.</summary>
public enum CheckoutStrategy
{
    /// <summary>
    /// Try it and see. Git carries uncommitted changes across whenever no file differs
    /// between the two branches, which is both the common case and what the user wants;
    /// it costs nothing to attempt and its refusal is specific about why.
    /// </summary>
    Carry,

    /// <summary>Stash first, switch, then restore the stash on the new branch.</summary>
    StashAndSwitch,
}

/// <summary>
/// Lists branches and moves between them.
///
/// Reads are one git process per call, not one per branch: <c>for-each-ref</c> answers
/// name, tip, upstream, ahead/behind and which worktree holds it in a single invocation.
/// A process per row would sit behind a list that refreshes after every mutation.
/// </summary>
/// <param name="stashes">
/// Used by the stash-and-switch path rather than reimplemented there. Every stash mutation
/// has to name the entry it means by sha, because the list is shared with every other
/// worktree in the repository and reorders under them; that rule lives in
/// <see cref="StashService"/>, and a second implementation of stashing here is a second
/// place for it to be got wrong.
/// </param>
public sealed class BranchService(GitCli git, GitWriter writer, UndoService undo, StashService stashes)
{
    /// <summary>
    /// Field separator for <c>for-each-ref</c>, as that command spells it.
    ///
    /// <c>%1f</c> here and <c>%x1f</c> in <see cref="UndoService"/> and
    /// <see cref="StashService"/> is not an inconsistency waiting to be tidied:
    /// <c>for-each-ref</c> and the log formatters are separate format languages, and each
    /// spelling is emitted *literally* by the other. Both produce the same byte, and getting
    /// it wrong fails silently — the separator appears in the output as text and every field
    /// lands in column one.
    /// </summary>
    private const string Separator = "%1f";

    /// <summary>ASCII unit separator: the byte both spellings above produce.</summary>
    internal const char SeparatorChar = '\u001f';

    private const string LocalPrefix = "refs/heads/";
    private const string RemotePrefix = "refs/remotes/";

    /// <summary>
    /// Every local and remote-tracking branch, most recently committed first.
    ///
    /// Ordering is git's rather than ours: a review cockpit is nearly always after the
    /// branch somebody just worked on, and sorting by name buries it among branches from
    /// months ago.
    /// </summary>
    public async Task<IReadOnlyList<Branch>> ListAsync(string worktreePath, CancellationToken ct = default)
    {
        // %(refname) rather than %(refname:short), because the short form cannot be told
        // apart afterwards: a remote arrives as `origin/main` and a local branch may
        // legitimately be called `feature/login`, so the slash settles nothing. The full
        // name carries the namespace, and shortening it here is unambiguous.
        var format = string.Join(Separator,
        [
            "%(refname)",
            "%(objectname)",
            "%(upstream:short)",
            "%(upstream:track)",
            "%(worktreepath)",
            "%(HEAD)",
            "%(committerdate:iso-strict)",
            "%(contents:subject)",
        ]);

        var result = await git.TryRunAsync(
                worktreePath, ct,
                "for-each-ref", "--sort=-committerdate", $"--format={format}",
                "refs/heads", "refs/remotes")
            .ConfigureAwait(false);

        return result.Success ? Parse(result.StandardOutput) : [];
    }

    /// <summary>
    /// Parses the format above.
    ///
    /// The subject is taken as the remainder of the line rather than as a field of its own:
    /// splitting into a fixed count and reading index 7 would truncate any subject that
    /// happened to contain the separator byte.
    /// </summary>
    internal static IReadOnlyList<Branch> Parse(string output)
    {
        var branches = new List<Branch>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split(SeparatorChar, 8);
            if (fields.Length < 7) continue;

            var refName = fields[0];
            var isRemote = refName.StartsWith(RemotePrefix, StringComparison.Ordinal);
            var name = Shorten(refName);

            // `origin/HEAD` is a symbolic pointer at another row in this same list, not a
            // branch. Listed, it is a duplicate that cannot be checked out and whose name
            // means something different from every other row's.
            if (isRemote && name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

            var worktree = fields[4];
            var (ahead, behind, gone) = ParseTrack(fields[3]);

            branches.Add(new Branch
            {
                Name = name,
                Sha = fields[1],
                IsRemote = isRemote,
                IsCurrent = fields[5] == "*",
                CheckedOutIn = worktree.Length > 0 ? RepoPaths.ToPlatform(worktree) : null,
                Upstream = fields[2].Length > 0 ? fields[2] : null,
                Ahead = ahead,
                Behind = behind,
                IsUpstreamGone = gone,
                CommittedAt = ParseDate(fields[6]),
                Subject = fields.Length > 7 ? fields[7] : "",
            });
        }

        return branches;
    }

    private static string Shorten(string refName)
    {
        if (refName.StartsWith(LocalPrefix, StringComparison.Ordinal)) return refName[LocalPrefix.Length..];
        if (refName.StartsWith(RemotePrefix, StringComparison.Ordinal)) return refName[RemotePrefix.Length..];
        return refName;
    }

    /// <summary>
    /// Reads <c>%(upstream:track)</c>: <c>[ahead 3, behind 1]</c>, <c>[gone]</c>, or empty.
    ///
    /// Empty means two different things — no upstream at all, or an upstream that agrees
    /// exactly — so the counts stay null here and the caller separates the two by whether
    /// <c>%(upstream:short)</c> was also empty.
    /// </summary>
    internal static (int? Ahead, int? Behind, bool Gone) ParseTrack(string track)
    {
        if (track.Length == 0) return (null, null, false);
        if (track.Contains("gone", StringComparison.OrdinalIgnoreCase)) return (null, null, true);

        return (ReadCount(track, "ahead "), ReadCount(track, "behind "), false);
    }

    private static int? ReadCount(string track, string label)
    {
        var at = track.IndexOf(label, StringComparison.Ordinal);
        if (at < 0) return null;

        var digits = track[(at + label.Length)..];
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end])) end++;

        return end > 0 && int.TryParse(digits[..end], out var value) ? value : null;
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when)
            ? when
            : null;

    // -----------------------------------------------------------------------
    // Mutations
    //
    // Undo points are recorded only where reversing is worth a button: deleting and
    // renaming. Creating and switching are cheap and lose nothing, and recording them
    // would bury the commit undo — the stack surfaces one action at a time, so anything
    // put on it displaces something the user is more likely to want back.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Switches this worktree to a branch.
    ///
    /// <c>switch</c> rather than <c>checkout</c>: the two overlap, but <c>checkout</c> also
    /// means "restore this path", so a branch name that happens to match a file does
    /// something else entirely under it. <c>switch</c> only ever moves HEAD.
    ///
    /// Nothing about the working tree is checked first. Git carries uncommitted changes
    /// across whenever no file differs between the two branches, and pre-checking "the tree
    /// is dirty" would refuse the switch git would have allowed. The refusal, when it comes,
    /// names the files — and that is what the caller turns into the stash-or-abort choice.
    /// </summary>
    public async Task<GitMutation> SwitchAsync(
        string worktreePath, string branch, CheckoutStrategy strategy = CheckoutStrategy.Carry,
        CancellationToken ct = default)
    {
        var target = await ResolveSwitchTargetAsync(worktreePath, branch, ct).ConfigureAwait(false);

        if (strategy is CheckoutStrategy.StashAndSwitch)
            return await StashAndSwitchAsync(worktreePath, target, ct).ConfigureAwait(false);

        // `--` so a branch whose name looks like an option cannot become one.
        return await writer
            .RunAsync(worktreePath, $"switch to {target}", WriteKind.StartsOperation, ct,
                ["switch", "--", target])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Turns what the user clicked into something <c>git switch</c> accepts.
    ///
    /// Clicking a remote row means "work on that branch", but the name it displays is not a
    /// branch git will check out: <c>git switch origin/main</c> fails outright with "a branch
    /// is expected, got remote branch". The short name is what git accepts, and doing so
    /// creates the local tracking branch — which is exactly what was meant.
    ///
    /// The lookup is not skipped for a name that merely *looks* remote. A local branch may
    /// legitimately be called <c>origin/main</c>, and stripping the prefix off that one would
    /// switch to a different branch than the row that was clicked. So a local ref of the
    /// requested name always wins, and the rewrite happens only when there is no such local
    /// branch and there is a remote-tracking ref by that name.
    /// </summary>
    private async Task<string> ResolveSwitchTargetAsync(string worktreePath, string branch, CancellationToken ct)
    {
        var slash = branch.IndexOf('/');
        if (slash <= 0) return branch;

        if (await RefExistsAsync(worktreePath, $"refs/heads/{branch}", ct).ConfigureAwait(false))
            return branch;

        return await RefExistsAsync(worktreePath, $"refs/remotes/{branch}", ct).ConfigureAwait(false)
            ? branch[(slash + 1)..]
            : branch;
    }

    private async Task<bool> RefExistsAsync(string worktreePath, string fullRef, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "show-ref", "--verify", "--quiet", fullRef)
            .ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>
    /// Stashes, switches, then restores — the option offered when a plain switch is refused.
    ///
    /// Every failure path here has to leave the user's work somewhere they can find it,
    /// because for the moment between the stash and the pop the stash is the only copy:
    ///
    /// <list type="bullet">
    /// <item>the stash itself failing changes nothing, and is reported as-is;</item>
    /// <item>the switch failing restores the stash where it came from, rather than leaving
    /// them on the old branch with an emptied tree;</item>
    /// <item>the restore failing is reported as a *successful* switch whose stash is still
    /// in the stash list — saying "could not switch" there would send them to look for
    /// their changes on the wrong branch.</item>
    /// </list>
    /// </summary>
    private async Task<GitMutation> StashAndSwitchAsync(
        string worktreePath, string branch, CancellationToken ct)
    {
        var operation = $"stash and switch to {branch}";
        var label = $"switching to {branch}";

        // What the stash held beforehand, so the entry this call creates can be told apart
        // from the ones already there — and from any another worktree adds while it runs.
        var before = await stashes.ListAsync(worktreePath, ct).ConfigureAwait(false);

        var stash = await stashes
            .PushAsync(worktreePath, label, includeUntracked: true, ct: ct)
            .ConfigureAwait(false);

        if (!stash.Success) return stash with { Operation = operation };

        var after = await stashes.ListAsync(worktreePath, ct).ConfigureAwait(false);

        // Null means the push stashed nothing, which is a success rather than a failure:
        // `git stash push` on a clean tree prints "No local changes to save" and exits zero.
        // An agent committing the offending change while the confirmation was on screen
        // makes that the ordinary case here, not a rare one — and it is the case where an
        // unqualified `git stash pop` would restore whatever entry happened to be on top and
        // drop it, which for a stash list shared with every other worktree is somebody
        // else's work.
        var ours = StashService.NewEntry(before, after, label);

        var switched = await writer
            .RunAsync(worktreePath, operation, WriteKind.StartsOperation, ct, ["switch", "--", branch])
            .ConfigureAwait(false);

        if (!switched.Success)
        {
            if (ours is null) return switched with { Operation = operation };

            var putBack = await stashes.PopBySha(worktreePath, ours.Sha, ct).ConfigureAwait(false);

            // The compensation can fail too, and in this app that is not far-fetched: an
            // agent writing to the same files in the second between the stash and the pop is
            // the ordinary case. Reporting only the switch failure there would leave the
            // user's work sitting in a stash that nothing on screen mentions.
            return switched with
            {
                Operation = operation,
                Detail = putBack.Success
                    ? null
                    : $"{switched.Message} Your changes could not be put back either — " +
                      $"they are in the stash as \"{label}\".",
            };
        }

        // Nothing was stashed, so there is nothing to carry across. The switch is the whole
        // of the operation and it worked.
        if (ours is null) return switched with { Operation = operation };

        var restored = await stashes.PopBySha(worktreePath, ours.Sha, ct).ConfigureAwait(false);

        if (restored.Success) return restored with { Operation = operation };

        return restored with
        {
            // Deliberately reported as a success: the switch happened, and the only thing
            // that did not is recoverable and named.
            ExitCode = 0,
            Failure = GitFailure.None,
            Operation = operation,
            Detail = $"Switched to {branch}, but the stashed changes did not restore cleanly. " +
                     "They are still in the stash.",
        };
    }

    public async Task<string?> CurrentBranchAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "symbolic-ref", "--quiet", "--short", "HEAD")
            .ConfigureAwait(false);

        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    /// <summary>Creates a branch, optionally switching this worktree to it.</summary>
    /// <param name="startPoint">Where it begins. Empty means the current HEAD.</param>
    public Task<GitMutation> CreateAsync(
        string worktreePath, string name, string startPoint = "", bool checkout = true,
        CancellationToken ct = default)
    {
        var invalid = Validate(name);
        if (invalid is not null) return Task.FromResult(Refused(worktreePath, $"create branch {name}", invalid));

        string[] args = checkout
            ? startPoint.Length > 0 ? ["switch", "--create", name, startPoint] : ["switch", "--create", name]
            : startPoint.Length > 0 ? ["branch", name, startPoint] : ["branch", name];

        return writer.RunAsync(
            worktreePath, $"create branch {name}",
            // Only the checkout half can be refused by an operation in progress; creating a
            // ref while a rebase runs is legal and occasionally exactly what is wanted.
            checkout ? WriteKind.StartsOperation : WriteKind.WorkingTree, ct, args);
    }

    public async Task<GitMutation> RenameAsync(
        string worktreePath, string from, string to, CancellationToken ct = default)
    {
        var invalid = Validate(to);
        if (invalid is not null) return Refused(worktreePath, $"rename branch {from}", invalid);

        var mutation = await writer
            .RunAsync(worktreePath, $"rename {from} to {to}", WriteKind.WorkingTree, ct,
                ["branch", "-m", from, to])
            .ConfigureAwait(false);

        if (mutation.Success)
        {
            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = $"rename {from} to {to}",
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                InverseCommand = ["branch", "-m", to, from],
                IsDestructive = false,
                // Renaming does not move HEAD's commit, so there is nothing for the
                // stack's sha check to compare and nothing it would protect.
                VerifiesHead = false,
            });
        }

        return mutation;
    }

    /// <summary>
    /// Deletes a branch.
    ///
    /// The tip is resolved first so undo can put it back exactly. Git prints the sha it
    /// deleted, but abbreviated — and an abbreviation that is unambiguous today becomes
    /// ambiguous as the repository grows, which is not a property to hang a recovery
    /// command on.
    /// </summary>
    /// <param name="force">
    /// Passes <c>-D</c>. Needed when the branch's commits are on no other branch, which git
    /// refuses under <c>-d</c>. That refusal is worth surfacing rather than pre-empting: it
    /// is the one thing separating "delete a merged branch" from "abandon work".
    /// </param>
    public async Task<GitMutation> DeleteAsync(
        string worktreePath, string name, bool force = false, CancellationToken ct = default)
    {
        var tip = await ResolveAsync(worktreePath, name, ct).ConfigureAwait(false);

        var mutation = await writer
            .RunAsync(worktreePath, $"delete branch {name}", WriteKind.WorkingTree, ct,
                ["branch", force ? "-D" : "-d", name])
            .ConfigureAwait(false);

        if (mutation.Success && tip is not null)
        {
            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = $"delete branch {name}",
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                InverseCommand = ["branch", name, tip],
                // Recreating the branch at its old tip restores every commit that was on
                // it: the objects are still there and the reflog holds the tip regardless.
                // This is why the confirmation for a delete says undoable, not permanent.
                IsDestructive = false,
                VerifiesHead = false,
            });
        }

        return mutation;
    }

    /// <summary>Sets or clears a branch's upstream.</summary>
    /// <param name="upstream">Empty removes the tracking configuration entirely.</param>
    public Task<GitMutation> SetUpstreamAsync(
        string worktreePath, string branch, string upstream, CancellationToken ct = default) =>
        upstream.Length == 0
            ? writer.RunAsync(
                worktreePath, $"stop {branch} tracking", WriteKind.WorkingTree, ct,
                ["branch", "--unset-upstream", branch])
            : writer.RunAsync(
                worktreePath, $"track {upstream} with {branch}", WriteKind.WorkingTree, ct,
                ["branch", $"--set-upstream-to={upstream}", branch]);

    private async Task<string?> ResolveAsync(string worktreePath, string rev, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", rev)
            .ConfigureAwait(false);

        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
    }

    /// <summary>
    /// Rejects a name git would reject, before anything runs.
    ///
    /// One of the few places where checking first beats letting git refuse:
    /// <c>check-ref-format</c>'s message is about ref syntax rather than about the box the
    /// user just typed into, and the useful answer — which character is not allowed — is
    /// the same every time. The rules mirror <c>git-check-ref-format</c> for a single
    /// path component chain, which is what a branch name is.
    /// </summary>
    internal static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a branch needs a name";
        if (name != name.Trim()) return "a branch name cannot start or end with a space";
        if (name.StartsWith('-')) return "a branch name cannot start with a dash";
        if (name.StartsWith('.') || name.Contains("/.", StringComparison.Ordinal))
            return "no part of a branch name can start with a dot";
        if (name.StartsWith('/') || name.EndsWith('/')) return "a branch name cannot start or end with a slash";
        if (name.EndsWith('.')) return "a branch name cannot end with a dot";
        if (name.EndsWith(".lock", StringComparison.Ordinal)) return "a branch name cannot end with .lock";
        if (name.Contains("..", StringComparison.Ordinal)) return "a branch name cannot contain ..";
        if (name.Contains("//", StringComparison.Ordinal)) return "a branch name cannot contain //";
        if (name.Contains("@{", StringComparison.Ordinal)) return "a branch name cannot contain @{";
        if (name == "@") return "a branch cannot be called @";

        foreach (var c in name)
        {
            if (char.IsControl(c))
                return "a branch name cannot contain control characters";

            if (c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return $"a branch name cannot contain {(c == ' ' ? "spaces" : c.ToString())}";
        }

        return null;
    }

    private static GitMutation Refused(string worktreePath, string operation, string reason) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = GitFailure.Unknown,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    };
}
