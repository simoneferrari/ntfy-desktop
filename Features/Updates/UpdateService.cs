using System.IO;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Updates.Events;
using Velopack;
using Velopack.Sources;

namespace NtfyDesktop.Features.Updates;

public enum UpdateCheckResult
{
    NotSupported,   // not a Velopack install (dev / portable) — can't check or apply
    UpToDate,       // checked, nothing newer
    UpdateAvailable,// a newer release was found and stashed
    Failed,         // the check threw (network / parse) — left to the caller to surface
}

// Thin wrapper over Velopack's UpdateManager, pointed at this repo's GitHub
// Releases. The manager is pinned to the user's *selected* channel ("stable" or
// "dev") via ExplicitChannel — defaulting to the channel this build was installed
// from, so a stable install sees stable and a dev install auto-updates through every
// dev build. That channel separation is what walls the two apart. In-app channel
// switching (SetChannelAsync) rebuilds the manager onto the other channel; switching
// dev→stable is usually a *downgrade* (a dev build's version outranks the latest
// stable), which AllowVersionDowngrade permits — we already apply explicitly, never
// via auto-apply-on-startup (which won't apply a lower-or-equal version). prerelease:
// true is required so the dev channel's releases (published as GitHub pre-releases)
// are found, and it also keeps `releases/latest` on the last stable build. Only
// functional in a Velopack-installed build: running from the IDE reports
// IsSupported=false and every call no-ops, so dev runs never offer an update.
//
// On finding an update it both raises the in-app banner (UpdateStatusChanged event)
// and shows a Windows notification — the toast matters because this is largely a
// tray app, so the user shouldn't have to open the window to learn about updates.
public sealed class UpdateService(EventBus bus, ToastNotifier toasts, AppSettings settings)
{
    private const string RepoUrl = "https://github.com/simoneferrari/ntfy-desktop";

    // Dev/test hook: point the updater at a local `vpk pack` output folder instead
    // of GitHub, so the full check → download → apply → restart flow can be exercised
    // offline (see .claude/ROADMAP-NOTES.md). Set via the --update-feed <dir> launch
    // arg or the NTFY_UPDATE_FEED env var. Guarded: inert unless one is explicitly set
    // to an existing directory, so production always uses GitHub. Intentionally works
    // in Release builds too — the local loop needs an installed (Release) build.
    private const string FeedArg = "--update-feed";
    private const string FeedEnvVar = "NTFY_UPDATE_FEED";

    // Rebuilt by SetChannelAsync when the user switches channel (ExplicitChannel is
    // fixed at construction), so it's not readonly.
    private UpdateManager _manager = BuildManager(SelectedChannelFor(settings));

    // The update found by the last successful check, ready to download + apply.
    private UpdateInfo? _pending;

    // The version we last raised a toast for, so repeated checks (manual + the daily
    // background one) for the same update don't re-toast. The banner is idempotent.
    private string? _notifiedVersion;

    // True only when launched from a Velopack install — the only case where an
    // update can actually be applied.
    public bool IsSupported => _manager.IsInstalled;

    // ===== Channels =====

    // The channel this running build belongs to (derived from its own version), i.e.
    // what's actually installed right now.
    public string InstalledChannel => UpdateChannels.ForVersion(AppVersion.Current);

    // The channel the user wants future updates from. Defaults to the installed
    // channel until they pick otherwise. While it differs from InstalledChannel a
    // switch is pending — the next check finds the cross-channel build for the banner.
    public string SelectedChannel => SelectedChannelFor(settings);

    public bool IsChannelSwitchPending =>
        !string.Equals(SelectedChannel, InstalledChannel, StringComparison.Ordinal);

    private static string SelectedChannelFor(AppSettings s) =>
        string.IsNullOrEmpty(s.UpdateChannel)
            ? UpdateChannels.ForVersion(AppVersion.Current)
            : s.UpdateChannel;

    // Whether a check has already found an update waiting. Lets a late-created
    // consumer (the main-window VM, built only when the window is first shown) pick
    // up an update the checker found before the window existed.
    public bool IsUpdatePending => _pending is not null;

