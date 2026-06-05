using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Topics.Events;

public record TopicDeleted(Guid TopicId) : IEvent;