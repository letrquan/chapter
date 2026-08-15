using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Chapter.Core.Git;

/// <summary>A process holding a git lock file open.</summary>
public sealed record LockHolder(int ProcessId, string Name)
{
    public override string ToString() => $"{Name} (pid {ProcessId})";
}

/// <summary>
/// What is known about a lock file that blocked a mutation.
///
/// The distinction that matters to the user is between contention and wreckage: another
/// git working normally will release the lock in a moment, while a lock left behind by a
/// crashed process will never be released and needs deleting by hand. Nothing here deletes
/// it — that is a destructive act on a file the app did not create, and guessing wrong
/// while a real git is mid-write corrupts the index it was writing.
/// </summary>
public sealed record LockInfo
{
    public required string Path { get; init; }

    /// <summary>False when the lock was released between git failing and this lookup.</summary>
    public required bool Exists { get; init; }

    public IReadOnlyList<LockHolder> Holders { get; init; } = [];

    /// <summary>
    /// Whether the holder lookup actually ran and answered.
    ///
    /// An empty <see cref="Holders"/> list means nothing on its own: the Restart Manager
    /// returns nothing for a failed session, a missing DLL, an unexpected status, and a
    /// holder running at a higher integrity level — an elevated agent's git among them.
    /// Without this flag, every one of those reads as "nobody is holding it".
    /// </summary>
    public bool Inspected { get; init; }

    /// <summary>
    /// How long since the file was last written. Null when it is gone or unreadable.
    ///
    /// Last-write rather than creation time on purpose. NTFS file tunnelling replays the
    /// creation timestamp of a file deleted and recreated under the same name within a
    /// short window, and the effect chains — an agent looping git in the watched worktree
    /// cycles index.lock well inside it, so a lock created seconds ago reports an age of
    /// minutes and climbing.
    /// </summary>
    public TimeSpan? Age { get; init; }

    /// <summary>
    /// A lock this old that a successful lookup found nobody holding is probably debris.
    /// All three conditions are load-bearing; the threshold is deliberately generous,
    /// because a `git gc` or a large `git add` can legitimately hold the index for a while
    /// and calling a live operation stale is the worse error.
    /// </summary>
    public bool LooksStale => Exists && Inspected && Holders.Count == 0 && Age > TimeSpan.FromMinutes(2);

    public string Summary
    {
        get
        {
            if (!Exists) return "the lock was released";

            if (Holders.Count > 0)
                return $"held by {string.Join(", ", Holders.Select(h => h.ToString()))}";

            if (LooksStale)
                return $"nothing is holding it and it was last written {Describe(Age!.Value)} ago — it may " +
                       "have been left behind by a git that crashed";

            return "held by another process this app cannot identify";
        }
    }

    private static string Describe(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => $"{(int)age.TotalSeconds}s",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d",
    };
}

/// <summary>
/// Works out who is holding a git lock file.
///
/// The Restart Manager is the only supported way to ask Windows "which processes have this
/// file open" without a driver or handle-table walking. It exists for installers wanting to
/// avoid a reboot, but the question it answers is exactly ours. Every failure path here
/// returns an empty list rather than throwing: not knowing who holds the lock must never
/// be worse than not looking.
/// </summary>
public static partial class GitLock
{
    /// <summary>Pulls the lock file's path out of git's own error message.</summary>
    public static string? PathFromStderr(string stderr)
    {
        var match = LockPathPattern().Match(stderr);
        return match.Success ? RepoPaths.ToPlatform(match.Groups[1].Value) : null;
    }

    /// <summary>Where <c>index.lock</c> lives for a worktree, given its git directory.</summary>
    public static string IndexLockPath(string gitDir) =>
        System.IO.Path.Combine(RepoPaths.ToPlatform(gitDir), "index.lock");

    /// <summary>
    /// Git quotes the path it could not create: <c>Unable to create '…/index.lock': File
    /// exists.</c> Both separators appear — git reports forward slashes even on Windows,
    /// but a path echoed from elsewhere may not.
    /// </summary>
    [GeneratedRegex(@"'([^']+\.lock)'", RegexOptions.CultureInvariant)]
    private static partial Regex LockPathPattern();

