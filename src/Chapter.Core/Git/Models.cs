namespace Chapter.Core.Git;

/// <summary>A single git worktree — either the main one or a linked one.</summary>
public sealed record Worktree
{
    /// <summary>Absolute path, normalised to the platform's separator.</summary>
    public required string Path { get; init; }

    /// <summary>Commit the worktree is checked out at. Empty for a bare repository.</summary>
    public string Head { get; init; } = "";

    /// <summary>Short branch name, or null when detached or bare.</summary>
    public string? Branch { get; init; }

    public bool IsBare { get; init; }
    public bool IsDetached { get; init; }

    /// <summary>True for the repository's primary worktree — always the first entry git reports.</summary>
    public bool IsMain { get; init; }

    /// <summary>
    /// Set when git considers the worktree removable — most commonly its directory was
    /// deleted without <c>git worktree remove</c>. These must render as disabled rather
    /// than being treated as browsable; one exists in the heat repo right now.
    /// </summary>
    public bool IsPrunable { get; init; }

    public string? PrunableReason { get; init; }
    public bool IsLocked { get; init; }
    public string? LockReason { get; init; }

    /// <summary>Label for the rail: the branch if there is one, else the folder name.</summary>
    public string DisplayName =>
        Branch ?? (IsDetached && Head.Length >= 7 ? $"({Head[..7]})" : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')));

    /// <summary>Whether the working directory is actually present and usable.</summary>
    public bool IsUsable => !IsPrunable && !IsBare && Directory.Exists(Path);
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,

    /// <summary>Present on disk but not tracked by git. Agents create these constantly.</summary>
    Untracked,
}

/// <summary>One file that differs between the comparison base and the working tree.</summary>
public sealed record ChangedFile
{
    /// <summary>Repo-relative path, forward-slashed as git reports it.</summary>
    public required string Path { get; init; }

    /// <summary>Previous path for renames and copies; null otherwise.</summary>
    public string? OldPath { get; init; }

    public required ChangeKind Kind { get; init; }

    /// <summary>Rename/copy similarity percentage where git reported one.</summary>
    public int? Similarity { get; init; }

    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }

    /// <summary>Git reports "-" for numstat on binary files; no line counts are meaningful.</summary>
    public bool IsBinary { get; init; }

    /// <summary>
    /// True when the file has changes that are not committed — staged, unstaged or
    /// untracked. Lets the branch-wide view mark what is still dirty without forcing a
    /// mode switch to find out.
    /// </summary>
    public bool IsUncommitted { get; init; }

    /// <summary>
    /// True when the file has an unresolved merge conflict. Nothing may be committed while
    /// any file is in this state, so the list has to carry it rather than making the UI ask
    /// separately.
    /// </summary>
    public bool IsConflicted { get; init; }

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The path whose content should be read from the base revision.</summary>
    public string BasePath => OldPath ?? Path;

    /// <summary>Whether the base side has content at all.</summary>
    public bool HasBaseSide => Kind is not (ChangeKind.Added or ChangeKind.Untracked);

    /// <summary>Whether the working-tree side has content at all.</summary>
    public bool HasWorkingSide => Kind is not ChangeKind.Deleted;
}

/// <summary>
/// Which slice of a worktree's work to look at.
///
/// These are genuinely different questions when reviewing an agent. "Everything it did on
/// this branch" is the usual one, but "what has it not committed yet" is what you ask when
/// the agent is mid-task or you are about to commit on its behalf.
/// </summary>
public enum DiffScope
{
    /// <summary>Everything on the branch: merge-base to working tree, committed or not.</summary>
    Branch,

    /// <summary>Only what has been committed on the branch: merge-base to HEAD.</summary>
    Committed,

    /// <summary>Only what is not committed: HEAD to working tree, plus untracked files.</summary>
    Uncommitted,

    /// <summary>Only the most recent commit.</summary>
    LastCommit,
}

/// <summary>What a worktree's changes are being compared against.</summary>
public sealed record DiffBase
{
    /// <summary>Resolved commit the comparison starts from.</summary>
    public required string Sha { get; init; }

    /// <summary>Human-readable description, e.g. "merge-base with main".</summary>
    public required string Description { get; init; }

    /// <summary>The branch the base was derived from, when it came from one.</summary>
    public string? BranchName { get; init; }

    public DiffScope Scope { get; init; } = DiffScope.Branch;

    /// <summary>
    /// Revision the comparison ends at, or null to compare against the working tree.
    /// This is what separates "committed only" from "everything".
    /// </summary>
    public string? ToRef { get; init; }

    /// <summary>
    /// Whether untracked files belong in this view. They are working-tree state, so they
    /// have no place in a comparison that ends at a commit.
    /// </summary>
    public bool IncludeUntracked { get; init; } = true;
}

/// <summary>A worktree plus everything the UI needs to render its row and file list.</summary>
public sealed record WorktreeChanges
{
    public required Worktree Worktree { get; init; }
    public required DiffBase Base { get; init; }
    public required IReadOnlyList<ChangedFile> Files { get; init; }

    public int TotalAdded => Files.Sum(f => f.LinesAdded);
    public int TotalRemoved => Files.Sum(f => f.LinesRemoved);
}
