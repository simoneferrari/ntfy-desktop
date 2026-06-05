using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.History.Events;

public record MessageInserted(HistoryMessage Message) : IEvent;