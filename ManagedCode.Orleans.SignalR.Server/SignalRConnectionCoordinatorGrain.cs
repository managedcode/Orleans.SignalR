using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRConnectionCoordinatorGrain)}")]
public class SignalRConnectionCoordinatorGrain(
    ILogger<SignalRConnectionCoordinatorGrain> logger,
    IOptions<OrleansSignalROptions> options,
    IClusterClient _) : Grain, ISignalRConnectionCoordinatorGrain
{
    private readonly Dictionary<string, int> _connectionPartitions = new(StringComparer.Ordinal);
    private readonly ILogger<SignalRConnectionCoordinatorGrain> _logger = logger;
    private readonly int _connectionsPerPartitionHint = Math.Max(1, options.Value.ConnectionsPerPartitionHint);
    private uint _basePartitionCount;
    private int _currentPartitionCount;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _basePartitionCount = Math.Max(1, options.Value.ConnectionPartitionCount);
        _currentPartitionCount = (int)_basePartitionCount;

        logger.LogInformation(
            "Connection coordinator activated with base partition count {PartitionCount} and hint {ConnectionsPerPartition}",
            _basePartitionCount,
            _connectionsPerPartitionHint);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        return Task.FromResult(_currentPartitionCount);
    }
    
    public Task<int> GetPartitionForConnection(string connectionId)
    {
        var partition = GetOrAssignPartition(connectionId);
        return Task.FromResult(partition);
    }

    public async Task SendToAll(HubMessage message)
    {
        var partitions = GetActivePartitions();
        if (partitions.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(partitions.Count);
        foreach (var partitionId in partitions)
        {
            var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partitionId);
            tasks.Add(partitionGrain.SendToPartition(message));
        }

        await Task.WhenAll(tasks);
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        var excludedByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in excludedConnectionIds)
        {
            var partition = GetOrAssignPartition(connectionId);
            if (!excludedByPartition.TryGetValue(partition, out var list))
            {
                list = new List<string>();
                excludedByPartition[partition] = list;
            }
            list.Add(connectionId);
        }

        var partitions = GetActivePartitions();
        if (partitions.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(partitions.Count);
        foreach (var partitionId in partitions)
        {
            var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partitionId);
            var excluded = excludedByPartition.TryGetValue(partitionId, out var list)
                ? list.ToArray()
                : Array.Empty<string>();
            tasks.Add(partitionGrain.SendToPartitionExcept(message, excluded));
        }

        await Task.WhenAll(tasks);
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        var partition = GetOrAssignPartition(connectionId);
        var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        return await partitionGrain.SendToConnection(message, connectionId);
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        var connectionsByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in connectionIds)
        {
            var partition = GetOrAssignPartition(connectionId);
            if (!connectionsByPartition.TryGetValue(partition, out var list))
            {
                list = new List<string>();
                connectionsByPartition[partition] = list;
            }
            list.Add(connectionId);
        }

        if (connectionsByPartition.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(connectionsByPartition.Count);
        foreach (var kvp in connectionsByPartition)
        {
            var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), kvp.Key);
            tasks.Add(partitionGrain.SendToConnections(message, kvp.Value.ToArray()));
        }

        await Task.WhenAll(tasks);
    }

    public Task NotifyConnectionRemoved(string connectionId)
    {
        if (_connectionPartitions.Remove(connectionId))
        {
            _logger.LogDebug("Removed connection {ConnectionId} from coordinator mapping.", connectionId);
            if (_connectionPartitions.Count == 0 && _currentPartitionCount != _basePartitionCount)
            {
                _logger.LogDebug("Resetting partition count to base value {PartitionCount} as no active connections remain.", _basePartitionCount);
                _currentPartitionCount = (int)_basePartitionCount;
            }
        }

        return Task.CompletedTask;
    }

    private List<int> GetActivePartitions()
    {
        if (_connectionPartitions.Count == 0)
        {
            return Enumerable.Range(0, _currentPartitionCount).ToList();
        }

        return _connectionPartitions.Values
            .Distinct()
            .OrderBy(static partitionId => partitionId)
            .ToList();
    }

    private int GetOrAssignPartition(string connectionId)
    {
        if (_connectionPartitions.TryGetValue(connectionId, out var partition))
        {
            return partition;
        }

        var partitionCount = EnsurePartitionCapacity(_connectionPartitions.Count + 1);
        partition = PartitionHelper.GetPartitionId(connectionId, (uint)partitionCount);
        _connectionPartitions[connectionId] = partition;

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
        }

        return _currentPartitionCount;
    }
}
