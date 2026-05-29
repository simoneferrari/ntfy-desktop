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

        SyncGroupOrder();
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
        SyncGroupOrder();
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

    // ===== Manual ordering (rail) =====
    //
    // The Topics list order is the source of truth for topic order within a section
    // (a "section" = topics sharing a group, or all ungrouped topics). GroupOrder is
    // the source of truth for folder order. None of these touch connections.

    private static string Section(TopicSettings t) => t.GroupName?.Trim() ?? string.Empty;

    public bool CanMoveTopic(TopicSettings topic, int direction) =>
        IndexInSection(topic, out var section) is { } i && i + direction >= 0 && i + direction < section.Count;

    // Reorder a topic among its section-mates. direction: -1 = up, +1 = down.
    public void MoveTopic(TopicSettings topic, int direction)
    {
        if (IndexInSection(topic, out var section) is not { } i) return;
        var j = i + direction;
        if (j < 0 || j >= section.Count) return;

        var neighbour = section[j];
        var a = _settings.Topics.IndexOf(topic);
        var b = _settings.Topics.IndexOf(neighbour);
        (_settings.Topics[a], _settings.Topics[b]) = (_settings.Topics[b], _settings.Topics[a]);

        _settings.Save();
        ReloadFromSettings();
        _settings.RaiseDisplayChanged();
    }

    // Move a topic into another group (null/blank = ungrouped), appended to the end
    // of the target section. Group membership is a display concern — no reconnect.
    public void MoveTopicToGroup(TopicSettings topic, string? group)
    {
        var normalized = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        if (Section(topic) == (normalized ?? string.Empty)) return;

        topic.GroupName = normalized;

        _settings.Topics.Remove(topic);
        var last = LastIndexOfSection(normalized ?? string.Empty);
        _settings.Topics.Insert(last < 0 ? _settings.Topics.Count : last + 1, topic);

        SyncGroupOrder();
        _settings.Save();
        ReloadFromSettings();
        _settings.RaiseDisplayChanged();
    }

    public bool CanMoveGroup(string group, int direction)
    {
        var i = _settings.GroupOrder.IndexOf(group);
        return i >= 0 && i + direction >= 0 && i + direction < _settings.GroupOrder.Count;
    }

    public void MoveGroup(string group, int direction)
    {
        var i = _settings.GroupOrder.IndexOf(group);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= _settings.GroupOrder.Count) return;

        (_settings.GroupOrder[i], _settings.GroupOrder[j]) = (_settings.GroupOrder[j], _settings.GroupOrder[i]);
        _settings.Save();
        _settings.RaiseDisplayChanged();
    }

    // Existing group names in their display order: GroupOrder first, then any
    // not-yet-tracked groups alphabetically.
    public IReadOnlyList<string> OrderedGroups()
    {
        var used = _settings.Topics.Select(Section).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var ordered = _settings.GroupOrder.Where(used.Contains).ToList();
        ordered.AddRange(used.Except(ordered, StringComparer.Ordinal)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase));
        return ordered;
    }

    // Reconcile GroupOrder with the groups actually in use: drop empties, append new
    // ones alphabetically. Caller persists.
    public void SyncGroupOrder()
    {
        var used = _settings.Topics.Select(Section).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        _settings.GroupOrder.RemoveAll(g => !used.Contains(g, StringComparer.Ordinal));
        foreach (var g in used.OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase))
            if (!_settings.GroupOrder.Contains(g, StringComparer.Ordinal))
                _settings.GroupOrder.Add(g);
    }

    private int? IndexInSection(TopicSettings topic, out List<TopicSettings> section)
    {
        section = _settings.Topics.Where(t => Section(t) == Section(topic)).ToList();
        var i = section.IndexOf(topic);
        return i < 0 ? null : i;
    }

    private int LastIndexOfSection(string section)
    {
        var last = -1;
        for (var k = 0; k < _settings.Topics.Count; k++)
            if (Section(_settings.Topics[k]) == section) last = k;
        return last;
    }
}
