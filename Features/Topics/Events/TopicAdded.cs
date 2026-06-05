using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Topics.Events;

public record TopicAdded(TopicSettings Topic) : IEvent;