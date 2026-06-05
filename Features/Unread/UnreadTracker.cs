using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.History.Events;
using NtfyDesktop.Features.Unread.Events;

namespace NtfyDesktop.Features.Unread;

/// <summary>
/// Which feed the user is currently looking at. Used to decide whether an
/// incoming message should count as unread, and what to clear when the window
/// regains focus.
/// </summary>
public readonly record struct ActiveView(bool IsFeed, Guid? TopicId)
{
    /// <summary>No feed is shown (e.g. Settings or Connections page).</summary>
    public static readonly ActiveView None = new(false, null);

    /// <summary>The combined "All topics" feed.</summary>
    public static ActiveView AllTopics => new(true, null);

    /// <summary>A single topic's feed.</summary>
    public static ActiveView Topic(Guid id) => new(true, id);

    /// <summary>True when a feed is shown and the given message belongs to it
    /// (All-topics matches every message; a topic feed matches only its own).</summary>
    public bool Matches(Guid messageTopicId) => IsFeed && (TopicId is null || TopicId == messageTopicId);
}

/// <summary>
/// Owns unread-message counts surfaced as rail badges. Counts are sourced from
/// <see cref="HistoryRepository"/>'s <c>read</c> column and cached in memory:
/// the hot path (a message arriving) updates the cache incrementally, while
/// coarse changes (mark-read, topic add/remove, retention sweep) re-query.
///
/// A feed becomes "read" when the user navigates to it, when a message arrives
/// while it's the active view and the window is focused, or when the window
/// regains focus while it's the active view — the three triggers that together
/// keep the active feed's badge from getting stuck.
/// </summary>
public sealed class UnreadTracker
{
    private readonly HistoryRepository _history;
    private readonly object _lock = new();

    private Dictionary<Guid, int> _counts;
    private ActiveView _activeView = ActiveView.None;
    private bool _windowActive;

    public UnreadTracker(HistoryRepository history, EventBus bus)
    {
        _history = history;
        _counts = history.GetUnreadCounts();

        // Hot path: a message arrived — increment incrementally.
        bus.Subscribe<MessageInserted>(this, e => OnMessageInserted(e.Message));
        // Rows removed (feed Clear / delete-message / by-topic / retention sweep) —
        // incremental tracking can't see deletes, so re-seed from the DB. Topic-set
        // changes that affect counts always go through a delete, so this also covers
        // topic removal-with-history.
        bus.Subscribe<MessagesDeleted>(this, _ => Refresh());
    }

    public int Total
    {
        get { lock (_lock) return _counts.Values.Sum(); }
    }

    public int CountFor(Guid topicId)
    {
        lock (_lock) return _counts.GetValueOrDefault(topicId);
    }

    /// <summary>Re-seed the whole cache from the database. Used after deletes,
    /// retention sweeps and topic-set changes, where incremental tracking would
    /// drift.</summary>
    public void Refresh()
    {
        lock (_lock) _counts = _history.GetUnreadCounts();
        _ = new UnreadCountChanged(null).PublishAsync();
    }

    /// <summary>Record which feed is on screen. Navigating to a feed marks it
    /// read (explicit user action); navigating away (None) just stops
    /// auto-reading arrivals.</summary>
    public void SetActiveView(ActiveView view)
    {
        lock (_lock) _activeView = view;
        if (view.IsFeed)
            MarkViewRead(view);
    }

    /// <summary>Track window focus. Regaining focus marks the current feed read,
    /// covering messages that arrived while the window was hidden in the tray.</summary>
    public void SetWindowActive(bool active)
    {
        ActiveView view;
        lock (_lock)
        {
            _windowActive = active;
            view = _activeView;
        }
        if (active && view.IsFeed)
            MarkViewRead(view);
    }

    public void MarkAllRead()
    {
        _history.MarkAllRead();
        Refresh();
    }

    public void MarkTopicRead(Guid topicId)
    {
        _history.MarkTopicRead(topicId);
        Refresh();
    }

    private void MarkViewRead(ActiveView view)
    {
        if (view.TopicId is { } id) MarkTopicRead(id);
        else                        MarkAllRead();
    }

    private void OnMessageInserted(HistoryMessage m)
    {
        bool viewing;
        lock (_lock)
        {
            viewing = _windowActive && _activeView.Matches(m.TopicId);
            _counts[m.TopicId] = viewing ? 0 : _counts.GetValueOrDefault(m.TopicId) + 1;
        }

        // Persist the read state for the message we just chose not to count, so it
        // doesn't resurface as unread on the next launch.
        if (viewing)
            _history.MarkTopicRead(m.TopicId);

        _ = new UnreadCountChanged(m.TopicId).PublishAsync();
    }
}
