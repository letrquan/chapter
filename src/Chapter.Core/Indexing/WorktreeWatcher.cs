using System.Collections.Concurrent;
using Chapter.Core.Git;

namespace Chapter.Core.Indexing;

/// <summary>
/// Watches worktrees for changes so an agent's edits appear without a manual refresh.
///
/// Two things make this survivable in practice. Build output is excluded — an agent
/// running <c>dotnet build</c> writes thousands of files under bin/ and obj/, and watching
/// them would drown the UI in refreshes of files nobody is reviewing. And events are
/// coalesced, because a single save routinely produces several notifications.
/// </summary>
public sealed class WorktreeWatcher : IDisposable
{
    /// <summary>
    /// Quiet period before reporting. Long enough to collapse the burst from one save or
    /// one agent edit, short enough that the UI still feels live.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

    private readonly ConcurrentDictionary<string, WatcherPair> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The working tree, plus the worktree's git administrative directory.
    ///
    /// The second one matters more than it looks: a commit writes nothing in the working
    /// tree, only under the git directory — which the working-tree watcher deliberately
    /// ignores, and which for a linked worktree lives outside the watched root entirely.
    /// Without it the app never notices an agent finishing its work, which is the single
    /// event this whole feature exists to catch.
    /// </summary>
    private sealed record WatcherPair(FileSystemWatcher Tree, FileSystemWatcher? GitDir) : IDisposable
    {
        public void Dispose()
        {
            Tree.EnableRaisingEvents = false;
            Tree.Dispose();

            if (GitDir is null) return;
            GitDir.EnableRaisingEvents = false;
            GitDir.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, PendingChange> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingChange
    {
        public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
        public Timer? Timer;

        /// <summary>Git state moved — a commit, stage, or checkout. Working tree unchanged.</summary>
        public bool GitStateChanged;

        /// <summary>The OS dropped events; what changed is unknown.</summary>
        public bool Overflowed;
    }

    /// <summary>Why a worktree needs re-reading.</summary>
    public enum ChangeReason
    {
        /// <summary>Specific working-tree files changed; the path list is complete.</summary>
        Files,

        /// <summary>A commit, stage or checkout. Diffs are stale, the symbol index is not.</summary>
        GitState,

        /// <summary>Events were dropped. Nothing can be trusted; rebuild.</summary>
        Overflow,
    }

    /// <summary>Raised after the quiet period with the worktree and the paths that changed.</summary>
    public event Action<string, IReadOnlyList<string>, ChangeReason>? Changed;

    /// <param name="gitDir">
    /// The worktree's git administrative directory, from
    /// <c>git rev-parse --absolute-git-dir</c>. Optional, but without it commits, staging
    /// and branch switches go unnoticed.
    /// </param>
    public void Watch(string worktreePath, string? gitDir = null)
    {
        if (_watchers.ContainsKey(worktreePath)) return;

        var root = RepoPaths.ToPlatform(worktreePath);
        if (!Directory.Exists(root)) return;

        FileSystemWatcher tree;
        try
        {
            tree = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                // The default 8KB buffer overflows during a build, and an overflow drops
                // events silently. A larger buffer plus the Error handler below covers it.
                InternalBufferSize = 64 * 1024,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            return; // Path vanished between the check and the construction.
        }

        tree.Created += (_, e) => Queue(worktreePath, root, e.FullPath);
        tree.Changed += (_, e) => Queue(worktreePath, root, e.FullPath);
        tree.Deleted += (_, e) => Queue(worktreePath, root, e.FullPath);
        tree.Renamed += (_, e) =>
        {
            Queue(worktreePath, root, e.OldFullPath);
            Queue(worktreePath, root, e.FullPath);
        };

        // On overflow the OS dropped events, so whatever we collected is incomplete.
        tree.Error += (_, _) => QueueOverflow(worktreePath);

        var pair = new WatcherPair(tree, CreateGitDirWatcher(worktreePath, gitDir));

        if (!_watchers.TryAdd(worktreePath, pair))
        {
            pair.Dispose();
            return;
        }

        tree.EnableRaisingEvents = true;
        if (pair.GitDir is not null) pair.GitDir.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Watches the git directory for the files a commit touches — <c>index</c>, <c>HEAD</c>
    /// and the ref logs. Changes there carry no useful path list, so they report as a
    /// forced flush meaning "re-read this worktree".
    /// </summary>
    private FileSystemWatcher? CreateGitDirWatcher(string worktreePath, string? gitDir)
    {
        if (gitDir is null) return null;

        var path = RepoPaths.ToPlatform(gitDir);
        if (!Directory.Exists(path)) return null;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                InternalBufferSize = 16 * 1024,
            };

            void OnGitChange(object _, FileSystemEventArgs e)
            {
                // Ignore git's own lock and temp churn, which fires constantly during any
                // operation and would otherwise refresh several times per command.
                var name = Path.GetFileName(e.FullPath);
                if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return;

                QueueForcedRefresh(worktreePath);
            }

            watcher.Created += OnGitChange;
            watcher.Changed += OnGitChange;
            watcher.Deleted += OnGitChange;

            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException)
        {
            return null; // Not fatal: working-tree edits are still observed.
        }
    }

    public void Unwatch(string worktreePath)
    {
        if (_watchers.TryRemove(worktreePath, out var pair)) pair.Dispose();

        if (_pending.TryRemove(worktreePath, out var pending))
        {
            pending.Timer?.Dispose();
        }
    }

    private void Queue(string worktreePath, string root, string fullPath)
    {
        if (IsIgnored(root, fullPath)) return;

        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        Schedule(worktreePath, pending => pending.Paths.Add(relative));
    }

    private void QueueForcedRefresh(string worktreePath) =>
        Schedule(worktreePath, pending => pending.GitStateChanged = true);

    private void QueueOverflow(string worktreePath) =>
        Schedule(worktreePath, pending => pending.Overflowed = true);

    private void Schedule(string worktreePath, Action<PendingChange> record)
    {
        var pending = _pending.GetOrAdd(worktreePath, _ => new PendingChange());
        lock (pending)
        {
            record(pending);

            // Restart the quiet period on every event so a burst reports once, at the end.
            pending.Timer ??= new Timer(_ => Flush(worktreePath), null, Timeout.Infinite, Timeout.Infinite);
            pending.Timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(string worktreePath)
    {
        if (!_pending.TryGetValue(worktreePath, out var pending)) return;

        string[] paths;
        bool gitState, overflowed;

        lock (pending)
        {
            paths = pending.Paths.ToArray();
            gitState = pending.GitStateChanged;
            overflowed = pending.Overflowed;

            pending.Paths.Clear();
            pending.GitStateChanged = false;
            pending.Overflowed = false;
        }

        if (paths.Length == 0 && !gitState && !overflowed) return;

        // Overflow outranks everything: once events have been dropped, the path list is
        // known to be incomplete, so reporting it as complete would be worse than useless.
        var reason = overflowed ? ChangeReason.Overflow
            : paths.Length == 0 ? ChangeReason.GitState
            : ChangeReason.Files;

        Changed?.Invoke(worktreePath, reason == ChangeReason.Overflow ? [] : paths, reason);
    }

    /// <summary>
    /// Whether a path is in a directory we never care about. Checked segment by segment
    /// because the interesting exclusions — bin, obj, node_modules — sit at any depth.
    /// </summary>
    private static bool IsIgnored(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return true;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (FileScanner.IsIgnoredDirectory(segment)) return true;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var path in _watchers.Keys.ToArray()) Unwatch(path);
    }
}
