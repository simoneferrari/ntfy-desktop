using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Updates.Events;

// Raised once the periodic checker finds a newer stable release on GitHub.
// Carries the version string for the update banner.
public sealed class UpdateAvailable(string version) : IEvent
{
    public string Version { get; } = version;
}
