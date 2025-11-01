using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, int> _groupPartitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _groupMembership = new(StringComparer.Ordinal);
    private readonly int _groupsPerPartitionHint = Math.Max(1, options.Value.GroupsPerPartitionHint);
    private readonly ILogger<SignalRGroupCoordinatorGrain> _logger = logger;
    private uint _basePartitionCount;
    private int _currentPartitionCount;
    private string? _hubKey;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _basePartitionCount = Math.Max(1, options.Value.GroupPartitionCount);
        _currentPartitionCount = (int)_basePartitionCount;
        _hubKey = this.GetPrimaryKeyString();

        logger.LogInformation("Group coordinator activated with base partition count {PartitionCount} and hint {GroupsPerPartition}", _basePartitionCount, _groupsPerPartitionHint);
        return base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        return Task.FromResult(_currentPartitionCount);
    }

    public Task<int> GetPartitionForGroup(string groupName)
    {
        var partition = GetOrAssignPartition(groupName);
        return Task.FromResult(partition);
    }

    public async Task SendToGroup(string groupName, HubMessage message)
    {
        var partition = GetOrAssignPartition(groupName);
        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.SendToGroups(message, new[] { groupName });
    }

    public async Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds)
    {
        var partition = GetOrAssignPartition(groupName);
        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.SendToGroupsExcept(message, new[] { groupName }, excludedConnectionIds);
    }

    public async Task SendToGroups(string[] groupNames, HubMessage message)
    {
        var groupsByPartition = new Dictionary<int, List<string>>();
        foreach (var groupName in groupNames)
        {
            var partition = GetOrAssignPartition(groupName);
            if (!groupsByPartition.TryGetValue(partition, out var list))
            {
                list = new List<string>();
                groupsByPartition[partition] = list;
            }
            list.Add(groupName);
        }

        if (groupsByPartition.Count < 100)
        {
            var tasks = new List<Task>(groupsByPartition.Count);
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = await GetPartitionGrainAsync(kvp.Key);
                tasks.Add(partitionGrain.SendToGroups(message, kvp.Value.ToArray()));
            }
            await Task.WhenAll(tasks);
        }
        else
        {
            foreach (var kvp in groupsByPartition)
            {
                var partitionId = kvp.Key;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var partitionGrain = await GetPartitionGrainAsync(partitionId);
                        await partitionGrain.SendToGroups(message, kvp.Value.ToArray());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send to groups in partition {PartitionId}", partitionId);
                    }
                });
            }
        }
    }

    public async Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        var partition = GetOrAssignPartition(groupName);
        var membership = _groupMembership.TryGetValue(groupName, out var count) ? count + 1 : 1;
        _groupMembership[groupName] = membership;

        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.AddConnectionToGroup(groupName, connectionId, observer);
    }

    public async Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        var partition = _groupPartitions.TryGetValue(groupName, out var existingPartition)
            ? existingPartition
            : PartitionHelper.GetPartitionId(groupName, (uint)_currentPartitionCount);
        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.RemoveConnectionFromGroup(groupName, connectionId, observer);

        if (_groupMembership.TryGetValue(groupName, out var count))
        {
            if (count <= 1)
            {
                ReleaseGroup(groupName);
            }
            else
            {
                _groupMembership[groupName] = count - 1;
            }
        }
    }

    public Task NotifyGroupRemoved(string groupName)
    {
        ReleaseGroup(groupName);
        return Task.CompletedTask;
    }

    private async Task<ISignalRGroupPartitionGrain> GetPartitionGrainAsync(int partitionId)
    {
        var hubKey = _hubKey ??= this.GetPrimaryKeyString();
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, hubKey, partitionId);
        await partitionGrain.EnsureInitialized(hubKey);
        return partitionGrain;
    }

    private int GetOrAssignPartition(string groupName)
    {
        if (_groupPartitions.TryGetValue(groupName, out var partition))
        {
            return partition;
        }

        var partitionCount = EnsurePartitionCapacity(_groupPartitions.Count + 1);
        partition = PartitionHelper.GetPartitionId(groupName, (uint)partitionCount);
        _groupPartitions[groupName] = partition;

        _logger.LogDebug("Assigned group {GroupName} to partition {Partition} (partitionCount={PartitionCount})", groupName, partition, partitionCount);
        return partition;
    }

    private int EnsurePartitionCapacity(int prospectiveGroups)
    {
        var desired = Math.Max((int)_basePartitionCount,
            PartitionHelper.GetOptimalGroupPartitionCount(prospectiveGroups, _groupsPerPartitionHint));

        if (desired > _currentPartitionCount)
        {
            _logger.LogInformation(
                "Increasing group partition count from {OldPartitionCount} to {NewPartitionCount} for {GroupCount} tracked groups.",
                _currentPartitionCount,
                desired,
                prospectiveGroups);
            _currentPartitionCount = desired;
        }

        return _currentPartitionCount;
    }

    private void ReleaseGroup(string groupName)
    {
        var removedMembership = _groupMembership.Remove(groupName);
        var removedPartition = _groupPartitions.Remove(groupName);

        if ((removedMembership || removedPartition) && _groupMembership.Count == 0 && _currentPartitionCount != _basePartitionCount)
        {
            _logger.LogDebug("Resetting group partition count to base value {PartitionCount} as no active groups remain.", _basePartitionCount);
            _currentPartitionCount = (int)_basePartitionCount;
        }
    }
}
