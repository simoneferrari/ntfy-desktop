using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Settings.Events;

// Raised when the user changes the "check for updates automatically" setting,
// carrying its new state. The Updates feature reacts (an immediate check on enable);
// Settings itself stays out of that concern.
public record AutoUpdateCheckSettingChanged(bool Enabled) : IEvent;
