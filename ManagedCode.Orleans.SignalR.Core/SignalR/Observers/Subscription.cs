using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public sealed class Subscription(SignalRObserver observer) : IDisposable
{
    // Heartbeat and disconnect use the same registration map while group membership changes.
    private readonly ConcurrentDictionary<GrainId, IObserverConnectionManager> _grains = new();
    private bool _disposed;

    public ISignalRObserver Reference { get; private set; } = default!;

    public string? HubKey { get; private set; }

    public bool UsePartitioning { get; private set; }

    public int PartitionId { get; private set; }

    public IReadOnlyCollection<IObserverConnectionManager> Grains => _grains.IsEmpty ? [] : [.. _grains.Values];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        DisposeCore();
    }

    internal void DisposeReference(Action<ISignalRObserver> deleteObjectReference)
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        var reference = Reference;
        DisposeCore();

        if (reference is not null)
        {
            deleteObjectReference(reference);
        }
    }

    private void DisposeCore()
    {
        observer?.Dispose();
        _grains.Clear();
        Reference = null!;
        HubKey = null;
        UsePartitioning = false;
        PartitionId = 0;
    }

    public void AddGrain(IObserverConnectionManager grain)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        var grainId = ((GrainReference)grain).GrainId;
        _grains.TryAdd(grainId, grain);

        if (Volatile.Read(ref _disposed))
        {
            _grains.TryRemove(grainId, out _);
        }
    }

    public void RemoveGrain(IObserverConnectionManager grain)
    {
        _grains.TryRemove(((GrainReference)grain).GrainId, out _);
    }

    public void ClearGrains()
    {
        _grains.Clear();
    }

    public void SetReference(ISignalRObserver reference)
    {
        Reference = reference;
    }

    public void SetConnectionMetadata(string hubKey, bool usePartitioning, int partitionId)
    {
        HubKey = hubKey;
        UsePartitioning = usePartitioning;
        PartitionId = partitionId;
    }

    public SignalRObserver GetObserver()
    {
        return observer;
    }

    public ImmutableArray<GrainId> GetHeartbeatGrainIds()
    {
        return ImmutableArray.CreateRange(_grains.Keys);
    }
}
