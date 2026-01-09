using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRConnectionCoordinatorGrain)}")]
public sealed class SignalRConnectionCoordinatorGrain : Grain, ISignalRConnectionCoordinatorGrain
{
    private readonly ILogger<SignalRConnectionCoordinatorGrain> _logger;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly IPersistentState<ConnectionCoordinatorState> _state;
    private readonly Dictionary<string, int> _connectionPartitions;
    private readonly HashSet<int> _activePartitions;
    private readonly int _connectionsPerPartitionHint;
    private uint _basePartitionCount;
    private int _currentPartitionCount;

    public SignalRConnectionCoordinatorGrain(
        ILogger<SignalRConnectionCoordinatorGrain> logger,
        IOptions<OrleansSignalROptions> options,
        [PersistentState(nameof(SignalRConnectionCoordinatorGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionCoordinatorState> state)
    {
        _logger = logger;
        _options = options;
        _state = state;
        _connectionPartitions = new Dictionary<string, int>(StringComparer.Ordinal);
        _activePartitions = new HashSet<int>();
        _connectionsPerPartitionHint = Math.Max(1, _options.Value.ConnectionsPerPartitionHint);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await _state.ReadStateAsync(cancellationToken);
        _state.State ??= new ConnectionCoordinatorState();
        var partitions = EnsureOrdinalDictionary(_state.State.ConnectionPartitions);
        _connectionPartitions.Clear();
        _activePartitions.Clear();
        foreach (var kvp in partitions)
        {
            _connectionPartitions[kvp.Key] = kvp.Value;
            _activePartitions.Add(kvp.Value);
        }
        _state.State.ConnectionPartitions = _connectionPartitions;
        _basePartitionCount = Math.Max(1u, _options.Value.ConnectionPartitionCount);
        _currentPartitionCount = _state.State.CurrentPartitionCount;

        // Ensure partition count is at least base, but preserve higher counts to maintain routing consistency
        if (_currentPartitionCount <= 0 || _currentPartitionCount < _basePartitionCount)
        {
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
        }
        // Only reset to base if truly empty AND partition count was scaled up
        // This preserves routing consistency for connections that might reconnect
        else if (_connectionPartitions.Count == 0 && _currentPartitionCount > _basePartitionCount)
        {
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
        }

        _logger.LogInformation(
            "Connection coordinator activated with base partition count {PartitionCount}, current {CurrentPartitionCount}, hint {ConnectionsPerPartition}, tracked connections {TrackedConnections}",
            _basePartitionCount,
            _currentPartitionCount,
            _connectionsPerPartitionHint,
            _connectionPartitions.Count);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        return Task.FromResult(_currentPartitionCount);
    }

    public async Task<int> GetPartitionForConnection(string connectionId)
    {
        var stopwatch = Stopwatch.StartNew();
        var wasNew = !_connectionPartitions.ContainsKey(connectionId);
        var partition = GetOrAssignPartition(connectionId);
        stopwatch.Stop();

        if (stopwatch.Elapsed > TimeSpan.FromMilliseconds(500))
        {
            _logger.LogWarning(
                "GetPartitionForConnection for {ConnectionId} took {Elapsed} (tracked={Tracked})",
                connectionId,
                stopwatch.Elapsed,
                _connectionPartitions.Count);
        }

        // Persist state if a new partition was assigned to ensure consistency after reactivation
        if (wasNew)
        {
            await _state.WriteStateAsync();
        }

        return partition;
    }

    public async Task SendToAll(HubMessage message)
    {
        var partitionCount = _activePartitions.Count;
        if (partitionCount == 0)
        {
            return;
        }

        // Use ArrayPool for task collection to reduce allocations
        var tasks = ArrayPool<Task>.Shared.Rent(partitionCount);
        try
        {
            var hubKey = this.GetPrimaryKeyString();
            var taskIndex = 0;

            foreach (var partitionId in _activePartitions)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, hubKey, partitionId);
                tasks[taskIndex++] = partitionGrain.SendToPartition(message);
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(tasks, clearArray: true);
        }
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        var partitionCount = _activePartitions.Count;
        if (partitionCount == 0)
        {
            return;
        }

        // Group excluded connections by partition using CollectionsMarshal for efficient access
        var excludedByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in excludedConnectionIds)
        {
            var partition = GetOrAssignPartition(connectionId);
            ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(excludedByPartition, partition, out var exists);
            if (!exists)
            {
                list = new List<string>();
            }
            list!.Add(connectionId);
        }

        // Use ArrayPool for task collection
        var tasks = ArrayPool<Task>.Shared.Rent(partitionCount);
        try
        {
            var hubKey = this.GetPrimaryKeyString();
            var taskIndex = 0;

            foreach (var partitionId in _activePartitions)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, hubKey, partitionId);
                var excluded = excludedByPartition.TryGetValue(partitionId, out var list)
                    ? CollectionsMarshal.AsSpan(list).ToArray()
                    : [];
                tasks[taskIndex++] = partitionGrain.SendToPartitionExcept(message, excluded);
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(tasks, clearArray: true);
        }
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        var partition = GetOrAssignPartition(connectionId);
        var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        return await partitionGrain.SendToConnection(message, connectionId);
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        if (connectionIds.Length == 0)
        {
            return;
        }

        // Group connections by partition using CollectionsMarshal for efficient access
        var connectionsByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in connectionIds)
        {
            var partition = GetOrAssignPartition(connectionId);
            ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(connectionsByPartition, partition, out var exists);
            if (!exists)
            {
                list = new List<string>();
            }
            list!.Add(connectionId);
        }

