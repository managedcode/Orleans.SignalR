using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class Subscription : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SignalRObserver _observer;
    private readonly IDisposable _timer;

    public Subscription(SignalRObserver observer, TimeSpan pingTime)
    {
        _observer = observer;
        _timer = new Timer(Callback, this, pingTime, pingTime);
    }

    public ISignalRObserver Reference { get; private set; }

    public HashSet<IObserverConnectionManager> Grains { get; } = new();

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _observer.Dispose();
        Reference = null!;
    }

    private void Callback(object? state)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            if (token.IsCancellationRequested)
                return;

            foreach (var grain in Grains)
            {
                if (token.IsCancellationRequested)
                    return;

                await grain.Ping(Reference).ConfigureAwait(false);
            }
        }, _cts.Token).ConfigureAwait(false);
    }

    public void SetReference(ISignalRObserver reference)
    {
        Reference = reference;
    }

    public SignalRObserver GetObserver()
    {
        return _observer;
    }
}