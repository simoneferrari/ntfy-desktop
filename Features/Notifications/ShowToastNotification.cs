using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics;

namespace NtfyDesktop.Features.Notifications;

/// <summary>
/// Displays a toast notification when an Ntfy message is received, if configuration allows it.
/// Topic-scoped checks key off the event's TopicId (topic names aren't unique across servers).
/// Pause checks go through NotificationGate so this class doesn't have to know
/// how pause is persisted.
/// </summary>
public class ShowToastNotification(
    AppSettings settings,
    NotificationGate gate,
    ToastNotifier toaster,
    BackfillSummaryNotifier summary) : IEventHandler<NtfyMessageReceived>
{
    private ActiveHours ResolveActiveHours(TopicSettings? topicSettings)
    {
        return topicSettings is not null
            ? new(topicSettings.ActiveHoursEnabled ?? false, topicSettings.ActiveHoursStart, topicSettings.ActiveHoursEnd)
            : new(settings.ActiveHoursEnabled, settings.ActiveHoursStart, settings.ActiveHoursEnd);
    }

    private bool DropMessage(NtfyMessage message, Guid topicId)
    {
        // drop if notifications are paused (globally or for this topic)
        if (gate.IsTopicPaused(topicId)) return true;

        var topicSettings = settings.GetTopicById(topicId);

        // drop if below min priority threshold
        var minPriority = topicSettings?.MinPriority ?? settings.GlobalMinPriority;
        if (message.Priority < minPriority) return true;

        // Resolve active hours (per-topic overrides global when non-null)
        var activeHours = ResolveActiveHours(topicSettings);

        // drop when active hours are enabled and current time is excluded
        if (activeHours.Enabled && activeHours.Excludes(TimeOnly.FromDateTime(DateTime.Now)))
            return true;

        return false;
    }

    public Task HandleAsync(NtfyMessageReceived eventModel, CancellationToken ct)
    {
        var message = eventModel.Message;

        // The rule engine suppressed the toast for this message: no toast, and don't
        // count it in the catch-up summary either. (Feed-hiding is a separate axis,
        // applied via the stored suppressed flag — a correlated resolution still toasts.)
        if (eventModel.SuppressToast)
            return Task.CompletedTask;

        // Same gate for live and backfilled messages: pause, below-min-priority and
        // outside-active-hours all mean "don't notify". A gated message is neither
        // toasted nor counted in the catch-up summary.
        if (DropMessage(message, eventModel.TopicId))
            return Task.CompletedTask;

        // Backfilled (catch-up) messages don't each pop a toast — they're coalesced into
        // a single "N messages while you were away" summary, so a reconnect that replays
        // a backlog doesn't spray one toast per missed message.
        if (eventModel.IsBackfill)
            summary.Record(eventModel.TopicId);
        else
            toaster.Show(message, eventModel.TopicId);

        return Task.CompletedTask;
    }
}
