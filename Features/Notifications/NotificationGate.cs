using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Notifications.Events;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Notifications;

// Owns the notification-pause read/write surface. Writes through to
// AppSettings.IsPaused (global) and TopicSettings.IsPaused (per topic).
//
// Per-topic pause is keyed by TopicId (topic names are no longer unique across
// servers). The pause axis is independent of the socket: sockets stay open while
// paused, messages still arrive and are persisted, only the toast is suppressed
// (see ShowToastNotification).
//
// Pause changes are broadcast on the bus: NotificationsStatusChanged (global) and
// TopicNotificationsStatusChanged (per topic). Fire-and-forget — consumers are UI
// surfaces that re-read / update targeted state.
public sealed class NotificationGate(AppSettings settings)
{
    public NotificationStatus GlobalStatus =>
        settings.IsPaused ? NotificationStatus.Paused : NotificationStatus.Active;

    public bool IsGloballyPaused => settings.IsPaused;

    public bool IsTopicPaused(Guid topicId)
    {
        if (settings.IsPaused) return true;
        return settings.GetTopicById(topicId)?.IsPaused ?? false;
    }

    // True only when the per-topic flag is set (ignores global pause).
    // Used by UI surfaces that need to distinguish "topic-specific pause" from
    // "everything paused".
    public bool IsTopicSpecificallyPaused(Guid topicId) =>
        settings.GetTopicById(topicId)?.IsPaused ?? false;

    public void PauseAll()
    {
        if (settings.IsPaused) return;
        settings.IsPaused = true;
        settings.Save();
        new NotificationsStatusChanged().PublishAsync();
    }

    public void ResumeAll()
    {
        if (!settings.IsPaused) return;
        settings.IsPaused = false;
        settings.Save();
        new NotificationsStatusChanged().PublishAsync();
    }

    public void PauseTopic(Guid topicId)
    {
        var t = settings.GetTopicById(topicId);
        if (t is null || t.IsPaused) return;
        t.IsPaused = true;
        settings.Save();
        new TopicNotificationsStatusChanged(topicId, true).PublishAsync();
    }

    public void ResumeTopic(Guid topicId)
    {
        var t = settings.GetTopicById(topicId);
        if (t is null || !t.IsPaused) return;
        t.IsPaused = false;
        settings.Save();
        new TopicNotificationsStatusChanged(topicId, false).PublishAsync();
    }
}
