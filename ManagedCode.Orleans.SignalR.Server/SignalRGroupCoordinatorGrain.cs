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
[GrainType($"ManagedCode.{nameof(SignalRGroupCoordinatorGrain)}")]
public class SignalRGroupCoordinatorGrain(
    ILogger<SignalRGroupCoordinatorGrain> logger,
    IOptions<OrleansSignalROptions> options) : Grain, ISignalRGroupCoordinatorGrain
{
    private uint _partitionCount;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _partitionCount = options.Value.GroupPartitionCount;
            
        logger.LogInformation("Group coordinator activated with {PartitionCount} partitions", _partitionCount);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        return Task.FromResult((int)_partitionCount);
    }
    
    public Task<int> GetPartitionForGroup(string groupName)
    {
        return Task.FromResult(PartitionHelper.GetPartitionId(groupName, _partitionCount));
    }

    public async Task SendToGroup(string groupName, HubMessage message)
    {
        var partition = PartitionHelper.GetPartitionId(groupName, _partitionCount);
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        await partitionGrain.SendToGroups(message, new[] { groupName });
    }

    public async Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds)
    {
        var partition = PartitionHelper.GetPartitionId(groupName, _partitionCount);
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        await partitionGrain.SendToGroupsExcept(message, new[] { groupName }, excludedConnectionIds);
    }

    public async Task SendToGroups(string[] groupNames, HubMessage message)
    {
        // Group by partition
        var groupsByPartition = new Dictionary<int, List<string>>();
        foreach (var groupName in groupNames)
        {
            var partition = PartitionHelper.GetPartitionId(groupName, _partitionCount);
            if (!groupsByPartition.ContainsKey(partition))
                groupsByPartition[partition] = new List<string>();
            groupsByPartition[partition].Add(groupName);
        }

        // For smaller numbers of partitions/groups, use Task.WhenAll for consistency
        if (groupsByPartition.Count < 100)
        {
            var tasks = new List<Task>(groupsByPartition.Count);
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), kvp.Key);
                tasks.Add(partitionGrain.SendToGroups(message, kvp.Value.ToArray()));
            }
            await Task.WhenAll(tasks);
        }
        else
        {
            // For many partitions, use fire-and-forget to avoid memory issues
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), kvp.Key);
                var partitionId = kvp.Key;
                _ = partitionGrain.SendToGroups(message, kvp.Value.ToArray()).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.LogError(t.Exception, "Failed to send to groups in partition {PartitionId}", partitionId);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    public async Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        var partition = PartitionHelper.GetPartitionId(groupName, _partitionCount);
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        await partitionGrain.AddConnectionToGroup(groupName, connectionId, observer);
    }

    public async Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        var partition = PartitionHelper.GetPartitionId(groupName, _partitionCount);
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, this.GetPrimaryKeyString(), partition);
        await partitionGrain.RemoveConnectionFromGroup(groupName, connectionId, observer);
    }
}