        if (connectionsByPartition.Count == 0)
        {
            return;
        }

        // Use ArrayPool for task collection
        var tasks = ArrayPool<Task>.Shared.Rent(connectionsByPartition.Count);
        try
        {
            var hubKey = this.GetPrimaryKeyString();
            var taskIndex = 0;

            foreach (var kvp in connectionsByPartition)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, hubKey, kvp.Key);
                tasks[taskIndex++] = partitionGrain.SendToConnections(message, CollectionsMarshal.AsSpan(kvp.Value).ToArray());
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(tasks, clearArray: true);
        }
    }

    public async Task NotifyConnectionRemoved(string connectionId)
    {
        if (_connectionPartitions.Remove(connectionId, out var removedPartition))
        {
            _logger.LogDebug("Removed connection {ConnectionId} from coordinator mapping (partition {Partition}).", connectionId, removedPartition);

            // Check if any other connections are using this partition
            var partitionStillActive = false;
            foreach (var partition in _connectionPartitions.Values)
            {
                if (partition == removedPartition)
                {
                    partitionStillActive = true;
                    break;
                }
            }

            if (!partitionStillActive)
            {
                _activePartitions.Remove(removedPartition);
            }

            if (_connectionPartitions.Count == 0 && _currentPartitionCount != _basePartitionCount)
            {
                _logger.LogDebug("Resetting partition count to base value {PartitionCount} as no active connections remain.", _basePartitionCount);
                _currentPartitionCount = (int)_basePartitionCount;
                _state.State.CurrentPartitionCount = _currentPartitionCount;
                _activePartitions.Clear();
            }

            // Persist state changes to ensure consistency after reactivation
            await _state.WriteStateAsync();
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _state.State.CurrentPartitionCount = _currentPartitionCount;
        if (_connectionPartitions.Count == 0)
        {
            await _state.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _state.WriteStateAsync(cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrAssignPartition(string connectionId)
    {
        if (_connectionPartitions.TryGetValue(connectionId, out var partition))
        {
            return partition;
        }

        var partitionCount = EnsurePartitionCapacity(_connectionPartitions.Count + 1);
        partition = PartitionHelper.GetPartitionId(connectionId, (uint)partitionCount);
        _connectionPartitions[connectionId] = partition;
        _activePartitions.Add(partition);

        _logger.LogDebug("Assigned connection {ConnectionId} to partition {Partition} (partitionCount={PartitionCount})", connectionId, partition, partitionCount);
        return partition;
    }

    private int EnsurePartitionCapacity(int prospectiveConnections)
    {
        var desired = Math.Max((int)_basePartitionCount,
            PartitionHelper.GetOptimalPartitionCount(prospectiveConnections, _connectionsPerPartitionHint));

        if (desired > _currentPartitionCount)
        {
            _logger.LogInformation(
                "Increasing connection partition count from {OldPartitionCount} to {NewPartitionCount} for {ConnectionCount} tracked connections.",
                _currentPartitionCount,
                desired,
                prospectiveConnections);
            _currentPartitionCount = desired;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
        }

        return _currentPartitionCount;
    }

    private static Dictionary<string, int> EnsureOrdinalDictionary(Dictionary<string, int>? dictionary)
    {
        if (dictionary is null)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        if (dictionary.Comparer == StringComparer.Ordinal)
        {
            return dictionary;
        }

        return new Dictionary<string, int>(dictionary, StringComparer.Ordinal);
    }
}
