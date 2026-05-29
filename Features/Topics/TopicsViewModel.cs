using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Topics;

// Exposes the configured topic list for the Topics page.
// Mutations route through SettingsManager and trigger a ConnectionManager re-apply
// so subscriptions actually reflect the change.
public sealed partial class TopicsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly HistoryRepository _history;

    public ObservableCollection<TopicSettings> Topics { get; } = new();

    [ObservableProperty]
    private TopicSettings? _selectedTopic;

    [ObservableProperty]
    private bool _isEmpty = true;

    public TopicsViewModel(AppSettings settings, ConnectionManager connections, HistoryRepository history)
    {
        _settings = settings;
        _connections = connections;
        _history = history;
        Topics.CollectionChanged += (_, _) => IsEmpty = Topics.Count == 0;
        ReloadFromSettings();
    }

    public void ReloadFromSettings()
    {
        Topics.Clear();
        foreach (var t in _settings.Topics)
            Topics.Add(t);
    }

    public async Task AddOrUpdateAsync(TopicSettings edited, TopicSettings? original)
    {
        if (original is not null)
        {
            // Editing: keep the original's stable identity. Server + display name now
            // come from the editor dialog (the user may move the topic to another
            // server or rename its label).
            edited.Id = original.Id;

            var idx = _settings.Topics.IndexOf(original);
            if (idx >= 0) _settings.Topics[idx] = edited;
        }
        else
        {
            // New topic: the dialog preselects the default server, but fall back just
            // in case it came through unset.
            if (edited.ServerId == Guid.Empty)
                edited.ServerId = _settings.DefaultServerId;
            _settings.Topics.Add(edited);
        }

        _settings.Save();
        ReloadFromSettings();

        // Only rebuild the one topic's connection, and only when its subscription
        // identity (topic name or server) changed — other edits (display name,
        // priority, active hours) don't touch the socket. Add / enable / disable are
        // handled idempotently by ApplySettings.
        if (original is not null && (original.Name != edited.Name || original.ServerId != edited.ServerId))
            await _connections.RebuildTopicAsync(edited.Id);
        else
            await _connections.ApplySettingsAsync();
    }

    // Remove a topic. When deleteHistory is false the messages are kept and stay
    // browsable under "All topics" (mirrors server removal); when true they're
    // purged by topic id.
    public async Task RemoveAsync(TopicSettings topic, bool deleteHistory)
    {
        _settings.Topics.Remove(topic);
        _settings.Save();

        ReloadFromSettings();

        if (deleteHistory)
            _history.DeleteByTopicId(topic.Id);

        await _connections.ApplySettingsAsync();
    }

    // Flip a topic's Enabled flag and re-apply so the socket actually starts/stops.
    // Same persistence path as editing the topic via the dialog.
    public async Task ToggleEnabledAsync(TopicSettings topic)
    {
        topic.Enabled = !topic.Enabled;
        _settings.Save();
        ReloadFromSettings();
        await _connections.ApplySettingsAsync();
    }
}
