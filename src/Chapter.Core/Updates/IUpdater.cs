namespace Chapter.Core.Updates;

/// <summary>
/// Where a running Chapter stands relative to the newest published build.
///
/// A state rather than a bool, because every interesting moment is one of the ones in
/// between: a check that is still in flight, a download that is 40% through, an update
/// sitting on disk waiting for a restart. A UI given only "an update exists" has to invent
/// the difference between "we are fetching it" and "it is here", and would get it wrong.
/// </summary>
public enum UpdateState
{
    /// <summary>
    /// This copy cannot update itself, and is not pretending otherwise. A build run out of
    /// <c>bin/Debug</c>, or an unzipped portable copy, has no install to replace — Velopack
    /// only knows where the app lives when the installer put it there. Reporting
    /// <see cref="UpToDate"/> here would be a lie in the one direction that matters: the
    /// user would stop looking for new versions.
    /// </summary>
    Unmanaged,

    /// <summary>Checked, and this is the newest build there is.</summary>
    UpToDate,

    /// <summary>A check is in flight.</summary>
    Checking,

    /// <summary>A newer build exists and is being fetched. <see cref="UpdateStatus.Percent"/> is meaningful.</summary>
    Downloading,

    /// <summary>Downloaded and staged. It takes effect on the next launch, and nothing is lost by never restarting.</summary>
    Ready,

    /// <summary>The last check or download did not finish. <see cref="UpdateStatus.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// A snapshot of the updater, in the shape the front-end draws.
///
/// <see cref="CurrentVersion"/> is always populated — the help panel shows which build is
/// running whether or not that build can update itself, and "which version am I on" is the
/// first question asked of a beta.
/// </summary>
public sealed record UpdateStatus
{
    public required UpdateState State { get; init; }

    /// <summary>The running build, e.g. <c>0.1.0-beta.1</c>.</summary>
    public string CurrentVersion { get; init; } = "";

    /// <summary>The build waiting to be installed, when there is one. Null otherwise.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>Download progress, 0–100. Only moves in <see cref="UpdateState.Downloading"/>.</summary>
    public int Percent { get; init; }

    /// <summary>Why the last attempt failed, for <see cref="UpdateState.Failed"/> alone.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Self-update, as far as anything outside the shell needs to know about it.
///
/// The implementation is Velopack and lives in <c>Chapter.App</c>, not here, for the same
/// reason the folder picker does: it is a property of how this copy was installed, which is
/// a question about the Windows shell rather than about git. Core keeps the seam so the
/// bridge can serve <c>checkForUpdate</c> without linking an installer framework into the
/// project the tests run against — and so a build with no updater at all (the test host, a
/// future non-Windows shell) is a null reference rather than a special case.
/// </summary>
public interface IUpdater
{
    /// <summary>The latest known state. Never blocks; reflects the last completed step.</summary>
    UpdateStatus Status { get; }

    /// <summary>
    /// Looks for a newer build and downloads one if it exists, reporting progress through
    /// <see cref="StatusChanged"/> as it goes. Safe to call again while one is running —
    /// the second call returns the state of the first rather than starting a second download.
    /// </summary>
    Task<UpdateStatus> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Restarts into the downloaded build. Does not return: the process is replaced.
    /// Refused, rather than throwing, when nothing is staged.
    /// </summary>
    void ApplyAndRestart();

    /// <summary>Raised whenever <see cref="Status"/> changes, on whatever thread noticed.</summary>
    event Action<UpdateStatus>? StatusChanged;
}
