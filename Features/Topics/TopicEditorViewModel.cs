using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Topics;

// Backs the add/edit-topic dialog. Operates on a clone so the user can cancel cleanly.
public sealed partial class TopicEditorViewModel : ObservableObject
{
    // Preserved across edit so the topic keeps its stable identity.
    public Guid Id { get; private set; } = Guid.NewGuid();

    public IReadOnlyList<ServerConfig> AvailableServers { get; private set; } = Array.Empty<ServerConfig>();

    /// <summary>Existing group names, offered as suggestions in the editable group box.</summary>
    public IReadOnlyList<string> AvailableGroups { get; private set; } = Array.Empty<string>();

    [ObservableProperty] private ServerConfig? _selectedServer;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _hasMinPriority;
    [ObservableProperty] private Priority _minPriority = Priority.Min;
    [ObservableProperty] private bool _overrideActiveHours;
    [ObservableProperty] private bool _activeHoursEnabled;
    [ObservableProperty] private TimeOnly _activeHoursStart = new(9, 0);
    [ObservableProperty] private TimeOnly _activeHoursEnd   = new(18, 0);

    public static TopicEditorViewModel FromTopic(
        TopicSettings? source,
        IReadOnlyList<ServerConfig> servers,
        Guid defaultServerId,
        IReadOnlyList<string> groups)
    {
        if (source is null)
        {
            return new TopicEditorViewModel
            {
                AvailableServers = servers,
                AvailableGroups = groups,
                SelectedServer = servers.FirstOrDefault(s => s.Id == defaultServerId) ?? servers.FirstOrDefault(),
            };
        }

        return new TopicEditorViewModel
        {
            Id = source.Id,
            AvailableServers = servers,
            AvailableGroups = groups,
            SelectedServer = servers.FirstOrDefault(s => s.Id == source.ServerId) ?? servers.FirstOrDefault(),
            Name = source.Name,
            DisplayName = source.DisplayName ?? string.Empty,
            GroupName = source.GroupName ?? string.Empty,
            Enabled = source.Enabled,
            IsPaused = source.IsPaused,
            HasMinPriority = source.MinPriority is not null,
            MinPriority = source.MinPriority ?? Priority.Min,
            OverrideActiveHours = source.ActiveHoursEnabled is not null,
            ActiveHoursEnabled = source.ActiveHoursEnabled ?? false,
            ActiveHoursStart = source.ActiveHoursStart,
            ActiveHoursEnd = source.ActiveHoursEnd,
        };
    }

    public TopicSettings ToTopic() => new()
    {
        Id = Id,
        ServerId = SelectedServer?.Id ?? Guid.Empty,
        Name = Name.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
        GroupName = string.IsNullOrWhiteSpace(GroupName) ? null : GroupName.Trim(),
        Enabled = Enabled,
        IsPaused = IsPaused,
        MinPriority = HasMinPriority ? MinPriority : null,
        ActiveHoursEnabled = OverrideActiveHours ? ActiveHoursEnabled : null,
        ActiveHoursStart = ActiveHoursStart,
        ActiveHoursEnd = ActiveHoursEnd,
    };
}
