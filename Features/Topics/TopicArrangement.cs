using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics.Events;

namespace NtfyDesktop.Features.Topics;

/// <summary>
/// Responsible for topic organization (groups, move, etc)
/// </summary>
public class TopicArrangement(AppSettings settings)
{
    #region Group-name normalization

    public static string GetTopicGroupName(TopicSettings topic) => topic.GroupName?.Trim() ?? string.Empty;

    #endregion

    #region Queries

    public TopicSettings? FirstTopicInGroup(string group) =>
        settings.Topics.FirstOrDefault(t => GetTopicGroupName(t) == group);

    public List<TopicSettings> GetTopicsInGroup(string? groupName)
        => settings.Topics.Where(t => GetTopicGroupName(t) == (groupName ?? string.Empty)).ToList();

    private int? GetTopicIndexInGroup(TopicSettings topic, out List<TopicSettings> group)
    {
        group = settings.Topics.Where(t => GetTopicGroupName(t) == GetTopicGroupName(topic)).ToList();

        var index = group.IndexOf(topic);

        return index < 0 ? null : index;
    }

    private int GetLastIndexOfGroup(string groupName)
    {
        var last = -1;

        for (var k = 0; k < settings.Topics.Count; k++)
            if (GetTopicGroupName(settings.Topics[k]) == groupName) last = k;

        return last;
    }

    // Distinct existing group names, offered as suggestions in the topic editor.
    // (Alphabetical + case-insensitive dedup — deliberately different from
    // OrderedGroups(), which returns group display order.)
    public IReadOnlyList<string> GroupNames =>
        settings.Topics
            .Select(t => t.GroupName?.Trim())
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Existing group names in their display order: GroupOrder first, then any
    // not-yet-tracked groups alphabetically.
    public List<string> OrderedGroupNames
    {
        get
        {
            var used = settings.Topics
                .Select(GetTopicGroupName)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

            var ordered = settings.GroupOrder.Where(used.Contains).ToList();

            ordered.AddRange(used.Except(ordered, StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase));

            return ordered;
        }
    }

    #endregion

    #region Topic ordering

    public bool CanMoveTopicWithinGroup(TopicSettings topic, int direction) =>
        GetTopicIndexInGroup(topic, out var section) is { } i && i + direction >= 0 && i + direction < section.Count;

    // Reorder a topic among its group-mates. direction: -1 = up, +1 = down.
    public void MoveTopicWithinGroup(TopicSettings topic, int direction)
    {
        if (GetTopicIndexInGroup(topic, out var group) is not { } i) return;
        var j = i + direction;
        if (j < 0 || j >= group.Count) return;

        var neighbour = group[j];
        var a = settings.Topics.IndexOf(topic);
        var b = settings.Topics.IndexOf(neighbour);

        (settings.Topics[a], settings.Topics[b]) = (settings.Topics[b], settings.Topics[a]);

        settings.Save();
        _ = new TopicMoved(topic.Id).PublishAsync();
    }

    // Move a topic into another group (null/blank = ungrouped), appended to the end
    // of the target section. Group membership is a display concern — no reconnect.
    public void MoveTopicToGroup(TopicSettings topic, string? groupName)
    {
        var normalized = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();

        if (GetTopicGroupName(topic) == (normalized ?? string.Empty)) return;

        topic.GroupName = normalized;

        settings.Topics.Remove(topic);

        var lastIndex = GetLastIndexOfGroup(normalized ?? string.Empty);

        settings.Topics.Insert(lastIndex < 0 ? settings.Topics.Count : lastIndex + 1, topic);

        SyncGroupOrder();
        settings.Save();

        _ = new TopicMoved(topic.Id).PublishAsync();
    }

    // Drop `dragged` immediately before/after `anchor`, joining anchor's section.
    public void MoveTopicRelativeTo(TopicSettings dragged, TopicSettings anchor, bool before)
    {
        if (ReferenceEquals(dragged, anchor)) return;

        dragged.GroupName = anchor.GroupName;
        settings.Topics.Remove(dragged);

        var i = settings.Topics.IndexOf(anchor);
        if (i < 0) i = settings.Topics.Count - 1;
        settings.Topics.Insert(before ? i : i + 1, dragged);

        SyncGroupOrder();
        settings.Save();

        _ = new TopicMoved(dragged.Id).PublishAsync();
    }

    #endregion

    #region Group ordering

    // When reordering topic groups, a group dragged from below its anchor lands before it;
    // from above, after it. Direction is implied by the current GroupOrder.
    public bool GroupGoesBefore(string draggedGroupName, string anchorGroupName) =>
        settings.GroupOrder.IndexOf(draggedGroupName) > settings.GroupOrder.IndexOf(anchorGroupName);

    public bool CanMoveGroup(string groupName, int direction)
    {
        var i = settings.GroupOrder.IndexOf(groupName);
        return i >= 0 && i + direction >= 0 && i + direction < settings.GroupOrder.Count;
    }

    public void MoveGroup(string group, int direction)
    {
        var i = settings.GroupOrder.IndexOf(group);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= settings.GroupOrder.Count) return;

        (settings.GroupOrder[i], settings.GroupOrder[j]) = (settings.GroupOrder[j], settings.GroupOrder[i]);

        settings.Save();
        _ = new GroupMoved(group).PublishAsync();
    }

    public void MoveGroupRelativeTo(string dragged, string anchor)
    {
        var before = GroupGoesBefore(dragged, anchor);

        if (string.Equals(dragged, anchor, StringComparison.Ordinal)) return;
        if (!settings.GroupOrder.Contains(dragged) || !settings.GroupOrder.Contains(anchor)) return;

        settings.GroupOrder.Remove(dragged);
        var i = settings.GroupOrder.IndexOf(anchor);
        settings.GroupOrder.Insert(before ? i : i + 1, dragged);

        settings.Save();
        _ = new GroupMoved(dragged).PublishAsync();
    }

    // Reconcile GroupOrder with the groups actually in use: drop empties, append new
    // ones alphabetically. Caller persists.
    public void SyncGroupOrder()
    {
        var used = settings.Topics
            .Select(GetTopicGroupName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();

        settings.GroupOrder.RemoveAll(g => !used.Contains(g, StringComparer.Ordinal));

        foreach (var g in used.OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)) {

            if (!settings.GroupOrder.Contains(g, StringComparer.Ordinal))
                settings.GroupOrder.Add(g);
        }
    }

    #endregion

    #region Group collapse state

    // Persisted collapse state for a group folder.
    public bool IsGroupCollapsed(string groupName) =>
        settings.CollapsedGroups.Contains(groupName);

    public void SetGroupCollapsed(string groupName, bool newValue)
    {
        var currentValue = IsGroupCollapsed(groupName);
        if (newValue == currentValue) return;

        if (newValue) settings.CollapsedGroups.Add(groupName);
        else           settings.CollapsedGroups.Remove(groupName);

        settings.Save();
    }

    #endregion
}
