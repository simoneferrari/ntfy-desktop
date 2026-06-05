using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Topics.Events;

public record TopicUpdated(TopicSettings Topic) : IEvent;