using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Connections.Events;

public record TopicConnectionStatusChanged(Guid TopicId, TopicConnectionStatus Status, string? LastError) : IEvent;