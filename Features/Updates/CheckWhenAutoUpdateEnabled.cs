using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Settings.Events;

namespace NtfyDesktop.Features.Updates;

// When the user turns automatic update checking on, check right away rather than
// waiting up to a day for the background service's next tick. Turning it off needs
// no action — the background check gates each tick on the setting. Auto-registered
// by the IEventHandler<> assembly scan.
public sealed class CheckWhenAutoUpdateEnabled(UpdateService updates) : IEventHandler<AutoUpdateCheckSettingChanged>
{
    public Task HandleAsync(AutoUpdateCheckSettingChanged eventModel, CancellationToken ct)
        => eventModel.Enabled ? updates.CheckAsync() : Task.CompletedTask;
}
