using System;
using System.Collections.Generic;
using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class Subscription(SignalRObserver observer) : IDisposable
{
    private readonly HashSet<IObserverConnectionManager> _grains = new();
    private bool _disposed;

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
        observer?.Dispose();
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
        return observer;
    }
}
