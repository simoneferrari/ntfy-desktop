using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Notifications.Events;

public record TopicNotificationsStatusChanged(Guid TopicId, bool IsPaused) : IEvent;
