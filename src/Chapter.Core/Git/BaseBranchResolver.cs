using System.Collections.Concurrent;

namespace Chapter.Core.Git;

/// <summary>
/// Works out what a worktree's changes should be compared against.
///
/// The obvious answer — "diff against main" — fails on real repositories: neither of the
/// repos this was built against has <c>origin/HEAD</c> set, so asking git for the remote's
/// default branch returns an error rather than "main". Hence the fallback chain.
/// </summary>
public sealed class BaseBranchResolver(GitCli git)
{
    /// <summary>Checked in order once origin/HEAD has been ruled out.</summary>
    private static readonly string[] Candidates = ["main", "master", "develop", "trunk"];

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the repository's default branch, or null when there is no plausible
    /// candidate — in which case the UI must ask rather than guess.
    /// </summary>
    public async Task<string?> ResolveDefaultBranchAsync(string worktreePath, CancellationToken ct = default)
    {
        var repoKey = await GetCommonDirAsync(worktreePath, ct).ConfigureAwait(false);
        if (_cache.TryGetValue(repoKey, out var cached)) return cached;

        var resolved = await ProbeDefaultBranchAsync(worktreePath, ct).ConfigureAwait(false);
        _cache[repoKey] = resolved;
        return resolved;
    }

    private async Task<string?> ProbeDefaultBranchAsync(string worktreePath, CancellationToken ct)
    {
        // 1. What the remote says its default is. Correct when set, frequently unset.
        var originHead = await git.TryRunAsync(worktreePath, ct, "symbolic-ref", "--short", "refs/remotes/origin/HEAD")
            .ConfigureAwait(false);
        if (originHead.Success && originHead.Trimmed.Length > 0)
            return originHead.Trimmed;

        // 2. A local branch by conventional name.
        foreach (var candidate in Candidates)
        {
            if (await RefExistsAsync(worktreePath, $"refs/heads/{candidate}", ct).ConfigureAwait(false))
                return candidate;
        }

        // 3. A remote branch by conventional name, for repos with no local default checked out.
        foreach (var candidate in Candidates)
        {
            if (await RefExistsAsync(worktreePath, $"refs/remotes/origin/{candidate}", ct).ConfigureAwait(false))
                return $"origin/{candidate}";
        }

        return null;
    }

    private async Task<bool> RefExistsAsync(string worktreePath, string fullRef, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "show-ref", "--verify", "--quiet", fullRef)
            .ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>
    /// Resolves what to compare against for a given scope.
    ///
    /// Branch and Committed both start from the merge base with the default branch and
    /// differ only in where they end; Uncommitted ignores the branch entirely and compares
    /// HEAD against the working tree.
    /// </summary>
    public async Task<DiffBase> ResolveBaseAsync(
        string worktreePath, DiffScope scope = DiffScope.Branch, CancellationToken ct = default)
    {
        // With no commits at all there is nothing to compare against and no scope means
        // anything different: everything present is new. Every scope collapses to that,
        // rather than each failing separately on an unresolvable HEAD.
        if (!await HasCommitsAsync(worktreePath, ct).ConfigureAwait(false))
        {
            return new DiffBase
            {
                Sha = await EmptyTreeShaAsync(worktreePath, ct).ConfigureAwait(false),
                Description = "no commits yet",
                Scope = scope,
                ToRef = null,
                IncludeUntracked = true,
            };
        }

        return scope switch
        {
            DiffScope.Uncommitted => await ResolveUncommittedAsync(worktreePath, ct).ConfigureAwait(false),
            DiffScope.LastCommit => await ResolveLastCommitAsync(worktreePath, ct).ConfigureAwait(false),
            _ => await ResolveBranchAsync(worktreePath, scope, ct).ConfigureAwait(false),
        };
    }

