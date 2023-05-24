using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans.Runtime;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class Subscription : IDisposable
{
    public Subscription()
    {
    }
    
    public Subscription(SignalRObserver observer)
    {
        Observer = observer;
    }
    
    public SignalRObserver Observer { get; set; }
    public ISignalRObserver Reference { get; set; }
    
    public HashSet<GrainId> GrainIds { get; } = new();

    public void Dispose()
    {
        Observer.Dispose();
        Reference = null!;
    }
}


public class SignalRObserver : ISignalRObserver, IDisposable
{
    public SignalRObserver(Func<HubMessage, Task>? onNextAction = null)
    {
        _onNextAction = onNextAction;
    }

    private Func<HubMessage, Task>? _onNextAction;

    public async Task OnNextAsync(HubMessage message)
    {
        if (_onNextAction is not null)
            await _onNextAction.Invoke(message);
    }

    public void Dispose()
    {
        _onNextAction = null;
    }
}


public class SignalRAsyncObserver<HubMessage> : IAsyncObserver<HubMessage>, IDisposable
{
    public SignalRAsyncObserver(Func<HubMessage, Task>? onNextAction = null, Func<Exception, Task>? onErrorAction = null,
        Func<Task>? onCompletedAction = null)
    {
        _onNextAction = onNextAction;
        _onErrorAction = onErrorAction;
        _onCompletedAction = onCompletedAction;
    }

    private Func<HubMessage, Task>? _onNextAction;
    private Func<Task>? _onCompletedAction;
    private Func<Exception, Task>? _onErrorAction;

    public async Task OnNextAsync(HubMessage message, StreamSequenceToken? token = null)
    {
        if (_onNextAction is not null)
            await _onNextAction.Invoke(message);
    }
    public async  Task OnCompletedAsync()
    {
        if (_onCompletedAction is not null)
            await _onCompletedAction.Invoke();
    }

    public async Task OnErrorAsync(Exception ex)
    {
        if (_onErrorAction is not null)
            await _onErrorAction.Invoke(ex);
    }
    
    public void Dispose()
    {
        _onNextAction = null;
        _onErrorAction = null;
        _onCompletedAction = null;
    }
}