using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Updates.Events;

// Raised after every update check so the in-app banner reflects the current state.
// Available=true with a Version when a release is staged; Available=false clears the
// banner (e.g. the user switched channels and back, leaving nothing pending).
public sealed class UpdateStatusChanged(bool available, string version) : IEvent
{
    public bool Available { get; } = available;
    public string Version { get; } = version;
}
