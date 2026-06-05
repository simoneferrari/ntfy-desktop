using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Unread.Events;

public record UnreadCountChanged(Guid? TopicId) : IEvent;