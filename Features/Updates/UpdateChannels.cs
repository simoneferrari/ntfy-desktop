using System.Reflection;

namespace NtfyDesktop.Features.Updates;

// The two Velopack update channels. The names match release.yml's packing exactly
// (lowercase "stable"/"dev"): CI routes a SemVer pre-release version to the dev
// channel and a final version to stable, and the updater reads releases.{channel}.json.
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Dev = "dev";

    // The channel this running build belongs to, derived from its own version the
    // same way release.yml routes it: a pre-release suffix (e.g. 0.7.0-dev.1) ⇒ dev,
    // otherwise stable. So the installed channel is always consistent with how the
    // build was packed — no separate state to keep in sync.
    public static string ForVersion(string version) =>
        version.Contains('-') ? Dev : Stable;
}

// The running build's version string, parsed once. Carries any pre-release suffix
// (e.g. 0.7.0-dev.1) from the informational version; the SDK may append "+<commit>"
// build metadata, which we trim. Falls back to the numeric assembly version (which
// can't represent a pre-release suffix).
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    // True when this build is a pre-release (has a SemVer "-suffix") — i.e. a dev build.
    public static bool IsPrerelease => Current.Contains('-');

    private static string Resolve()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "";

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
