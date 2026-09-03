namespace Chapter.Core.Git;

/// <summary>
/// Git speaks forward slashes everywhere, including on Windows where it also reports
/// drive paths as <c>I:/foo/bar</c>. The filesystem wants backslashes. Every crossing of
/// that boundary goes through here so the conversion happens in exactly one place.
/// </summary>
public static class RepoPaths
{
    /// <summary>Converts a git-reported path to a platform path suitable for file IO.</summary>
    public static string ToPlatform(string gitPath) =>
        Path.DirectorySeparatorChar == '/' ? gitPath : gitPath.Replace('/', '\\');

    /// <summary>Converts a platform path to the forward-slashed form git and the UI use.</summary>
    public static string ToGit(string platformPath) => platformPath.Replace('\\', '/');

    /// <summary>
    /// Joins a worktree root and a repo-relative path into an absolute platform path,
    /// refusing anything that escapes the worktree.
    ///
    /// Two ways out exist without the check, and both are reachable from a path that
    /// arrives over the bridge: <c>Path.Combine</c> discards the root entirely when the
    /// second argument is itself rooted (<c>Combine(@"C:\wt", @"C:\Windows\x")</c> is
    /// <c>C:\Windows\x</c>), and <c>..</c> segments walk upwards.
    /// </summary>
    public static string Resolve(string worktreeRoot, string repoRelativePath)
    {
        var root = Path.GetFullPath(ToPlatform(worktreeRoot));
        var combined = Path.GetFullPath(Path.Combine(root, ToPlatform(repoRelativePath)));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Path '{repoRelativePath}' resolves outside the worktree.", nameof(repoRelativePath));
        }

        return combined;
    }

    /// <summary>
    /// Whether a repo-relative path enters the git administrative directory.
    ///
    /// <see cref="Resolve"/> is not enough on its own for writes. It answers "does this
    /// escape the worktree", and <c>.git/hooks/pre-commit</c> does not escape anything —
    /// it is squarely inside. Writing there is arbitrary code execution the next time
    /// anyone runs git in the repository, and <c>.git/config</c> is the same thing by a
    /// different route (<c>core.pager</c>, <c>core.fsmonitor</c>, an alias). Paths reaching
    /// the write path come from the front-end, which renders content an agent wrote, so
    /// this is reachable rather than theoretical.
    /// </summary>
    public static bool EntersGitDirectory(string repoRelativePath)
    {
        foreach (var segment in repoRelativePath.Split('/', '\\'))
        {
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Git's <c>-z</c> output is NUL-separated with no trailing empty field of interest.
    /// Splitting naively leaves a phantom empty entry that then parses as a bogus record.
    /// </summary>
    public static string[] SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The path out of one <c>status --porcelain=v2 -z</c> record.
    ///
    /// The field count differs per record kind and getting it wrong is quiet rather than
    /// loud: a rename read at the ordinary offset yields <c>R100 NewName.cs</c> and an
    /// unmerged one yields a pair of object ids, both of which look enough like a path to
    /// end up in a dialog. Under <c>-z</c> a rename's original path is a separate record
    /// rather than a tab-separated suffix, and it is dropped by the prefix filters that
    /// select records in the first place.
    /// </summary>
    public static string PathFromStatusRecord(string record)
    {
        if (record.StartsWith("? ", StringComparison.Ordinal) ||
            record.StartsWith("! ", StringComparison.Ordinal))
            return record[2..];

        var fields = record.StartsWith("2 ", StringComparison.Ordinal) ? 9
            : record.StartsWith("u ", StringComparison.Ordinal) ? 10
            : 8;

        var position = 0;
        for (var field = 0; field < fields; field++)
        {
            var space = record.IndexOf(' ', position);
            if (space < 0) return "";
            position = space + 1;
        }

        return position < record.Length ? record[position..] : "";
    }
}
