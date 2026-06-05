using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Notifications;

/// <summary>
/// Coalesces backfilled (catch-up) messages into a single "N messages while you were
/// away" summary toast, instead of re-toasting each one on reconnect.
///
/// Backfill arrives as a burst over the socket after a connect with <c>since=</c>, and
/// ntfy sends no end-of-backlog delimiter — so we debounce: every recorded message resets
/// a short timer and the summary fires once the burst goes quiet. The caller
/// (<see cref="ShowToastNotification"/>) records only messages that would have notified
/// live (it applies the pause / priority / active-hours gate first), so the count reflects
/// notifications actually missed.
///
/// Singleton: <see cref="IEventHandler{T}"/> handlers are transient (new per message), so
/// the accumulator state has to live here.
/// </summary>
public sealed class BackfillSummaryNotifier(AppSettings settings, ToastNotifier toaster) : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _counts = [];
    private Timer? _timer;

    public void Record(Guid topicId)
    {
        lock (_lock)
        {
            _counts[topicId] = _counts.GetValueOrDefault(topicId) + 1;
            (_timer ??= new Timer(_ => Flush())).Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        Dictionary<Guid, int> batch;
        lock (_lock)
        {
            if (_counts.Count == 0) return;
            batch = new Dictionary<Guid, int>(_counts);
            _counts.Clear();
        }

        var total = batch.Values.Sum();
        var topics = batch
            .Select(kv => (Label: settings.GetTopicById(kv.Key)?.EffectiveDisplayName ?? "a topic", Count: kv.Value))
            .OrderByDescending(t => t.Count)
            .ToList();

        // One topic → deep-link the summary to it; several → open All topics (Guid.Empty
        // resolves to the combined feed in MainWindow.NavigateToTopic).
        var clickTopic = batch.Count == 1 ? batch.Keys.First() : Guid.Empty;

        toaster.ShowBackfillSummary(total, topics, clickTopic);
    }

    public void Dispose() => _timer?.Dispose();
}
