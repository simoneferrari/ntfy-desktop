namespace NtfyDesktop.Core.Messaging;

public enum PublishMode
{
    /// <summary>
    /// Awaits every handler
    /// </summary>
    WaitForAll,
    
    /// <summary>
    /// Awaits until the first handler completes
    /// </summary>
    WaitForAny,
    
    /// <summary>
    /// Fire-and-forget; handler faults go to the error callback
    /// </summary>
    WaitForNone
}