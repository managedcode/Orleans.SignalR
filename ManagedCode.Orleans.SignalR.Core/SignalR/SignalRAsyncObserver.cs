using System;
using System.Threading.Tasks;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class SignalRAsyncObserver<T> : IAsyncObserver<T>, IDisposable
{
    public SignalRAsyncObserver(Func<T, Task>? onNextAction = null, Func<Exception, Task>? onErrorAction = null,
        Func<Task>? onCompletedAction = null)
    {
        OnNextAsync = onNextAction;
        OnErrorAsync = onErrorAction;
        OnCompletedAsync = onCompletedAction;
    }

    public Func<T, Task> OnNextAsync { get; set; }
    public Func<Task> OnCompletedAsync { get; set; }
    public Func<Exception, Task> OnErrorAsync { get; set; }

    Task IAsyncObserver<T>.OnNextAsync(T item, StreamSequenceToken? token = null)
    {
        OnNext?.Invoke(this, item);
        return OnNextAsync?.Invoke(item) ?? Task.CompletedTask;
    }

    Task IAsyncObserver<T>.OnCompletedAsync()
    {
        OnCompleted?.Invoke(this, EventArgs.Empty);
        return OnCompletedAsync?.Invoke() ?? Task.CompletedTask;
    }

    Task IAsyncObserver<T>.OnErrorAsync(Exception ex)
    {
        OnError?.Invoke(this, ex);
        return OnErrorAsync?.Invoke(ex) ?? Task.CompletedTask;
    }

    public event EventHandler<T> OnNext;
    public event EventHandler OnCompleted;
    public event EventHandler<Exception> OnError;
    public void Dispose()
    {
        OnNext = null;
        OnCompleted  = null;
        OnError = null;
        OnNextAsync = null;
        OnCompletedAsync = null;
        OnErrorAsync = null;
    }
}