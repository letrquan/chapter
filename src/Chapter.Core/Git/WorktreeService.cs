namespace Chapter.Core.Git;

/// <summary>One entry <c>git worktree prune</c> says it would forget.</summary>
/// <param name="Name">
/// The administrative id under <c>.git/worktrees/</c>, which is the directory's original
/// basename — the only name git offers here, since the directory it referred to is gone.
/// </param>
public sealed record PrunableEntry(string Name, string Reason);

/// <summary>What a worktree directory holds, read before it is offered for deletion.</summary>
public sealed record WorktreeRemovalPreview
{
    public required string Path { get; init; }
    public bool Ok { get; init; }

    /// <summary>False for a prunable record: the directory is already gone.</summary>
    public bool Exists { get; init; }
    public bool IsLocked { get; init; }
    public string Branch { get; init; } = "";
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
    public IReadOnlyList<string> UntrackedPaths { get; init; } = [];
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
    public string Message { get; init; } = "";

    public bool HasContent =>
        ChangedPaths.Count > 0 || UntrackedPaths.Count > 0 || IgnoredPaths.Count > 0;
}

/// <summary>
/// Discovers the worktrees belonging to a repository, and edits the set.
///
/// Handles both layouts seen in the wild: linked worktrees nested inside the repo
/// (<c>heat/.worktrees/work-x</c>) and siblings alongside it (<c>book-review</c> next to
/// <c>book</c>). <c>git worktree list</c> reports both identically, which is exactly why
/// we ask git rather than scanning the filesystem.
///
/// Every mutation here runs in the repository's <b>main</b> worktree rather than in the one
/// the user is looking at. That is not tidiness: <c>git worktree remove</c> will happily
/// delete the directory the command is running in, and a process whose working directory has
/// been deleted is in an unsupported state on POSIX and cannot delete it at all on Windows,
/// where a directory in use as a CWD is undeletable. Running from the main worktree — which
/// git refuses to remove or move — means the host of the command is never the target of it.
/// </summary>
public sealed class WorktreeService(GitCli git, GitWriter writer)
{
    public async Task<IReadOnlyList<Worktree>> ListAsync(string anyPathInRepo, CancellationToken ct = default)
    {
        var output = await git.RunAsync(anyPathInRepo, ct, "worktree", "list", "--porcelain").ConfigureAwait(false);
        var worktrees = Parse(output);

        // `worktree list` deliberately knows nothing about tracking. Read it once for the
        // repository and join by branch instead of spawning a command per rail row.
        var tracking = await ReadTrackingAsync(anyPathInRepo, ct).ConfigureAwait(false);
        return [.. worktrees.Select(worktree =>
        {
            if (worktree.Branch is null || !tracking.TryGetValue(worktree.Branch, out var state))
                return worktree;

            return worktree with
            {
                Upstream = state.Upstream,
                Ahead = state.Ahead,
                Behind = state.Behind,
                IsUpstreamGone = state.Gone,
            };
        })];
    }

    private async Task<IReadOnlyDictionary<string, TrackingState>> ReadTrackingAsync(
        string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(
            worktreePath, ct,
            "for-each-ref", "--format=%(refname:short)%1f%(upstream:short)%1f%(upstream:track)",
            "refs/heads").ConfigureAwait(false);

        if (!result.Success) return new Dictionary<string, TrackingState>(StringComparer.Ordinal);

        var states = new Dictionary<string, TrackingState>(StringComparer.Ordinal);
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split(BranchService.SeparatorChar, 3);
            if (fields.Length < 3 || fields[0].Length == 0) continue;

            var (ahead, behind, gone) = BranchService.ParseTrack(fields[2]);
            if (fields[1].Length > 0 && !gone)
                (ahead, behind) = (ahead ?? 0, behind ?? 0);
            states[fields[0]] = new TrackingState(
                fields[1].Length == 0 ? null : fields[1], ahead, behind, gone);
        }