    // Version of the pending update, for the banner / status text. Null until found.
    public string? PendingVersion => _pending?.TargetFullRelease.Version?.ToString();

    // Checks the selected channel for a newer release. On finding one it stashes it and
    // notifies (banner + toast). Network / parse failures return Failed rather than
    // throwing — a failed check must never disrupt the running app.
    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsSupported) return UpdateCheckResult.NotSupported;

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
        }
        catch
        {
            return UpdateCheckResult.Failed;
        }

        if (_pending is null)
        {
            // Nothing staged — make sure the banner reflects that (it may have been
            // showing a cross-channel build the user then reverted away from).
            await bus.PublishAsync(new UpdateStatusChanged(false, ""), PublishMode.WaitForNone);
            return UpdateCheckResult.UpToDate;
        }

        await NotifyFoundAsync();
        return UpdateCheckResult.UpdateAvailable;
    }

    private async Task NotifyFoundAsync()
    {
        var version = PendingVersion ?? "";

        // Banner: always reflect the current pending update (idempotent).
        await bus.PublishAsync(new UpdateStatusChanged(true, version), PublishMode.WaitForNone);

        // Toast: once per distinct version, so repeated checks don't spam.
        if (version == _notifiedVersion) return;
        _notifiedVersion = version;
        toasts.ShowUpdateAvailable(version);
    }

    // Switches the channel the updater follows. Persists the choice, rebuilds the
    // manager onto the new channel (ExplicitChannel is fixed at construction), clears
    // any stash from the old channel, then re-checks so the banner/toast reflect the
    // new channel — a cross-channel build (a downgrade for dev→stable) surfaces through
    // the normal "Restart & update" flow. Reverting to InstalledChannel finds nothing
    // and clears the banner. No-ops on a non-installed build (every call is inert).
    public async Task SetChannelAsync(string channel)
    {
        settings.UpdateChannel = channel;
        settings.Save();

        _manager = BuildManager(channel);
        _pending = null;
        _notifiedVersion = null;

        await CheckAsync();
    }

    // Downloads the pending update and restarts into it. The process exits inside
    // ApplyUpdatesAndRestart, so this only returns if there's nothing pending or the
    // download throws (left to the caller to surface / retry).
    public async Task DownloadAndApplyAsync()
    {
        if (!IsSupported || _pending is null) return;

        await _manager.DownloadUpdatesAsync(_pending);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    // Builds an UpdateManager pinned to the given channel. AllowVersionDowngrade lets a
    // dev→stable switch apply a lower version (a dev build outranks the latest stable);
    // it's harmless for normal forward updates and for same-channel installs (where this
    // matches Velopack's own default channel anyway).
    private static UpdateManager BuildManager(string channel) =>
        new(ResolveSource(), new UpdateOptions
        {
            ExplicitChannel = channel,
            AllowVersionDowngrade = true,
        });

    // GitHub releases by default; a local folder when the dev/test feed override is set.
    private static IUpdateSource ResolveSource()
    {
        var feed = LocalFeedPath();
        return feed is not null
            ? new SimpleFileSource(new DirectoryInfo(feed))
            : new GithubSource(RepoUrl, accessToken: null, prerelease: true);
    }

    // The override directory from --update-feed or NTFY_UPDATE_FEED, or null when
    // unset / blank / not an existing directory (so it silently falls back to GitHub).
    private static string? LocalFeedPath()
    {
        var path = ArgFeedPath() ?? Environment.GetEnvironmentVariable(FeedEnvVar);
        if (string.IsNullOrWhiteSpace(path)) return null;

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return Directory.Exists(path) ? path : null;
    }

    // Scans the launch args for `--update-feed <dir>` or `--update-feed=<dir>`,
    // mirroring how App handles --data-path.
    private static string? ArgFeedPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == FeedArg && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(FeedArg + "=", StringComparison.Ordinal))
                return args[i][(FeedArg.Length + 1)..];
        }
        return null;
    }
}
