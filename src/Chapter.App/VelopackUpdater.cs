using System.Reflection;
using Chapter.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Chapter.App;

/// <summary>
/// Self-update against this repository's GitHub releases.
///
/// Velopack rather than a hand-rolled downloader, for the reason self-update on Windows is
/// worth a dependency at all: you cannot overwrite a running executable. Doing it correctly
/// means a second process, a staged copy, an atomic swap and somewhere to roll back to when
/// the swap half-happens — and getting any of that wrong bricks the install rather than
/// failing the update. Velopack also ships deltas, which is what keeps a self-contained
/// build's 170MB download from being 170MB every time.
/// </summary>
public sealed class VelopackUpdater : IUpdater
{
    private const string RepositoryUrl = "https://github.com/letrquan/chapter";

    private readonly UpdateManager? _manager;
    private readonly Lock _gate = new();

    /// <summary>Guards against a second check while one is running, not against races on the status.</summary>
    private int _busy;

    private UpdateStatus _status;

    public event Action<UpdateStatus>? StatusChanged;

    public VelopackUpdater()
    {
        var version = RunningVersion();

        // Prereleases are offered to builds that are themselves prereleases. It is the rule
        // that maintains itself: while Chapter is in beta every user is on a `-beta.n` build
        // and follows the betas, and the first person to install a stable 1.0.0 stops being
        // shown them without anyone having to remember to change a flag here.
        var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: version.Contains('-'));

        try
        {
            _manager = new UpdateManager(source);
        }
        catch
        {
            // Construction reads the install metadata beside the executable. A build run from
            // bin/Debug has none, and that is the ordinary case during development rather than
            // an error worth surfacing.
            _manager = null;
        }

        var managed = _manager?.IsInstalled == true;

        _status = new UpdateStatus
        {
            State = managed ? UpdateState.UpToDate : UpdateState.Unmanaged,
            CurrentVersion = version,
        };
    }

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        if (_manager is null || !_manager.IsInstalled) return Status;

        // A second caller gets the first call's state rather than a second download. The
        // startup check and the user pressing the button in the help panel are the two that
        // collide, and they collide precisely when the network is slow enough to matter.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return Status;

        try
        {
            Publish(s => s with { State = UpdateState.Checking, Error = null });

            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
                return Publish(s => s with { State = UpdateState.UpToDate, AvailableVersion = null, Percent = 0 });

            var target = update.TargetFullRelease.Version.ToString();

            Publish(s => s with { State = UpdateState.Downloading, AvailableVersion = target, Percent = 0 });

            await _manager
                .DownloadUpdatesAsync(update, percent => Publish(s => s with { Percent = percent }), ct)
                .ConfigureAwait(false);

            return Publish(s => s with { State = UpdateState.Ready, AvailableVersion = target, Percent = 100 });
        }
        catch (Exception ex)
        {
            // Reported, never thrown. A machine that is offline, behind a proxy, or rate-limited
            // by an unauthenticated GitHub is the common case, and none of them is a reason to
            // interrupt somebody reviewing a diff.
            return Publish(s => s with { State = UpdateState.Failed, Error = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void ApplyAndRestart()
    {
        if (_manager is null || Status.State is not UpdateState.Ready) return;

        // Null applies the newest staged release. Nothing is passed as restart arguments:
        // the repository list is in settings.json, so a relaunch with no path opens on the
        // same repositories, and passing the one from this launch's command line would pin
        // the reopened window to it.
        _manager.ApplyUpdatesAndRestart(null);
    }

    private UpdateStatus Publish(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus updated;
        lock (_gate) updated = _status = change(_status);

        StatusChanged?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// The version this build reports, taken from the assembly rather than from Velopack.
    ///
    /// <c>UpdateManager.CurrentVersion</c> is null unless the app was installed, and "which
    /// build am I running" is a question worth answering in a copy that cannot update itself
    /// — during development it is the only way to tell one from another. The informational
    /// version carries the SemVer prerelease tag that the four-part assembly version drops,
    /// and the <c>+sha</c> suffix the SDK appends is cut: it is build provenance, not a version.
    /// </summary>
    private static string RunningVersion()
    {
        var informational = typeof(VelopackUpdater).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
