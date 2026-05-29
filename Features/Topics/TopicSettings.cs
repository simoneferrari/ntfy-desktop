using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Topics;

public class TopicSettings
{
    /// <summary>Stable synthetic identity. Used as the universal key across
    /// connections, pause state, history, feed and toast activation.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Which server this topic is subscribed on.</summary>
    public Guid ServerId { get; set; }

    /// <summary>The actual ntfy topic name (the subscription identifier).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional friendly label; falls back to <see cref="Name"/> for display.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional user-defined group/folder for the nav rail. Null/blank =
    /// ungrouped (shown above the folders).</summary>
    public string? GroupName { get; set; }

    public bool Enabled { get; set; } = true;
    public bool IsPaused { get; set; } = false;
    public Priority? MinPriority { get; set; } = null;

    // null = inherit global; true/false = override global
    public bool? ActiveHoursEnabled { get; set; } = null;
    public TimeOnly ActiveHoursStart { get; set; } = new TimeOnly(9, 0);
    public TimeOnly ActiveHoursEnd { get; set; } = new TimeOnly(18, 0);

    /// <summary>What to show in the UI: friendly name if set, otherwise the topic name.</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    public TopicSettings Clone() => new()
    {
        Id = Id,
        ServerId = ServerId,
        Name = Name,
        DisplayName = DisplayName,
        GroupName = GroupName,
        Enabled = Enabled,
        IsPaused = IsPaused,
        MinPriority = MinPriority,
        ActiveHoursEnabled = ActiveHoursEnabled,
        ActiveHoursStart = ActiveHoursStart,
        ActiveHoursEnd = ActiveHoursEnd,
    };
}
