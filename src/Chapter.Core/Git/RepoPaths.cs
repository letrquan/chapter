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
    /// Git's <c>-z</c> output is NUL-separated with no trailing empty field of interest.
    /// Splitting naively leaves a phantom empty entry that then parses as a bogus record.
    /// </summary>
    public static string[] SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
