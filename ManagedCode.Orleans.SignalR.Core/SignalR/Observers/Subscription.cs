using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class Subscription(SignalRObserver observer) : IDisposable
{
    private readonly HashSet<IObserverConnectionManager> _grains = new();
    private readonly HashSet<GrainId> _heartbeatGrainIds = new();
    private bool _disposed;

    ~Subscription()
    {
        Dispose();
    }

    public ISignalRObserver Reference { get; private set; } = default!;

    public string? HubKey { get; private set; }

    public bool UsePartitioning { get; private set; }

    public int PartitionId { get; private set; }

    public IReadOnlyCollection<IObserverConnectionManager> Grains => _grains;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
        _grains.Add(grain);
        _heartbeatGrainIds.Add(((GrainReference)grain).GrainId);
    }

    public void RemoveGrain(IObserverConnectionManager grain)
    {
        _grains.Remove(grain);
        _heartbeatGrainIds.Remove(((GrainReference)grain).GrainId);
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
        if (_heartbeatGrainIds.Count == 0)
        {
            return ImmutableArray<GrainId>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GrainId>(_heartbeatGrainIds.Count);
        foreach (var grainId in _heartbeatGrainIds)
        {
            builder.Add(grainId);
        }

        return builder.MoveToImmutable();
    }
}