        return states;
    }

    private sealed record TrackingState(string? Upstream, int? Ahead, int? Behind, bool Gone);

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c>. Records are separated by blank lines;
    /// each is a set of <c>key value</c> lines, with <c>bare</c> and <c>detached</c>
    /// appearing as bare keywords. The first record is always the main worktree.
    /// </summary>
    internal static IReadOnlyList<Worktree> Parse(string porcelain)
    {
        var worktrees = new List<Worktree>();

        string? path = null, head = null, branch = null, prunableReason = null, lockReason = null;
        bool bare = false, detached = false, prunable = false, locked = false;

        void Flush()
        {
            if (path is null) return;

            worktrees.Add(new Worktree
            {
                Path = RepoPaths.ToPlatform(path),
                Head = head ?? "",
                Branch = branch,
                IsBare = bare,
                IsDetached = detached,
                IsMain = worktrees.Count == 0,
                IsPrunable = prunable,
                PrunableReason = prunableReason,
                IsLocked = locked,
                LockReason = lockReason,
            });

            path = head = branch = prunableReason = lockReason = null;
            bare = detached = prunable = locked = false;
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];

            switch (key)
            {
                case "worktree":
                    // A missing blank line before the next record would otherwise merge them.
                    Flush();
                    path = value;
                    break;
                case "HEAD": head = value; break;
                case "branch": branch = ShortenRef(value); break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "prunable": prunable = true; prunableReason = value; break;
                case "locked": locked = true; lockReason = value.Length > 0 ? value : null; break;
            }
        }

        Flush();
        return worktrees;
    }

    private static string ShortenRef(string fullRef) =>
        fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..] : fullRef;

    // -----------------------------------------------------------------------
    // Mutations
    //
    // Nothing here records an undo point, following the rule BranchService set: the stack
    // surfaces one action at a time, so anything put on it displaces something the user is
    // more likely to want back. Every operation below is already reversible by a single
    // visible control — a worktree that was just created has a Remove beside it, a move has
    // a Move back, a lock an Unlock — except removal, which nothing can reverse because the
    // uncommitted files went with the directory. Burying a commit's undo behind "unlock
    // worktree" would buy nothing and cost that.
    //
    // The guard runs as WriteKind.WorkingTree throughout, which is to say it never blocks.
    // That is the honest kind: none of these begins a multi-step operation in the worktree
    // the command runs in, and git happily adds a worktree while another one is mid-rebase.
    // The refusals that matter here — a dirty target, a locked target, the main worktree —
    // are git's, and git's are specific.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a worktree.
    /// </summary>
    /// <param name="path">
    /// Where it goes. Relative paths are resolved against the main worktree here rather than
    /// left to git, so that the path in the operation log is the path that was created.
    /// </param>
    /// <param name="branch">
    /// The branch to check out, or the name to create when <paramref name="createBranch"/>
    /// is set. Empty leaves it to git, which creates a branch named after the directory.
    /// </param>
    /// <param name="startPoint">
    /// Where a newly created branch begins. Empty leaves it to git, which means HEAD — and
    /// HEAD here is the <b>main</b> worktree's, because that is where the command runs, which
    /// is rarely the worktree the user is looking at. That gap is why the front-end asks for
    /// this rather than letting it default.
    /// </param>
    public async Task<GitMutation> AddAsync(
        string anyPathInRepo, string path, string branch = "", bool createBranch = false,
        string startPoint = "", CancellationToken ct = default)
    {
        var leaf = LeafName(path);
        var operation = $"add worktree {leaf}";

        var host = await MainWorktreeAsync(anyPathInRepo, ct).ConfigureAwait(false);
        if (host is null) return Refused(anyPathInRepo, operation, "this repository has no main worktree");

        if (path.Trim().Length == 0) return Refused(host, operation, "a worktree needs a path");

        if (createBranch)
        {
            var invalid = BranchService.Validate(branch);
            if (invalid is not null) return Refused(host, operation, invalid);
        }

        var optionish = Optionish(startPoint, "a start point") ?? Optionish(branch, "a branch name");
        if (optionish is not null) return Refused(host, operation, optionish);

        string absolute;
        try
        {
            absolute = Path.GetFullPath(RepoPaths.ToPlatform(path.Trim()), host);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refused(host, operation, "that is not a usable path");
        }

        // Checked before git rather than after, which is the exception to this codebase's
        // usual rule, and it is earned: `git worktree add -b x <path>` creates the branch
        // *first* and only then discovers the path is taken. The user fixes the path, tries
        // again, and gets "a branch named 'x' already exists" — a second error, about
        // something they never did, left behind by the first attempt. Refusing up front
        // means the retry is the retry they expect.
        var occupied = Occupied(absolute);
        if (occupied is not null) return Refused(host, operation, occupied);

        List<string> args = ["worktree", "add"];
        if (createBranch)
        {
            args.Add("-b");
            args.Add(branch);
        }

        // Absolute by construction above, so it cannot be read as an option — which is what
        // a `--` would have bought, and `git worktree add` does not accept one.
        args.Add(absolute);

        if (createBranch)
        {
            if (startPoint.Trim().Length > 0) args.Add(startPoint.Trim());
        }
        else if (branch.Trim().Length > 0)
        {
            args.Add(branch.Trim());
        }

        return await writer
            .RunAsync(host, operation, WriteKind.WorkingTree, ct, [.. args])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Why a path cannot take a new worktree, or null when it can.
    ///
    /// An empty directory is fine — git accepts one, and a user who made the folder first is
    /// doing something reasonable. A directory that cannot be read at all counts as occupied:
    /// the question being asked is "can git put a checkout here", and "I am not allowed to
    /// look" is a no. Answering it as a refusal keeps this on the path where every failure
    /// gets a sentence and a line in the operation log, rather than escaping as an exception
    /// that reaches the front-end as a raw bridge error.
    /// </summary>
    private static string? Occupied(string absolute)
    {
        if (File.Exists(absolute)) return $"{absolute} is a file";
        if (!Directory.Exists(absolute)) return null;

        try
        {
            return Directory.EnumerateFileSystemEntries(absolute).Any()
                ? $"{absolute} already exists and is not empty"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{absolute} exists and could not be read";
        }
    }

    /// <summary>
    /// Refuses a positional argument that git would read as an option, or null when it is safe.
    ///
    /// Not a theoretical hole: <c>git worktree add</c> parses options wherever they appear,
    /// including after the path. <c>add -b x &lt;path&gt; --force</c> is a *forced* add at
    /// HEAD, not an add starting from a commit-ish called <c>--force</c> — so the user asks
    /// for one thing and git silently runs another, here specifically the one thing this
    /// panel refuses on purpose (a second worktree on a branch another already holds). Git
    /// will not refuse it, because from git's side nothing went wrong, and <c>--</c> is not
    /// accepted by this command. So it is refused before the argument is built. The path is
    /// exempt by being absolute; anything the user typed is not.
    /// </summary>
    private static string? Optionish(string value, string what) =>
        value.Trim().StartsWith('-') ? $"{what} cannot begin with a dash" : null;

    /// <summary>
    /// Removes a worktree.
    /// </summary>
    /// <param name="force">
    /// Passes a single <c>--force</c>, which covers the one refusal worth overriding from a
    /// dialog: the tree contains modified or untracked files. A <i>locked</i> worktree needs
    /// <c>--force --force</c> and deliberately does not get it — a lock exists precisely to
    /// stop this, so the way past one is to unlock it, which is its own visible action.
    /// </param>
    public async Task<GitMutation> RemoveAsync(
        string anyPathInRepo, string target, bool force = false, CancellationToken ct = default)
    {
        var operation = $"remove worktree {LeafName(target)}";

        var (host, worktree, refusal) = await ResolveTargetAsync(anyPathInRepo, target, operation, ct)
            .ConfigureAwait(false);

        if (refusal is not null) return refusal;

        if (worktree!.IsMain)
            return Refused(host!, operation, "the main worktree cannot be removed — it is the repository");

        // Git's ordinary removal check ignores files matched by .gitignore. That is a
        // dangerous surprise for an app whose confirmation explicitly promises to name
        // everything the directory contains: a lone .env or node_modules would otherwise
        // disappear without the second, deliberate force confirmation. Keep the lock as a
        // separate decision first; unlocking is the only honest way past a worktree lock.
        if (worktree.IsLocked)
            return Refused(host!, operation, "the worktree is locked — unlock it before removing");

        if (!force)
        {
            var ignored = await HasIgnoredContentAsync(worktree.Path, ct).ConfigureAwait(false);
            if (!ignored.Success)
                return Refused(host!, operation,
                    "the worktree's ignored files could not be checked safely",
                    GitFailure.Unknown);

            if (ignored.HasIgnored)
                return Refused(host!, operation,
                    "the worktree contains ignored files; use force only after checking them",
                    GitFailure.WouldLoseChanges);
        }

        List<string> args = ["worktree", "remove"];
        if (force) args.Add("--force");
        args.Add(worktree.Path);

        return await writer.RunAsync(host!, operation, WriteKind.WorkingTree, ct, [.. args]).ConfigureAwait(false);
    }

    /// <summary>
    /// What removing a worktree would delete, before the question is asked.
    ///
    /// Git's own refusal is the safety net, and it is a late one: it arrives after the user
    /// has said yes to a dialog that named a directory and nothing inside it. The removal
    /// path already knows that <c>status</c> omits ignored files, and refuses rather than
    /// deleting a <c>.env</c> in silence — this reads the same records so the first dialog
    /// can say what is there instead of the second one saying that something is.
    /// </summary>
    public async Task<WorktreeRemovalPreview> PreviewRemoveAsync(
        string anyPathInRepo, string target, CancellationToken ct = default)
    {
        var worktrees = await ListAsync(anyPathInRepo, ct).ConfigureAwait(false);

        // The same comparison ResolveTargetAsync makes, for the same reason: the front-end
        // supplies the path, and only a worktree this repository admits to may be described.
        var normalised = target.TrimEnd(Path.DirectorySeparatorChar, '/');
        var worktree = worktrees.FirstOrDefault(w =>
            string.Equals(w.Path.TrimEnd(Path.DirectorySeparatorChar, '/'), normalised,
                StringComparison.OrdinalIgnoreCase));

        // A prunable record has no directory left to read. That is not a failed preview: it
        // is the honest answer that there are no bytes to lose.
        if (worktree is null || !Directory.Exists(worktree.Path))
            return new WorktreeRemovalPreview { Path = target, Ok = true, Exists = false };

        GitResult result;
        try
        {
            result = await git.TryRunAsync(
                worktree.Path, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored")
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return new WorktreeRemovalPreview
            {
                Path = worktree.Path,
                Ok = false,
                Exists = true,
                IsLocked = worktree.IsLocked,
                Message = GitCli.RedactText(ex.StandardError).Trim(),
            };
        }

        if (!result.Success)
            return new WorktreeRemovalPreview
            {
                Path = worktree.Path,
                Ok = false,
                Exists = true,
                IsLocked = worktree.IsLocked,
                Message = GitCli.RedactText(result.StandardError).Trim(),
            };

        var changed = new List<string>();
        var untracked = new List<string>();
        var ignored = new List<string>();

        foreach (var record in RepoPaths.SplitNul(result.StandardOutput))
        {
            if (record.StartsWith("! ", StringComparison.Ordinal)) ignored.Add(record[2..]);
            else if (record.StartsWith("? ", StringComparison.Ordinal)) untracked.Add(record[2..]);
            else if (record.StartsWith("1 ", StringComparison.Ordinal) ||
                     record.StartsWith("2 ", StringComparison.Ordinal) ||
                     record.StartsWith("u ", StringComparison.Ordinal))
            {
                var path = RepoPaths.PathFromStatusRecord(record);
                if (path.Length > 0) changed.Add(path);
            }
        }

        return new WorktreeRemovalPreview
        {
            Path = worktree.Path,
            Ok = true,
            Exists = true,
            IsLocked = worktree.IsLocked,
            Branch = worktree.Branch ?? "",
            ChangedPaths = changed,
            UntrackedPaths = untracked,
            IgnoredPaths = ignored,
        };
    }

    /// <summary>
    /// Whether the directory contains bytes Git's normal removal check deliberately omits.
    /// Porcelain-v2's ignored records begin with <c>! </c> and are NUL-terminated, so paths
    /// containing newlines cannot turn the probe into a false negative.
    /// </summary>
    private async Task<(bool Success, bool HasIgnored)> HasIgnoredContentAsync(
        string worktreePath, CancellationToken ct)
    {
        GitResult result;
        try
        {
            result = await git.TryRunAsync(
                worktreePath, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored")
                .ConfigureAwait(false);
        }
        catch (GitException)
        {
            // A failed probe is not evidence of an empty directory. Keep ordinary removal
            // fail-closed when Git cannot inspect the bytes it is about to delete.
            return (false, false);
        }

        if (!result.Success) return (false, false);

        var hasIgnored = result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(record => record.StartsWith("! ", StringComparison.Ordinal));

        return (true, hasIgnored);
    }

    /// <summary>Moves a worktree's directory, keeping its branch and its administrative link.</summary>
    public async Task<GitMutation> MoveAsync(
        string anyPathInRepo, string target, string destination, CancellationToken ct = default)
    {
        var operation = $"move worktree {LeafName(target)}";

        var (host, worktree, refusal) = await ResolveTargetAsync(anyPathInRepo, target, operation, ct)
            .ConfigureAwait(false);

        if (refusal is not null) return refusal;

        if (worktree!.IsMain)
            return Refused(host!, operation, "the main worktree cannot be moved");

        if (destination.Trim().Length == 0) return Refused(host!, operation, "a move needs somewhere to go");

        string absolute;
        try
        {
            absolute = Path.GetFullPath(RepoPaths.ToPlatform(destination.Trim()), host!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refused(host!, operation, "that is not a usable path");
        }

        if (Directory.Exists(absolute) || File.Exists(absolute))
            return Refused(host!, operation, $"{absolute} already exists");

        return await writer
            .RunAsync(host!, operation, WriteKind.WorkingTree, ct, ["worktree", "move", worktree.Path, absolute])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Locks a worktree, which stops <c>prune</c> and <c>move</c> touching it.
    /// </summary>
    /// <param name="reason">
    /// Shown by every other git client that reads the lock, and by this one in the worktree
    /// list. Optional to git; asked for anyway, because a lock nobody can explain later is
    /// an obstacle rather than a safeguard.
    /// </param>
    public async Task<GitMutation> LockAsync(
        string anyPathInRepo, string target, string reason = "", CancellationToken ct = default)
    {
        var operation = $"lock worktree {LeafName(target)}";

        var (host, worktree, refusal) = await ResolveTargetAsync(anyPathInRepo, target, operation, ct)
            .ConfigureAwait(false);

        if (refusal is not null) return refusal;

        List<string> args = ["worktree", "lock"];
        if (reason.Trim().Length > 0)
        {
            args.Add("--reason");
            args.Add(reason.Trim());
        }

        args.Add(worktree!.Path);

        return await writer.RunAsync(host!, operation, WriteKind.WorkingTree, ct, [.. args]).ConfigureAwait(false);
    }

    public async Task<GitMutation> UnlockAsync(
        string anyPathInRepo, string target, CancellationToken ct = default)
    {
        var operation = $"unlock worktree {LeafName(target)}";

        var (host, worktree, refusal) = await ResolveTargetAsync(anyPathInRepo, target, operation, ct)
            .ConfigureAwait(false);

        if (refusal is not null) return refusal;

        return await writer
            .RunAsync(host!, operation, WriteKind.WorkingTree, ct, ["worktree", "unlock", worktree!.Path])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What <c>prune</c> would forget, asked of git rather than worked out from the list.
    ///
    /// The distinction matters because prune has rules of its own. A worktree that is both
    /// missing and <i>locked</i> — the point of locking one that lives on a drive that is not
    /// always mounted — is reported by <c>worktree list</c> without a <c>prunable</c> marker
    /// and is skipped by prune. The two agree there today, and this is a dry run of the
    /// command itself rather than a second implementation of its conditions, which is how
    /// they go on agreeing.
    /// </summary>
    public async Task<IReadOnlyList<PrunableEntry>> PreviewPruneAsync(
        string anyPathInRepo, CancellationToken ct = default)
    {
        // A dry run writes nothing, so it goes through the read path rather than the writer:
        // it has no place in the operation log, which is the record of what the app *did*.
        var result = await git
            .TryRunAsync(anyPathInRepo, ct, "worktree", "prune", "--dry-run", "--verbose")
            .ConfigureAwait(false);

        // Standard **error**, which is not a typo and not obvious: `worktree prune --verbose`
        // reports what it is removing on stderr and leaves stdout empty. Reading stdout gets
        // a confident, permanently empty preview — a dialog that says "nothing to prune"
        // beside a button that then prunes four worktrees. Both streams are parsed, since the
        // match below is anchored and costs nothing if git ever moves the lines.
        return result.Success
            ? ParsePrunePreview(result.StandardError + "\n" + result.StandardOutput)
            : [];
    }

    /// <summary>
    /// Reads <c>Removing worktrees/&lt;name&gt;: &lt;reason&gt;</c>, which is the only shape
    /// <c>prune --verbose</c> emits. Anything else is ignored rather than guessed at.
    /// </summary>
    internal static IReadOnlyList<PrunableEntry> ParsePrunePreview(string output)
    {
        const string prefix = "Removing worktrees/";
        var entries = new List<PrunableEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = line[prefix.Length..];
            var colon = rest.IndexOf(':');

            entries.Add(colon < 0
                ? new PrunableEntry(rest, "")
                : new PrunableEntry(rest[..colon], rest[(colon + 1)..].Trim()));
        }

        return entries;
    }

    public async Task<GitMutation> PruneAsync(string anyPathInRepo, CancellationToken ct = default)
    {
        var host = await MainWorktreeAsync(anyPathInRepo, ct).ConfigureAwait(false);
        if (host is null) return Refused(anyPathInRepo, "prune worktrees", "this repository has no main worktree");

        return await writer
            .RunAsync(host, "prune worktrees", WriteKind.WorkingTree, ct, ["worktree", "prune"])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Where a new worktree should go, following whatever the repository already does.
    ///
    /// Both layouts the app supports are in use in the wild, and neither is better in the
    /// abstract — so the answer is the one that matches the neighbours. Only when there are
    /// no neighbours does this pick, and it picks the sibling layout: a worktree nested
    /// inside the main one appears in that worktree's own <c>git status</c> as an untracked
    /// directory, which in this app means the repository you are reviewing grows a phantom
    /// change that is really another agent's whole checkout.
    /// </summary>
    /// <param name="name">The branch or worktree name the directory should be called after.</param>
    public async Task<string> SuggestPathAsync(
        string anyPathInRepo, string name, CancellationToken ct = default)
    {
        var worktrees = await ListAsync(anyPathInRepo, ct).ConfigureAwait(false);
        var main = worktrees.FirstOrDefault(w => w.IsMain);
        if (main is null) return "";

        // Anything unusable as a directory name — `feature/login` is a perfectly good branch
        // and a nested path — flattened rather than rejected, since this is a suggestion the
        // user is about to see and can edit.
        var leaf = Sanitise(name.Trim().Length > 0 ? name.Trim() : "worktree");

        var root = main.Path.TrimEnd(Path.DirectorySeparatorChar, '/');
        var nested = Path.Combine(root, ".worktrees");

        var nestedHere = worktrees.Any(w =>
            !w.IsMain && w.Path.StartsWith(nested + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var suggestion = nestedHere
            ? Path.Combine(nested, leaf)
            : Path.Combine(Path.GetDirectoryName(root) ?? root, $"{Path.GetFileName(root)}-{leaf}");

        return Unique(suggestion);
    }

    /// <summary>
    /// Steps a suggestion past anything already on disk, so the prefilled path is one that
    /// will actually work rather than one the user has to notice is taken.
    /// </summary>
    private static string Unique(string suggestion)
    {
        var candidate = suggestion;

        for (var n = 2; n < 100 && (Directory.Exists(candidate) || File.Exists(candidate)); n++)
            candidate = $"{suggestion}-{n}";

        return candidate;
    }

    private static string Sanitise(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        return new string(chars).Trim('-', '.');
    }

    /// <summary>
    /// Resolves the repository's main worktree, which is where every mutation here runs.
    /// Null when git cannot be asked at all — a directory that is no longer a repository.
    /// </summary>
    private async Task<string?> MainWorktreeAsync(string anyPathInRepo, CancellationToken ct)
    {
        try
        {
            var worktrees = await ListAsync(anyPathInRepo, ct).ConfigureAwait(false);
            return worktrees.FirstOrDefault(w => w.IsMain)?.Path;
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the target among the repository's own worktrees, and the host to run from.
    ///
    /// Refusing an unknown path matters more here than it looks: every other parameter in
    /// this class is a path chosen by the front-end, and <c>git worktree remove</c> is happy
    /// to be pointed at a worktree of a repository the user never opened. Resolving through
    /// the list means the only removable things are the ones this repository admits to.
    /// </summary>
    private async Task<(string? Host, Worktree? Target, GitMutation? Refusal)> ResolveTargetAsync(
        string anyPathInRepo, string target, string operation, CancellationToken ct)
    {
        IReadOnlyList<Worktree> worktrees;
        try
        {
            worktrees = await ListAsync(anyPathInRepo, ct).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return (null, null, Refused(anyPathInRepo, operation, ex.StandardError.Trim()));
        }

        var host = worktrees.FirstOrDefault(w => w.IsMain)?.Path;
        if (host is null)
            return (null, null, Refused(anyPathInRepo, operation, "this repository has no main worktree"));

        var normalised = target.TrimEnd(Path.DirectorySeparatorChar, '/');

        var worktree = worktrees.FirstOrDefault(w =>
            string.Equals(w.Path.TrimEnd(Path.DirectorySeparatorChar, '/'), normalised,
                StringComparison.OrdinalIgnoreCase));

        return worktree is null
            ? (host, null, Refused(host, operation, "that worktree is not part of this repository"))
            : (host, worktree, null);
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static GitMutation Refused(
        string worktreePath,
        string operation,
        string reason,
        GitFailure failure = GitFailure.Unknown) => new()
    {
        Operation = operation,
        WorktreePath = worktreePath,
        CommandLine = "",
        ExitCode = -1,
        Failure = failure,
        Detail = $"Could not {operation}: {reason}",
        Attempts = 0,
    };
}
