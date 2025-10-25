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
    IClusterClient clusterClient) : Grain, ISignalRConnectionCoordinatorGrain
{
    private uint _partitionCount;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _partitionCount = options.Value.ConnectionPartitionCount;
            
        logger.LogInformation("Connection coordinator activated with {PartitionCount} partitions", _partitionCount);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        return Task.FromResult((int)_partitionCount);
    }
    
    public Task<int> GetPartitionForConnection(string connectionId)
    {
        return Task.FromResult(PartitionHelper.GetPartitionId(connectionId, _partitionCount));
    }

    public async Task SendToAll(HubMessage message)
    {
        // For smaller partition counts (< 100), use Task.WhenAll for consistency
        if (_partitionCount < 100)
        {
            var tasks = new List<Task>((int)_partitionCount);
            for (int i = 0; i < _partitionCount; i++)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), i);
                tasks.Add(partitionGrain.SendToPartition(message));
            }
            await Task.WhenAll(tasks);
        }
        else
        {
            // For many partitions, use fire-and-forget to avoid memory issues
            for (int i = 0; i < _partitionCount; i++)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), i);
                var partitionId = i; // Capture for closure
                _ = partitionGrain.SendToPartition(message).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.LogError(t.Exception, "Failed to send to partition {PartitionId}", partitionId);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        // Group excluded connections by partition
        var excludedByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in excludedConnectionIds)
        {
            var partition = PartitionHelper.GetPartitionId(connectionId, _partitionCount);
            if (!excludedByPartition.ContainsKey(partition))
                excludedByPartition[partition] = new List<string>();
            excludedByPartition[partition].Add(connectionId);
        }

        // For smaller partition counts (< 100), use Task.WhenAll for consistency
        if (_partitionCount < 100)
        {
            var tasks = new List<Task>((int)_partitionCount);
            for (int i = 0; i < _partitionCount; i++)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), i);
                var excluded = excludedByPartition.ContainsKey(i) ? excludedByPartition[i].ToArray() : System.Array.Empty<string>();
                tasks.Add(partitionGrain.SendToPartitionExcept(message, excluded));
            }
            await Task.WhenAll(tasks);
        }
        else
        {
            // For many partitions, use fire-and-forget to avoid memory issues
            for (int i = 0; i < _partitionCount; i++)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), i);
                var excluded = excludedByPartition.ContainsKey(i) ? excludedByPartition[i].ToArray() : System.Array.Empty<string>();
                var partitionId = i; // Capture for closure
                _ = partitionGrain.SendToPartitionExcept(message, excluded).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.LogError(t.Exception, "Failed to send to partition {PartitionId} except connections", partitionId);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        var partition = PartitionHelper.GetPartitionId(connectionId, _partitionCount);
        var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        return await partitionGrain.SendToConnection(message, connectionId);
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        // Group connections by partition
        var connectionsByPartition = new Dictionary<int, List<string>>();
        foreach (var connectionId in connectionIds)
        {
            var partition = PartitionHelper.GetPartitionId(connectionId, _partitionCount);
            if (!connectionsByPartition.ContainsKey(partition))
                connectionsByPartition[partition] = new List<string>();
            connectionsByPartition[partition].Add(connectionId);
        }

        // For smaller numbers of partitions/connections, use Task.WhenAll for consistency
        if (connectionsByPartition.Count < 100)
        {
            var tasks = new List<Task>(connectionsByPartition.Count);
            foreach (var kvp in connectionsByPartition)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), kvp.Key);
                tasks.Add(partitionGrain.SendToConnections(message, kvp.Value.ToArray()));
            }
            await Task.WhenAll(tasks);
        }
        else
        {
            // For many partitions, use fire-and-forget to avoid memory issues
            foreach (var kvp in connectionsByPartition)
            {
                var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), kvp.Key);
                var partitionId = kvp.Key;
                _ = partitionGrain.SendToConnections(message, kvp.Value.ToArray()).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.LogError(t.Exception, "Failed to send to connections in partition {PartitionId}", partitionId);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }
}