    public static LockInfo Describe(string lockFilePath)
    {
        var info = new FileInfo(lockFilePath);
        if (!info.Exists) return new LockInfo { Path = lockFilePath, Exists = false };

        TimeSpan? age = null;
        try
        {
            age = DateTime.UtcNow - info.LastWriteTimeUtc;
            // A clock skew or a copied file can produce a negative age, which would read
            // as "written in the future" rather than "unknown".
            if (age < TimeSpan.Zero) age = null;
        }
        catch (IOException)
        {
            // The file went away underneath us; the holder list below will say so.
        }

        var (holders, inspected) = FindHolders(lockFilePath);

        return new LockInfo
        {
            Path = lockFilePath,
            Exists = true,
            Age = age,
            Holders = holders,
            Inspected = inspected,
        };
    }

    // -----------------------------------------------------------------------
    // Restart Manager
    // -----------------------------------------------------------------------

    private const int RmRebootReasonNone = 0;
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    /// <summary>Guards against an unbounded retry if the process list keeps growing.</summary>
    private const int MaxListAttempts = 4;

    /// <summary>
    /// Asks Windows who has the file open.
    /// </summary>
    /// <returns>
    /// The holders, and whether the lookup succeeded. The second value is not decoration:
    /// every failure path here produces an empty list, and an empty list that means "the
    /// API did not answer" must not be reported as "nobody is holding it".
    /// </returns>
    private static (IReadOnlyList<LockHolder> Holders, bool Inspected) FindHolders(string path)
    {
        uint session = 0;
        var started = false;

        try
        {
            // The key buffer must hold CCH_RM_SESSION_KEY + 1 characters and is written to
            // by the API, so it has to be a StringBuilder with the capacity reserved up
            // front rather than a string.
            var key = new StringBuilder(64);
            if (RmStartSession(out session, 0, key) != ErrorSuccess) return ([], false);
            started = true;

            string[] files = [path];
            if (RmRegisterResources(session, 1, files, 0, null, 0, null) != ErrorSuccess) return ([], false);

            return ReadProcessList(session);
        }
        catch (DllNotFoundException)
        {
            return ([], false); // No Restart Manager: nothing to report, and nothing broken.
        }
        catch (EntryPointNotFoundException)
        {
            return ([], false);
        }
        finally
        {
            if (started)
            {
                try { RmEndSession(session); } catch (DllNotFoundException) { /* already gone */ }
            }
        }
    }

    private static (IReadOnlyList<LockHolder> Holders, bool Inspected) ReadProcessList(uint session)
    {
        uint capacity = 0;

        for (var attempt = 0; attempt < MaxListAttempts; attempt++)
        {
            var array = new RM_PROCESS_INFO[capacity];
            var count = capacity;
            uint reason = RmRebootReasonNone;

            var status = RmGetList(session, out var needed, ref count, capacity == 0 ? null : array, ref reason);

            if (status == ErrorSuccess)
            {
                var holders = new List<LockHolder>((int)count);
                for (var i = 0; i < count && i < array.Length; i++)
                    holders.Add(ToHolder(array[i]));

                return (holders, true);
            }

            if (status != ErrorMoreData) return ([], false);

            // Ask again with room for what it says it needs, plus slack: another process
            // can open the file between the two calls.
            capacity = needed + 2u;
        }

        return ([], false);
    }

    private static LockHolder ToHolder(RM_PROCESS_INFO info)
    {
        var pid = info.Process.dwProcessId;

        // The Restart Manager reports a display name, which for a console program is often
        // blank. The process name is the useful thing — "git" is what the user needs to
        // read — so prefer it and keep RM's name only as a fallback.
        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Exited between RmGetList and here.
        }
        catch (InvalidOperationException)
        {
        }

        if (string.IsNullOrWhiteSpace(name)) name = info.strAppName;
        if (string.IsNullOrWhiteSpace(name)) name = "unknown process";

        if (pid == Environment.ProcessId) name += " (this app)";

        return new LockHolder(pid, name);
    }

    /// <summary>
    /// The FILETIME is spelled out as two DWORDs rather than a long on purpose. A long
    /// would be aligned to 8 bytes on x64 and pad the struct out to 16, where the native
    /// one is 12 — which shifts every field of the RM_PROCESS_INFO that embeds it, so the
    /// process ids come back as garbage rather than failing outright.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public uint ProcessStartTimeLow;
        public uint ProcessStartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
