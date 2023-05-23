using System;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class SignalRConnection<THub> : ISignalRConnection
{
    public SignalRConnection(Func<InvocationMessage, Task>? onNextAction = null)
    {
        OnNextAsync = onNextAction;
    }
    
    public async Task SendMessage(InvocationMessage message)
    {
        await OnNextAsync.Invoke(message);
        //return Task.CompletedTask;
    }
    
    public Func<InvocationMessage, Task>? OnNextAsync { get; set; }
}


public class SignalRAsyncObserver<T> : IAsyncObserver<T>, IDisposable
{
    public SignalRAsyncObserver(Func<T, Task>? onNextAction = null, Func<Exception, Task>? onErrorAction = null,
        Func<Task>? onCompletedAction = null)
    {
        OnNextAsync = onNextAction;
        OnErrorAsync = onErrorAction;
        OnCompletedAsync = onCompletedAction;
    }

    public Func<T, Task>? OnNextAsync { get; set; }
    public Func<Task>? OnCompletedAsync { get; set; }
    public Func<Exception, Task>? OnErrorAsync { get; set; }

    Task IAsyncObserver<T>.OnNextAsync(T item, StreamSequenceToken? token = null)
    {
        _ = Task.Run(() =>
        {
            OnNextAsync?.Invoke(item);
        });
        
        return Task.CompletedTask;
    }

    Task IAsyncObserver<T>.OnCompletedAsync()
    {
        _ = Task.Run(() =>
        {
            OnCompletedAsync?.Invoke();
        });
        
        return Task.CompletedTask;
    }

    Task IAsyncObserver<T>.OnErrorAsync(Exception ex)
    {
        _ = Task.Run(() =>
        {
            OnErrorAsync?.Invoke(ex);
        });
        
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        OnNextAsync = null;
        OnCompletedAsync = null;
        OnErrorAsync = null;
    }
}