    private async Task<bool> HasCommitsAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD")
            .ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0;
    }

    private async Task<DiffBase> ResolveBranchAsync(string worktreePath, DiffScope scope, CancellationToken ct)
    {
        // Committed work ends at HEAD; everything else runs on to the working tree. That
        // single difference is what separates "what did it commit" from "what did it do".
        var committedOnly = scope == DiffScope.Committed;
        var toRef = committedOnly ? "HEAD" : null;

        var defaultBranch = await ResolveDefaultBranchAsync(worktreePath, ct).ConfigureAwait(false);

        if (defaultBranch is not null)
        {
            var mergeBase = await git.TryRunAsync(worktreePath, ct, "merge-base", defaultBranch, "HEAD")
                .ConfigureAwait(false);

            if (mergeBase.Success && mergeBase.Trimmed.Length > 0)
            {
                return new DiffBase
                {
                    Sha = mergeBase.Trimmed,
                    Description = committedOnly
                        ? $"committed since {defaultBranch}"
                        : $"merge-base with {defaultBranch}",
                    BranchName = defaultBranch,
                    Scope = scope,
                    ToRef = toRef,
                    IncludeUntracked = !committedOnly,
                };
            }
            // Unrelated histories, or the branch is an orphan. Fall through to HEAD.
        }

        var head = await ResolveHeadAsync(worktreePath, ct).ConfigureAwait(false);
        return new DiffBase
        {
            Sha = head,
            Description = defaultBranch is null ? "HEAD (no default branch found)" : "HEAD (unrelated to default branch)",
            Scope = scope,
            ToRef = toRef,
            IncludeUntracked = !committedOnly,
        };
    }

    private async Task<DiffBase> ResolveUncommittedAsync(string worktreePath, CancellationToken ct) => new()
    {
        Sha = await ResolveHeadAsync(worktreePath, ct).ConfigureAwait(false),
        Description = "uncommitted changes",
        Scope = DiffScope.Uncommitted,
        ToRef = null,
        IncludeUntracked = true,
    };

    private async Task<DiffBase> ResolveLastCommitAsync(string worktreePath, CancellationToken ct)
    {
        var parent = await git.TryRunAsync(worktreePath, ct, "rev-parse", "HEAD~1").ConfigureAwait(false);

        // A root commit has no parent; comparing it against the empty tree is what git
        // itself does, and it shows the commit as all-additions rather than failing.
        var sha = parent.Success && parent.Trimmed.Length > 0
            ? parent.Trimmed
            : await EmptyTreeShaAsync(worktreePath, ct).ConfigureAwait(false);

        return new DiffBase
        {
            Sha = sha,
            Description = "last commit",
            Scope = DiffScope.LastCommit,
            ToRef = "HEAD",
            IncludeUntracked = false,
        };
    }

    /// <summary>
    /// Resolves HEAD, falling back to the empty tree when there is no commit yet.
    ///
    /// Returning the literal "HEAD" here is not safe: in a repository with no commits
    /// <c>git rev-parse HEAD</c> fails, and passing "HEAD" on to <c>git diff</c> fails
    /// again, so a freshly initialised repo or a new orphan branch shows a git error
    /// instead of the files it contains. The empty tree makes every file read as an
    /// addition, which is what those files actually are.
    /// </summary>
    private async Task<string> ResolveHeadAsync(string worktreePath, CancellationToken ct)
    {
        var head = await git.TryRunAsync(worktreePath, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        if (head.Success && head.Trimmed.Length > 0) return head.Trimmed;

        return await EmptyTreeShaAsync(worktreePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The repository's empty-tree object, used as a stand-in base when there is no commit
    /// to compare against. Asked of git rather than hardcoded so it is right for
    /// SHA-256 repositories too.
    /// </summary>
    private async Task<string> EmptyTreeShaAsync(string worktreePath, CancellationToken ct)
    {
        // --stdin with no input: GitCli closes stdin immediately, so git hashes nothing.
        // Reading /dev/null instead would not resolve on Windows.
        var result = await git.TryRunAsync(worktreePath, ct, "hash-object", "-t", "tree", "--stdin")
            .ConfigureAwait(false);

        // The well-known SHA-1 empty tree, for when hash-object is unavailable.
        return result.Success && result.Trimmed.Length > 0
            ? result.Trimmed
            : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    }

    /// <summary>
    /// Path to the shared .git directory. All worktrees of a repo share one, which makes
    /// it the right cache key — resolving the default branch once covers every worktree.
    /// </summary>
    private async Task<string> GetCommonDirAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "rev-parse", "--path-format=absolute", "--git-common-dir")
            .ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : worktreePath;
    }
}
