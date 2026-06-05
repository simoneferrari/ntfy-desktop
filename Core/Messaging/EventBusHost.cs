namespace NtfyDesktop.Core.Messaging;

public static class EventBusHost
{
    private static EventBus? _eventBus;

    public static EventBus Current 
        => _eventBus ?? throw new InvalidOperationException("Event bus not initialized.");
    
    public static void Initialize(EventBus eventBus) 
        => _eventBus = eventBus;
    
}