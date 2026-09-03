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
    // Use ConcurrentDictionary as a concurrent hash-set because batch group mutations can overlap disconnect cleanup.
    private readonly ConcurrentDictionary<IObserverConnectionManager, bool> _grains = new();
    private readonly ConcurrentDictionary<GrainId, bool> _heartbeatGrainIds = new();
    private bool _disposed;

    public ISignalRObserver Reference { get; private set; } = default!;

    public string? HubKey { get; private set; }

    public bool UsePartitioning { get; private set; }

    public int PartitionId { get; private set; }

    public IReadOnlyCollection<IObserverConnectionManager> Grains => _grains.IsEmpty ? [] : [.. _grains.Keys];

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
        _heartbeatGrainIds.Clear();
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

        _grains.TryAdd(grain, true);
        _heartbeatGrainIds.TryAdd(((GrainReference)grain).GrainId, true);

        if (Volatile.Read(ref _disposed))
        {
            _grains.TryRemove(grain, out _);
            _heartbeatGrainIds.TryRemove(((GrainReference)grain).GrainId, out _);
        }
    }

    public void RemoveGrain(IObserverConnectionManager grain)
    {
        _grains.TryRemove(grain, out _);
        _heartbeatGrainIds.TryRemove(((GrainReference)grain).GrainId, out _);
    }

    public void ClearGrains()
    {
        _grains.Clear();
        _heartbeatGrainIds.Clear();
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
        if (_heartbeatGrainIds.IsEmpty)
        {
            return ImmutableArray<GrainId>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GrainId>(_heartbeatGrainIds.Count);
        foreach (var grainId in _heartbeatGrainIds.Keys)
        {
            builder.Add(grainId);
        }

        return builder.MoveToImmutable();
    }
}
