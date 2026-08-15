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

        /// <summary>Every event in this batch arrived while the app was writing.</summary>
        public bool SelfWrite = true;
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

    /// <summary>A coalesced batch of changes to one worktree.</summary>
    /// <param name="SelfOriginated">
    /// True when the app itself made these changes. The distinction only exists because the
    /// app now writes to the worktrees it watches: without it, every commit the app makes
    /// arrives back as if an agent had made it, and the app refreshes in response to itself.
    /// </param>
    public sealed record WorktreeChange(
        string WorktreePath,
        IReadOnlyList<string> Paths,
        ChangeReason Reason,
        bool SelfOriginated);

    /// <summary>Raised after the quiet period with the worktree and the paths that changed.</summary>
    public event Action<WorktreeChange>? Changed;

    // -----------------------------------------------------------------------
    // Self-write suppression
    // -----------------------------------------------------------------------

    /// <summary>
    /// How long after a mutation finishes its file events are still treated as ours.
    ///
    /// Filesystem notifications lag the write that caused them, so a window that closed
    /// with the git process would miss most of them. Kept short because anything an agent
    /// writes inside it is attributed to us too — see <see cref="BeginSelfWrite"/>.
    /// </summary>
    private static readonly TimeSpan SelfWriteGrace = TimeSpan.FromMilliseconds(600);

    private sealed class SelfWriteState
    {
        public int Depth;
        public long EndedAtTicks = long.MinValue;
    }

    private readonly ConcurrentDictionary<string, SelfWriteState> _selfWrites =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks everything the watcher sees in this worktree, until the scope is disposed and
    /// the grace period elapses, as the app's own work.
    ///
    /// The attribution is by time, not by path, because a mutation's real footprint is
    /// unknowable in advance — a checkout rewrites whatever differs between two commits.
    /// An agent writing inside the window is therefore credited to us as well, which is
    /// why the tag is only ever information and never a reason to discard a batch: nothing
    /// downstream may skip work on the strength of it. Consumers invalidate, re-index and
    /// notify exactly as they would for anybody else's write, and use the tag only to say
    /// where a change came from.
    /// </summary>
    public IDisposable BeginSelfWrite(string worktreePath)
    {
        var state = _selfWrites.GetOrAdd(worktreePath, _ => new SelfWriteState());
        lock (state) state.Depth++;

        return new SelfWriteScope(this, worktreePath);
    }

    private void EndSelfWrite(string worktreePath)
    {
        if (!_selfWrites.TryGetValue(worktreePath, out var state)) return;

        lock (state)
        {
            state.Depth--;
            if (state.Depth <= 0)
            {
                state.Depth = 0;
                state.EndedAtTicks = Environment.TickCount64;
            }
        }
    }

    private bool IsSelfWrite(string worktreePath)
    {
        if (!_selfWrites.TryGetValue(worktreePath, out var state)) return false;

        lock (state)
        {
            if (state.Depth > 0) return true;
            if (state.EndedAtTicks == long.MinValue) return false;

            return Environment.TickCount64 - state.EndedAtTicks < SelfWriteGrace.TotalMilliseconds;
        }
    }

    private sealed class SelfWriteScope(WorktreeWatcher watcher, string worktreePath) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // A double dispose would decrement the depth twice and end the window while
            // another mutation is still running inside it.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            watcher.EndSelfWrite(worktreePath);
        }
    }

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

        _selfWrites.TryRemove(worktreePath, out _);
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
        // Read the attribution now rather than at flush time: by the time the quiet period
        // elapses the mutation has long finished, so every batch would look foreign.
        var isSelf = IsSelfWrite(worktreePath);

        var pending = _pending.GetOrAdd(worktreePath, _ => new PendingChange());
        lock (pending)
        {
            record(pending);

            // One foreign event in the batch makes the whole batch foreign. Attribution
            // can only ever cost a redundant refresh in that direction; the other way it
            // would cost a missed one.
            if (!isSelf) pending.SelfWrite = false;

            // Restart the quiet period on every event so a burst reports once, at the end.
            pending.Timer ??= new Timer(_ => Flush(worktreePath), null, Timeout.Infinite, Timeout.Infinite);
            pending.Timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(string worktreePath)
    {
        if (!_pending.TryGetValue(worktreePath, out var pending)) return;

        string[] paths;
        bool gitState, overflowed, selfWrite;

        lock (pending)
        {
            paths = pending.Paths.ToArray();
            gitState = pending.GitStateChanged;
            overflowed = pending.Overflowed;
            selfWrite = pending.SelfWrite;

            pending.Paths.Clear();
            pending.GitStateChanged = false;
            pending.Overflowed = false;
            pending.SelfWrite = true;
        }

        if (paths.Length == 0 && !gitState && !overflowed) return;

        // Overflow outranks everything: once events have been dropped, the path list is
        // known to be incomplete, so reporting it as complete would be worse than useless.
        var reason = overflowed ? ChangeReason.Overflow
            : paths.Length == 0 ? ChangeReason.GitState
            : ChangeReason.Files;

        // An overflow is never treated as ours. The dropped events could have come from
        // anywhere, and claiming a rebuild-everything signal as self-inflicted would skip
        // the one refresh that recovers from it.
        if (overflowed) selfWrite = false;

        Changed?.Invoke(new WorktreeChange(
            worktreePath,
            reason == ChangeReason.Overflow ? [] : paths,
            reason,
            selfWrite));
    }

    /// <summary>
    /// Whether a path is in a directory we never care about. Checked segment by segment
    /// because the interesting exclusions — bin, obj, node_modules — sit at any depth.
    /// </summary>
    private static bool IsIgnored(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return true;

        // The app's own atomic-write scratch file, which has to live beside its target
        // because a rename is only atomic within a directory. Reporting it would send the
        // indexer after a file that exists for milliseconds and is never the one the user
        // is looking at.
        if (relative.EndsWith(WorkingTreeWriter.TempSuffix, StringComparison.OrdinalIgnoreCase)) return true;

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
