namespace NtfyDesktop.Core.Messaging;

internal sealed class EventEnvelope<TEvent> where TEvent : IEvent
{
    public required TEvent Event { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public required List<Task> Tasks { get; init; }
}