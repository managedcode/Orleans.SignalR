using System;
using System.Threading.Tasks;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

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