using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ReSharper disable MemberCanBePrivate.Global

namespace NtfyDesktop.Core.Messaging;

public sealed class EventBus(
    IMessenger messenger, 
    IUIDispatcher ui,
    ILogger<EventBus> logger,
    IServiceScopeFactory scopeFactory)
{
    public async Task PublishAsync<TEvent>(TEvent ev, PublishMode mode = PublishMode.WaitForAll, CancellationToken ct = default) where TEvent : IEvent
    {
        var tasks = new List<Task>();
        
        // lane 1 - instance subscribers (UI dispatch handled per-subscription)
        messenger.Send(new EventEnvelope<TEvent>() {
            Event = ev,
            CancellationToken = ct,
            Tasks = tasks
        });
        
        // lane 2 - DI-resolved class handlers, each publish in its own scope so scoped deps resolve
        var scope = scopeFactory.CreateScope();
        var classTasks = scope.ServiceProvider
            .GetServices<IEventHandler<TEvent>>()
            .Select(handler => SafeInvoke(handler, ev, ct))
            .ToArray();
        
        if (classTasks.Length == 0)
            scope.Dispose();
        else {

            tasks.AddRange(classTasks);

            // Dispose the scope only after its handlers finish — they may hold scoped deps.
            _ = Task.WhenAll(classTasks).ContinueWith(
                static (_, s) => ((IServiceScope) s!).Dispose(), scope,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            
        }

        if (tasks.Count == 0) return;

        switch (mode) {
            case PublishMode.WaitForAll:
                await Task.WhenAll(tasks).ConfigureAwait(false);
                break;
            case PublishMode.WaitForAny:
                await Task.WhenAny(tasks).ConfigureAwait(false);
                break;
            case PublishMode.WaitForNone:
                foreach (var task in tasks) Forget(task);
                break;
        }
        
    }
    
    private static Task SafeInvoke<TEvent>(IEventHandler<TEvent> h, TEvent e, CancellationToken ct)
        where TEvent : IEvent
    {
        try { return h.HandleAsync(e, ct); }
        catch (Exception ex) { return Task.FromException(ex); }  // catch sync throws from non-async handlers
    }
    
    public IDisposable Subscribe<TEvent>(object recipient, Func<TEvent, CancellationToken, Task> handler,
        ThreadOption threadOption = ThreadOption.PublisherThread) where TEvent : IEvent
    {
        messenger.Register<EventEnvelope<TEvent>>(recipient, (_, envelope) 
            => Dispatch(envelope, handler, threadOption));
        
        return new Unsubscriber(() => messenger.Unregister<EventEnvelope<TEvent>>(recipient));
    }

    public IDisposable Subscribe<TEvent>(object recipient, Action<TEvent> handler,
        ThreadOption threadOption = ThreadOption.PublisherThread) where TEvent : IEvent
        => Subscribe<TEvent>(recipient, (e, _) =>
        {
            handler(e);
            return Task.CompletedTask;
        }, threadOption);

    public IDisposable Subscribe<TRecipient, TEvent>(TRecipient recipient,
        Func<TRecipient, TEvent, CancellationToken, Task> handler,
        ThreadOption thread = ThreadOption.PublisherThread)
        where TRecipient : class where TEvent : IEvent
    {
        // The stored delegate captures `handler` (pass it static) but NOT the recipient —
        // `r` is supplied by the messenger at invocation, preserving weak semantics.
        messenger.Register<TRecipient, EventEnvelope<TEvent>>(recipient, (r, envelope) =>
            Dispatch(envelope, (e, ct) => handler(r, e, ct), thread));
        
        return new Unsubscriber(() => messenger.Unregister<EventEnvelope<TEvent>>(recipient));
    }
    
    private Task InvokeHandler<TEvent>(EventEnvelope<TEvent> env,
        Func<TEvent, CancellationToken, Task> handler, ThreadOption thread) where TEvent : IEvent
    {
        if (thread == ThreadOption.UIThread && !ui.IsOnUIThread())
            return ui.InvokeAsync(() => handler(env.Event, env.CancellationToken), env.CancellationToken);

        try { return handler(env.Event, env.CancellationToken); }  // already on the right thread
        catch (Exception ex) { return Task.FromException(ex); }     // catch sync throws from non-async handlers
    }

    private void Dispatch<TEvent>(EventEnvelope<TEvent> envelope, Func<TEvent, CancellationToken, Task> handler,
        ThreadOption threadOption) where TEvent : IEvent
        => envelope.Tasks.Add(InvokeHandler(envelope, handler, threadOption));

    private void Forget(Task task)
    {
        if (!task.IsFaulted) return;
        
        var exception = task.Exception.GetBaseException();

        task.ContinueWith(t => logger.LogError(exception, "Event handler failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Unsubscribe<TEvent>(object recipient) where TEvent : IEvent
        => messenger.Unregister<EventEnvelope<TEvent>>(recipient);
    
    public void UnsubscribeAll(object recipient) => messenger.UnregisterAll(recipient);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

}