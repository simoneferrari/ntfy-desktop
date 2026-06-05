namespace NtfyDesktop.Core.Messaging;

public static class EventBusExtensions
{
    extension<TEvent>(TEvent @event) where TEvent : IEvent
    {
        public Task PublishAsync(PublishMode mode = PublishMode.WaitForNone,
            CancellationToken ct = default) => EventBusHost.Current.PublishAsync(@event, mode, ct);

        // public void Publish() => _ = EventBusHost.Current.PublishAsync(@event, PublishMode.WaitForNone, CancellationToken.None);
    }
}