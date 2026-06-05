using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Topics.Events;

public record GroupMoved(string GroupName) : IEvent;