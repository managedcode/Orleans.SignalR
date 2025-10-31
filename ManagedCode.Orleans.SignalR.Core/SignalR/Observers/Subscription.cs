using System;
using System.Collections.Generic;
using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class Subscription : IDisposable
{
    private readonly HashSet<IObserverConnectionManager> _grains = new();
    private readonly SignalRObserver _observer;
    private bool _disposed;

    public Subscription(SignalRObserver observer)
    {
        _observer = observer;
    }
    
    ~Subscription()
    {
        Dispose();
    }

    public ISignalRObserver Reference { get; private set; } = default!;

    public IReadOnlyCollection<IObserverConnectionManager> Grains => _grains;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _observer?.Dispose();
        _grains?.Clear();
        Reference = null!;
    }

    public void AddGrain(IObserverConnectionManager grain)
    {
        _grains.Add(grain);
    }

    public void RemoveGrain(IObserverConnectionManager grain)
    {
        _grains.Remove(grain);
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
