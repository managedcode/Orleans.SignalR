using System;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

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