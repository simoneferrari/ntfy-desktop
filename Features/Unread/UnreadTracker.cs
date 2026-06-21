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

    /// <summary>True when this view is a single topic's feed showing the given message's
    /// topic. The combined "All topics" feed deliberately does NOT match — viewing the
    /// aggregate is a passive overview and must not mark messages read.</summary>
    public bool MarksRead(Guid messageTopicId) => IsFeed && TopicId == messageTopicId;
}

/// <summary>
/// Owns unread-message counts surfaced as rail badges. Counts are sourced from
/// <see cref="HistoryRepository"/>'s <c>read</c> column and cached in memory:
/// the hot path (a message arriving) updates the cache incrementally, while
/// coarse changes (mark-read, topic add/remove, retention sweep) re-query.
///
/// A topic becomes "read" only in the context of <em>its own</em> feed: when the
/// user navigates to that topic's feed, when a message arrives while that feed is
/// the active view and the window is focused, or when the window regains focus
/// while that feed is active. The combined "All topics" feed is a passive overview
/// and never marks anything read — otherwise opening the app (which lands there)
/// would wipe every unread badge, hiding which messages were missed. Bulk clearing
/// is explicit via <see cref="MarkTopicRead"/> / <see cref="MarkAllRead"/>.
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
        // A message was retroactively hidden (a problem folded by its resolution) — it
        // drops out of the suppressed-excluding count, so re-seed.
        bus.Subscribe<MessageSuppressed>(this, _ => Refresh());
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

    /// <summary>Record which feed is on screen. Navigating to a single topic's feed
    /// marks that topic read (explicit "I'm looking at this topic"); navigating to the
    /// All-topics feed or away (None) marks nothing.</summary>
    public void SetActiveView(ActiveView view)
    {
        lock (_lock) _activeView = view;
        if (view.TopicId is { } id)
            MarkTopicRead(id);
    }

    /// <summary>Track window focus. Regaining focus marks the active topic feed read,
    /// covering messages that arrived for it while the window was hidden in the tray.
    /// Only a single-topic feed qualifies — the All-topics overview is left untouched.</summary>
    public void SetWindowActive(bool active)
    {
        ActiveView view;
        lock (_lock)
        {
            _windowActive = active;
            view = _activeView;
        }
        if (active && view.TopicId is { } id)
            MarkTopicRead(id);
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

    private void OnMessageInserted(HistoryMessage m)
    {
        // Suppressed messages don't nag: they're excluded from the unread count
        // (GetUnreadCounts also filters them, so the seeded cache agrees).
        if (m.Suppressed) return;

        bool viewing;
        lock (_lock)
        {
            // Auto-read only while looking at this message's own topic feed (focused).
            // Arrivals shown in the All-topics overview stay unread until the topic is opened.
            viewing = _windowActive && _activeView.MarksRead(m.TopicId);
            _counts[m.TopicId] = viewing ? 0 : _counts.GetValueOrDefault(m.TopicId) + 1;
        }

        // Persist the read state for the message we just chose not to count, so it
        // doesn't resurface as unread on the next launch.
        if (viewing)
            _history.MarkTopicRead(m.TopicId);

        _ = new UnreadCountChanged(m.TopicId).PublishAsync();
    }
}
