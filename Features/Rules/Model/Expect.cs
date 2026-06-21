using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Rules.Model;

/// <summary>A notification the engine raises itself (absence or recovery).</summary>
public sealed record AlertSpec(Priority Priority, string Title, string? Message);

/// <summary>
/// "I expect a message matching <see cref="When"/> at least every <see cref="Every"/>
/// (plus <see cref="Grace"/>); alert via <see cref="OnAbsence"/> when overdue. If
/// <see cref="OnRecovery"/> is set, also notify when matching messages resume after
/// an alert. <see cref="Id"/> namespaces the rule's saved state (pack name + index).
/// </summary>
public sealed record ExpectRule(
    string Id,
    Matcher When,
    TimeSpan Every,
    TimeSpan Grace,
    AlertSpec OnAbsence,
    AlertSpec? OnRecovery);
