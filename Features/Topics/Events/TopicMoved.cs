using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Topics.Events;

public record TopicMoved(Guid TopicId) : IEvent;