using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class Subscription : IDisposable
{
    private readonly SignalRObserver _observer;
    private IDisposable _timer;
    private readonly CancellationTokenSource _cts = new();
    public Subscription(SignalRObserver observer, TimeSpan pingTime)
    {
        _observer = observer;
        _timer = new Timer(Callback, this,pingTime,pingTime);
    }

    private void Callback(object? state)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            if(token.IsCancellationRequested)
                return;
            
            foreach (var grain in Grains)
            {
                if(token.IsCancellationRequested)
                    return;
                
                await grain.Ping(Reference);
            }
        }, _cts.Token).ConfigureAwait(false);
    }

    public void SetReference(ISignalRObserver reference)
    {
        Reference = reference;
    }

    public SignalRObserver GetObserver() => _observer;

    public ISignalRObserver Reference { get; private set; }
    
    public HashSet<IObserverConnectionManager> Grains { get; } = new();

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _observer.Dispose();
        Reference = null!;
    }
}