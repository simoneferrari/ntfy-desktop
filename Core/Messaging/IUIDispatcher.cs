namespace NtfyDesktop.Core.Messaging;

public interface IUIDispatcher
{
    bool IsOnUIThread();
    Task InvokeAsync(Func<Task> callback, CancellationToken ct = default);
}