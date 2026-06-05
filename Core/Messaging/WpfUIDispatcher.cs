using System.Windows.Threading;

namespace NtfyDesktop.Core.Messaging;

public sealed class WpfUIDispatcher(Dispatcher dispatcher) : IUIDispatcher
{
    public bool IsOnUIThread() => dispatcher.CheckAccess();

    public Task InvokeAsync(Func<Task> callback, CancellationToken ct = default)
        => dispatcher.InvokeAsync(callback, DispatcherPriority.Normal, ct).Task.Unwrap();
}