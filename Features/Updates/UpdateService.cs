using System.IO;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Notifications;
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
// Releases. The UpdateManager checks whichever channel this build was installed
// from ("stable" or "dev") — we don't pin a channel, so a stable install only sees
// stable releases and a dev install auto-updates through every dev build. That
// channel separation is what walls the two apart; prerelease:true is required so a
// dev install can see its releases (dev releases are published as GitHub
// pre-releases, which also keeps `releases/latest` pointing at the last stable
// build). Only functional in a Velopack-installed build: running from the IDE
// reports IsSupported=false and every call no-ops, so dev runs never offer an update.
//
// On finding an update it both raises the in-app banner (UpdateAvailable event)
// and shows a Windows notification — the toast matters because this is largely a
// tray app, so the user shouldn't have to open the window to learn about updates.
public sealed class UpdateService(EventBus bus, ToastNotifier toasts)
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

    private readonly UpdateManager _manager = new(ResolveSource());

    // The update found by the last successful check, ready to download + apply.
    private UpdateInfo? _pending;

    // The version we last raised a toast for, so repeated checks (manual + the daily
    // background one) for the same update don't re-toast. The banner is idempotent.
    private string? _notifiedVersion;

    // True only when launched from a Velopack install — the only case where an
    // update can actually be applied.
    public bool IsSupported => _manager.IsInstalled;

    // Whether a check has already found an update waiting. Lets a late-created
    // consumer (the main-window VM, built only when the window is first shown) pick
    // up an update the checker found before the window existed.
    public bool IsUpdatePending => _pending is not null;

    // Version of the pending update, for the banner / status text. Null until found.
    public string? PendingVersion => _pending?.TargetFullRelease.Version?.ToString();

    // Checks GitHub for a newer stable release. On finding one it stashes it and
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

        if (_pending is null) return UpdateCheckResult.UpToDate;

        await NotifyFoundAsync();
        return UpdateCheckResult.UpdateAvailable;
    }

    private async Task NotifyFoundAsync()
    {
        var version = PendingVersion ?? "";

        // Banner: always reflect the current pending update (idempotent).
        await bus.PublishAsync(new UpdateAvailable(version), PublishMode.WaitForNone);

        // Toast: once per distinct version, so repeated checks don't spam.
        if (version == _notifiedVersion) return;
        _notifiedVersion = version;
        toasts.ShowUpdateAvailable(version);
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
