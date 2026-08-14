namespace Chapter.Core.Indexing;

/// <summary>
/// Enumerates source files in a worktree, skipping the directories that would otherwise
/// dominate both the index and the file watcher.
/// </summary>
public static class FileScanner
{
    /// <summary>
    /// Directory names never worth walking. <c>bin</c> and <c>obj</c> matter most: an
    /// agent running a build fills them with generated C# that would double the index and
    /// pollute every navigation result with copies of the real declarations.
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", ".vscode", ".idea",
        "packages", "dist", "build", "out", "target", ".next", ".nuxt",
        "__pycache__", ".venv", "venv", "TestResults", ".worktrees",
    };

    public static bool IsIgnoredDirectory(string name) => IgnoredDirectories.Contains(name);

    /// <summary>
    /// All files under <paramref name="root"/> matching the predicate, as repo-relative
    /// forward-slashed paths.
    /// </summary>
    public static List<string> Enumerate(string root, Func<string, bool> matches, CancellationToken ct = default)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue; // A directory we cannot read should not abort the whole scan.
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    if (!IgnoredDirectories.Contains(name)) stack.Push(entry);
                    continue;
                }

                if (!matches(name)) continue;

                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                results.Add(relative);
            }
        }

        return results;
    }
}
