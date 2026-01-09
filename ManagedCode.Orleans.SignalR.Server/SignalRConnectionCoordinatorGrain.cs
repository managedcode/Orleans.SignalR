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
    private readonly Dictionary<string, PartitionAssignment> _connectionPartitions;
    private readonly HashSet<int> _activePartitions;
    private readonly int _connectionsPerPartitionHint;
    private uint _basePartitionCount;
    private int _currentPartitionCount;
    private int _partitionEpoch;

    public SignalRConnectionCoordinatorGrain(
        ILogger<SignalRConnectionCoordinatorGrain> logger,
        IOptions<OrleansSignalROptions> options,
        [PersistentState(nameof(SignalRConnectionCoordinatorGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionCoordinatorState> state)
    {
        _logger = logger;
        _options = options;
        _state = state;
        _connectionPartitions = new Dictionary<string, PartitionAssignment>(StringComparer.Ordinal);
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
            _activePartitions.Add(kvp.Value.PartitionId);
        }

        _state.State.ConnectionPartitions = _connectionPartitions;
        _basePartitionCount = Math.Max(1u, _options.Value.ConnectionPartitionCount);
        _currentPartitionCount = _state.State.CurrentPartitionCount;
        _partitionEpoch = Math.Max(1, _state.State.PartitionEpoch);

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
            // Reset epoch when scaling back to base with no connections
            _partitionEpoch = 1;
            _state.State.PartitionEpoch = _partitionEpoch;
        }

        _logger.LogInformation(
            "Connection coordinator activated with base partition count {PartitionCount}, current {CurrentPartitionCount}, epoch {Epoch}, hint {ConnectionsPerPartition}, tracked connections {TrackedConnections}",
            _basePartitionCount,
            _currentPartitionCount,
            _partitionEpoch,
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
        var (partition, wasNew, wasReassigned) = GetOrAssignPartitionWithEpoch(connectionId);
        stopwatch.Stop();

        if (stopwatch.Elapsed > TimeSpan.FromMilliseconds(500))
        {
            _logger.LogWarning(
                "GetPartitionForConnection for {ConnectionId} took {Elapsed} (tracked={Tracked})",
                connectionId,
                stopwatch.Elapsed,
                _connectionPartitions.Count);
        }

        // Persist state if a new partition was assigned or reassigned due to epoch change
        if (wasNew || wasReassigned)
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
            var (partition, _, _) = GetOrAssignPartitionWithEpoch(connectionId);
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
        var (partition, _, _) = GetOrAssignPartitionWithEpoch(connectionId);
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
            var (partition, _, _) = GetOrAssignPartitionWithEpoch(connectionId);
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
        if (_connectionPartitions.Remove(connectionId, out var removedAssignment))
        {
            var removedPartition = removedAssignment.PartitionId;
            _logger.LogDebug("Removed connection {ConnectionId} from coordinator mapping (partition {Partition}, epoch {Epoch}).",
                connectionId, removedPartition, removedAssignment.Epoch);

            // Check if any other connections are using this partition
            var partitionStillActive = false;
            foreach (var assignment in _connectionPartitions.Values)
            {
                if (assignment.PartitionId == removedPartition)
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
                _logger.LogDebug("Resetting partition count to base value {PartitionCount} and epoch to 1 as no active connections remain.", _basePartitionCount);
                _currentPartitionCount = (int)_basePartitionCount;
                _state.State.CurrentPartitionCount = _currentPartitionCount;
                _partitionEpoch = 1;
                _state.State.PartitionEpoch = _partitionEpoch;
                _activePartitions.Clear();
            }

            // Persist state changes to ensure consistency after reactivation
            await _state.WriteStateAsync();
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _state.State.CurrentPartitionCount = _currentPartitionCount;
        _state.State.PartitionEpoch = _partitionEpoch;

        if (_connectionPartitions.Count == 0)
        {
            await _state.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _state.WriteStateAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Gets or assigns a partition for a connection, handling epoch-based reassignment.
    /// Returns (partitionId, wasNew, wasReassigned).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int PartitionId, bool WasNew, bool WasReassigned) GetOrAssignPartitionWithEpoch(string connectionId)
    {
        if (_connectionPartitions.TryGetValue(connectionId, out var existingAssignment))
        {
            // Check if assignment is from current epoch
            if (existingAssignment.Epoch == _partitionEpoch)
            {
                return (existingAssignment.PartitionId, false, false);
            }

            // Stale epoch - check if partition would be different with current partition count
            var newPartition = PartitionHelper.GetPartitionId(connectionId, (uint)_currentPartitionCount);

            if (newPartition == existingAssignment.PartitionId)
            {
                // Same partition, just update epoch
                var updatedAssignment = PartitionAssignment.Create(existingAssignment.PartitionId, _partitionEpoch);
                _connectionPartitions[connectionId] = updatedAssignment;
                _logger.LogDebug(
                    "Updated connection {ConnectionId} epoch from {OldEpoch} to {NewEpoch} (partition {Partition} unchanged)",
                    connectionId, existingAssignment.Epoch, _partitionEpoch, existingAssignment.PartitionId);
                return (existingAssignment.PartitionId, false, true);
            }

            // Partition changed due to scaling - reassign
            // Note: The old partition may still have this connection until cleanup
            var reassignment = PartitionAssignment.Create(newPartition, _partitionEpoch);
            _connectionPartitions[connectionId] = reassignment;
            _activePartitions.Add(newPartition);

            _logger.LogInformation(
                "Reassigned connection {ConnectionId} from partition {OldPartition} (epoch {OldEpoch}) to partition {NewPartition} (epoch {NewEpoch}) due to scaling",
                connectionId, existingAssignment.PartitionId, existingAssignment.Epoch, newPartition, _partitionEpoch);

            return (newPartition, false, true);
        }

        // New connection - assign to partition with current epoch
        var partitionCount = EnsurePartitionCapacity(_connectionPartitions.Count + 1);
        var partition = PartitionHelper.GetPartitionId(connectionId, (uint)partitionCount);
        var assignment = PartitionAssignment.Create(partition, _partitionEpoch);

        _connectionPartitions[connectionId] = assignment;
        _activePartitions.Add(partition);

        _logger.LogDebug(
            "Assigned connection {ConnectionId} to partition {Partition} (epoch {Epoch}, partitionCount={PartitionCount})",
            connectionId, partition, _partitionEpoch, partitionCount);

        return (partition, true, false);
    }

    private int EnsurePartitionCapacity(int prospectiveConnections)
    {
        var desired = Math.Max((int)_basePartitionCount,
            PartitionHelper.GetOptimalPartitionCount(prospectiveConnections, _connectionsPerPartitionHint));

        if (desired > _currentPartitionCount)
        {
            _logger.LogInformation(
                "Increasing connection partition count from {OldPartitionCount} to {NewPartitionCount} (epoch {OldEpoch} -> {NewEpoch}) for {ConnectionCount} tracked connections.",
                _currentPartitionCount,
                desired,
                _partitionEpoch,
                _partitionEpoch + 1,
                prospectiveConnections);

            _currentPartitionCount = desired;
            _partitionEpoch++;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
            _state.State.PartitionEpoch = _partitionEpoch;
        }

        return _currentPartitionCount;
    }

    private static Dictionary<string, PartitionAssignment> EnsureOrdinalDictionary(Dictionary<string, PartitionAssignment>? dictionary)
    {
        if (dictionary is null)
        {
            return new Dictionary<string, PartitionAssignment>(StringComparer.Ordinal);
        }

        if (dictionary.Comparer == StringComparer.Ordinal)
        {
            return dictionary;
        }

        return new Dictionary<string, PartitionAssignment>(dictionary, StringComparer.Ordinal);
    }
}
