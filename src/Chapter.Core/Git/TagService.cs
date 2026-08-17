namespace Chapter.Core.Git;

/// <summary>One ref under <c>refs/tags</c>.</summary>
public sealed record Tag
{
    public required string Name { get; init; }

    /// <summary>
    /// The commit this tag names.
    ///
    /// For an annotated tag this is *not* the ref's own object: the ref points at a tag
    /// object, which in turn points at the commit. Reporting the tag object's sha here
    /// would give a hash that matches nothing in the history the user is looking at.
    /// </summary>
    public required string Sha { get; init; }

    /// <summary>
    /// Annotated tags are objects of their own, carrying a message, a tagger and a date;
    /// lightweight tags are a name pointing straight at a commit and carry nothing.
    /// </summary>
    public bool IsAnnotated { get; init; }

    /// <summary>The tag's own message when annotated, else the commit's subject.</summary>
    public string Subject { get; init; } = "";

    public DateTimeOffset? CreatedAt { get; init; }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>
/// Lists and edits tags.
///
/// Pushing them is deliberately absent: a tag reaches a remote through <c>git push</c>,
/// which is Phase 5's subject and blocked on the credential environment
/// (<see cref="GitCli.AllowCredentialPrompts"/> is still false, so a push that needed
/// credentials would fail opaquely rather than asking). Everything here is local.
/// </summary>
public sealed class TagService(GitCli git, GitWriter writer, UndoService undo)
{
    /// <summary>See <see cref="BranchService.SeparatorChar"/> — this is the same command family.</summary>
    private const string Separator = "%1f";

    public async Task<IReadOnlyList<Tag>> ListAsync(string worktreePath, CancellationToken ct = default)
    {
        var format = string.Join(Separator,
        [
            "%(refname:short)",
            "%(objecttype)",
            "%(objectname)",
            // Empty for a lightweight tag, and the commit behind the tag object for an
            // annotated one — which is the sha that means anything to the user.
            "%(*objectname)",
            // creatordate rather than taggerdate: the latter is empty for lightweight tags,
            // where the useful date is the commit's.
            "%(creatordate:iso-strict)",
            "%(contents:subject)",
        ]);

        var result = await git.TryRunAsync(
                worktreePath, ct,
                "for-each-ref", "--sort=-creatordate", $"--format={format}", "refs/tags")
            .ConfigureAwait(false);

        return result.Success ? Parse(result.StandardOutput) : [];
    }

    internal static IReadOnlyList<Tag> Parse(string output)
    {
        var tags = new List<Tag>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split(BranchService.SeparatorChar, 6);
            if (fields.Length < 5) continue;

            var annotated = fields[1] == "tag";
            var peeled = fields[3];

            tags.Add(new Tag
            {
                Name = fields[0],
                // The peeled sha for an annotated tag, the ref's own object for a
                // lightweight one — in both cases the commit, which is what a tag means.
                Sha = annotated && peeled.Length > 0 ? peeled : fields[2],
                IsAnnotated = annotated,
                CreatedAt = ParseDate(fields[4]),
                Subject = fields.Length > 5 ? fields[5] : "",
            });
        }

        return tags;
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when)
            ? when
            : null;

    /// <summary>Creates a tag at a revision.</summary>
    /// <param name="message">
    /// Non-empty makes it annotated. That is git's own rule rather than a flag of ours —
    /// <c>-m</c> implies <c>-a</c> — and it is the honest one to expose: the difference
    /// between the two kinds *is* whether there is something to say.
    /// </param>
    /// <param name="target">Empty means HEAD.</param>
    public async Task<GitMutation> CreateAsync(
        string worktreePath, string name, string message = "", string target = "",
        CancellationToken ct = default)
    {
        var invalid = BranchService.Validate(name);
        if (invalid is not null)
        {
            return Refused(worktreePath, $"create tag {name}",
                invalid.Replace("branch", "tag", StringComparison.Ordinal));
        }

        List<string> args = ["tag"];

        if (message.Trim().Length > 0)
        {
            args.Add("-a");
            args.Add("-m");
            args.Add(message.Trim());
        }

        args.Add(name);
        if (target.Length > 0) args.Add(target);

        var mutation = await writer
            .RunAsync(worktreePath, $"create tag {name}", WriteKind.WorkingTree, ct, [.. args])
            .ConfigureAwait(false);

        if (mutation.Success)
        {
            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = $"create tag {name}",
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                InverseCommand = ["tag", "-d", name],
                IsDestructive = false,
                VerifiesHead = false,
            });
        }

        return mutation;
    }

    /// <summary>
    /// Deletes a tag.
    ///
    /// The ref's own object is resolved first, not the commit behind it: recreating an
    /// annotated tag from the commit would silently turn it into a lightweight one, losing
    /// its message and tagger. <c>update-ref</c> restores the exact object either way.
    /// </summary>
    public async Task<GitMutation> DeleteAsync(string worktreePath, string name, CancellationToken ct = default)
    {
        var target = await ResolveAsync(worktreePath, $"refs/tags/{name}", ct).ConfigureAwait(false);

        var mutation = await writer
            .RunAsync(worktreePath, $"delete tag {name}", WriteKind.WorkingTree, ct, ["tag", "-d", name])
            .ConfigureAwait(false);

        if (mutation.Success && target is not null)
        {
            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = $"delete tag {name}",
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                InverseCommand = ["update-ref", $"refs/tags/{name}", target],
                IsDestructive = false,
                VerifiesHead = false,
            });
        }

        return mutation;
    }

    private async Task<string?> ResolveAsync(string worktreePath, string rev, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", rev)
            .ConfigureAwait(false);

        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : null;
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
