namespace Chapter.Core.Git;

/// <summary>
/// Discovers the worktrees belonging to a repository.
/// Handles both layouts seen in the wild: linked worktrees nested inside the repo
/// (<c>heat/.worktrees/work-x</c>) and siblings alongside it (<c>book-review</c> next to
/// <c>book</c>). <c>git worktree list</c> reports both identically, which is exactly why
/// we ask git rather than scanning the filesystem.
/// </summary>
public sealed class WorktreeService(GitCli git)
{
    public async Task<IReadOnlyList<Worktree>> ListAsync(string anyPathInRepo, CancellationToken ct = default)
    {
        var output = await git.RunAsync(anyPathInRepo, ct, "worktree", "list", "--porcelain").ConfigureAwait(false);
        return Parse(output);
    }

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
}
