using System;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class SignalRObserver(Func<HubMessage, Task> onNextAction) : ISignalRObserver, IDisposable
{
    private Func<HubMessage, Task>? _onNextAction = onNextAction;

    public void Dispose()
    {
        _onNextAction = null;
    }

    public async Task OnNextAsync(HubMessage message)
    {
        if (_onNextAction != null)
        {
            await _onNextAction.Invoke(message);
        }
    }
    
    public bool IsExist => _onNextAction != null;
}