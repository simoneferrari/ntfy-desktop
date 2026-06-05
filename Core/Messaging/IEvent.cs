namespace NtfyDesktop.Core.Messaging;

public interface IEvent;

public interface IEventHandler<in TEvent> where TEvent : IEvent
{
    Task HandleAsync(TEvent eventModel, CancellationToken ct);
}