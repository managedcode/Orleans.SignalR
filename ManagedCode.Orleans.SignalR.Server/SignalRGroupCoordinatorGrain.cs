using System;
using System.Buffers;
using System.Collections.Generic;
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
[GrainType($"ManagedCode.{nameof(SignalRGroupCoordinatorGrain)}")]
public sealed class SignalRGroupCoordinatorGrain : Grain, ISignalRGroupCoordinatorGrain
{
    private readonly ILogger<SignalRGroupCoordinatorGrain> _logger;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly IPersistentState<GroupCoordinatorState> _state;
    private readonly HashSet<int> _activePartitions;
    private readonly int _groupsPerPartitionHint;
    private uint _basePartitionCount;
    private string? _hubKey;
    private int _currentPartitionCount;
    private int _partitionEpoch;
    private bool _stateDirty;

    public SignalRGroupCoordinatorGrain(
        ILogger<SignalRGroupCoordinatorGrain> logger,
        IOptions<OrleansSignalROptions> options,
        [PersistentState(nameof(SignalRGroupCoordinatorGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<GroupCoordinatorState> state)
    {
        _logger = logger;
        _options = options;
        _state = state;
        _activePartitions = new HashSet<int>();
        _groupsPerPartitionHint = Math.Max(1, _options.Value.GroupsPerPartitionHint);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await _state.ReadStateAsync(cancellationToken);
        _state.State ??= new GroupCoordinatorState();
        _state.State.GroupPartitions = EnsureOrdinalDictionary(_state.State.GroupPartitions);
        _state.State.GroupMembership = EnsureOrdinalMembershipDictionary(_state.State.GroupMembership);
        _basePartitionCount = Math.Max(1u, _options.Value.GroupPartitionCount);
        _currentPartitionCount = _state.State.CurrentPartitionCount;
        _partitionEpoch = Math.Max(1, _state.State.PartitionEpoch);

        // Rebuild active partitions set from persisted state
        _activePartitions.Clear();
        foreach (var assignment in GroupPartitions.Values)
        {
            _activePartitions.Add(assignment.PartitionId);
        }

        // Ensure partition count is at least base, but preserve higher counts to maintain routing consistency
        if (_currentPartitionCount <= 0 || _currentPartitionCount < _basePartitionCount)
        {
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
        }
        // Only reset to base if truly empty AND partition count was scaled up
        else if (GroupPartitions.Count == 0 && _currentPartitionCount > _basePartitionCount)
        {
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
            // Reset epoch when scaling back to base with no groups
            _partitionEpoch = 1;
            _state.State.PartitionEpoch = _partitionEpoch;
        }

        _hubKey = this.GetPrimaryKeyString();
        _stateDirty = false;

        _logger.LogInformation(
            "Group coordinator activated with base partition count {PartitionCount}, current {CurrentPartitionCount}, epoch {Epoch}, hint {GroupsPerPartition}, tracked groups {TrackedGroups}",
            _basePartitionCount,
            _currentPartitionCount,
            _partitionEpoch,
            _groupsPerPartitionHint,
            GroupPartitions.Count);
        await base.OnActivateAsync(cancellationToken);
    }

    public Task<int> GetPartitionCount()
    {
        if (_currentPartitionCount < (int)_basePartitionCount)
        {
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
        }

        return Task.FromResult(_currentPartitionCount);
    }

    public Task<int> GetPartitionForGroup(string groupName)
    {
        var (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
        return Task.FromResult(partition);
    }

    public async Task SendToGroup(string groupName, HubMessage message)
    {
        var (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.SendToGroups(message, new[] { groupName });
    }

    public async Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds)
    {
        var (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.SendToGroupsExcept(message, new[] { groupName }, excludedConnectionIds);
    }

    public async Task SendToGroups(string[] groupNames, HubMessage message)
    {
        // Group by partition using CollectionsMarshal for efficient access
        var groupsByPartition = new Dictionary<int, List<string>>();
        foreach (var groupName in groupNames)
        {
            var (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
            ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(groupsByPartition, partition, out var exists);
            if (!exists)
            {
                list = new List<string>();
            }
            list!.Add(groupName);
        }

        if (groupsByPartition.Count == 0)
        {
            return;
        }

        // Use ArrayPool for task collection
        var tasks = ArrayPool<Task>.Shared.Rent(groupsByPartition.Count);
        try
        {
            var taskIndex = 0;
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = await GetPartitionGrainAsync(kvp.Key);
                tasks[taskIndex++] = partitionGrain.SendToGroups(message, CollectionsMarshal.AsSpan(kvp.Value).ToArray());
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to one or more group partitions");
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(tasks, clearArray: true);
        }
    }

    public async Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        var (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
        var membership = GroupMembership.TryGetValue(groupName, out var count) ? count + 1 : 1;
        GroupMembership[groupName] = membership;

        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.AddConnectionToGroup(groupName, connectionId, observer);

        // Persist state changes to ensure consistency after reactivation
        if (_stateDirty)
        {
            await _state.WriteStateAsync();
            _stateDirty = false;
        }
    }

    public async Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        int partition;
        if (GroupPartitions.TryGetValue(groupName, out var existingAssignment))
        {
            partition = existingAssignment.PartitionId;
        }
        else
        {
            partition = PartitionHelper.GetPartitionId(groupName, (uint)_currentPartitionCount);
        }

        var partitionGrain = await GetPartitionGrainAsync(partition);
        await partitionGrain.RemoveConnectionFromGroup(groupName, connectionId, observer);

        if (GroupMembership.TryGetValue(groupName, out var count))
        {
            if (count <= 1)
            {
                ReleaseGroup(groupName);
            }
            else
            {
                GroupMembership[groupName] = count - 1;
            }
        }

        // Persist state changes to ensure consistency after reactivation
        if (_stateDirty)
        {
            await _state.WriteStateAsync();
            _stateDirty = false;
        }
    }

    public async Task NotifyGroupRemoved(string groupName)
    {
        ReleaseGroup(groupName);

        if (_stateDirty)
        {
            await _state.WriteStateAsync();
            _stateDirty = false;
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _state.State.CurrentPartitionCount = _currentPartitionCount;
        _state.State.PartitionEpoch = _partitionEpoch;

        if (GroupPartitions.Count == 0)
        {
            await _state.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _state.WriteStateAsync(cancellationToken);
        }
    }

    private async Task<ISignalRGroupPartitionGrain> GetPartitionGrainAsync(int partitionId)
    {
        var hubKey = _hubKey ??= this.GetPrimaryKeyString();
        var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain(GrainFactory, hubKey, partitionId);
        await partitionGrain.EnsureInitialized(hubKey);
        return partitionGrain;
    }

    /// <summary>
    /// Gets or assigns a partition for a group, handling epoch-based reassignment.
    /// Returns (partitionId, wasNew, wasReassigned).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int PartitionId, bool WasNew, bool WasReassigned) GetOrAssignPartitionWithEpoch(string groupName)
    {
        if (GroupPartitions.TryGetValue(groupName, out var existingAssignment))
        {
            // Check if assignment is from current epoch
            if (existingAssignment.Epoch == _partitionEpoch)
            {
                return (existingAssignment.PartitionId, false, false);
            }

            // Stale epoch - check if partition would be different with current partition count
            var newPartition = PartitionHelper.GetPartitionId(groupName, (uint)_currentPartitionCount);

            if (newPartition == existingAssignment.PartitionId)
            {
                // Same partition, just update epoch
                var updatedAssignment = PartitionAssignment.Create(existingAssignment.PartitionId, _partitionEpoch);
                GroupPartitions[groupName] = updatedAssignment;
                _stateDirty = true;
                _logger.LogDebug(
                    "Updated group {GroupName} epoch from {OldEpoch} to {NewEpoch} (partition {Partition} unchanged)",
                    groupName, existingAssignment.Epoch, _partitionEpoch, existingAssignment.PartitionId);
                return (existingAssignment.PartitionId, false, true);
            }

            // Partition changed due to scaling - reassign
            var reassignment = PartitionAssignment.Create(newPartition, _partitionEpoch);
            GroupPartitions[groupName] = reassignment;
            _activePartitions.Add(newPartition);
            _stateDirty = true;

            _logger.LogInformation(
                "Reassigned group {GroupName} from partition {OldPartition} (epoch {OldEpoch}) to partition {NewPartition} (epoch {NewEpoch}) due to scaling",
                groupName, existingAssignment.PartitionId, existingAssignment.Epoch, newPartition, _partitionEpoch);

            return (newPartition, false, true);
        }

        // New group - assign to partition with current epoch
        var partitionCount = EnsurePartitionCapacity(GroupPartitions.Count + 1);
        var partition = PartitionHelper.GetPartitionId(groupName, (uint)partitionCount);
        var assignment = PartitionAssignment.Create(partition, _partitionEpoch);

        GroupPartitions[groupName] = assignment;
        _activePartitions.Add(partition);
        _stateDirty = true;

        _logger.LogDebug(
            "Assigned group {GroupName} to partition {Partition} (epoch {Epoch}, partitionCount={PartitionCount})",
            groupName, partition, _partitionEpoch, partitionCount);

        return (partition, true, false);
    }

    private int EnsurePartitionCapacity(int prospectiveGroups)
    {
        var desired = Math.Max((int)_basePartitionCount,
            PartitionHelper.GetOptimalGroupPartitionCount(prospectiveGroups, _groupsPerPartitionHint));

        if (desired > _currentPartitionCount)
        {
            _logger.LogInformation(
                "Increasing group partition count from {OldPartitionCount} to {NewPartitionCount} (epoch {OldEpoch} -> {NewEpoch}) for {GroupCount} tracked groups.",
                _currentPartitionCount,
                desired,
                _partitionEpoch,
                _partitionEpoch + 1,
                prospectiveGroups);

            _currentPartitionCount = desired;
            _partitionEpoch++;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
            _state.State.PartitionEpoch = _partitionEpoch;
        }

        return _currentPartitionCount;
    }

    private Dictionary<string, PartitionAssignment> GroupPartitions => _state.State.GroupPartitions!;
    private Dictionary<string, int> GroupMembership => _state.State.GroupMembership!;

    private void ReleaseGroup(string groupName)
    {
        var removedMembership = GroupMembership.Remove(groupName);
        var removedPartition = GroupPartitions.Remove(groupName, out var assignment);

        if (removedPartition)
        {
            _stateDirty = true;
            var partitionId = assignment.PartitionId;

            // Check if any other groups are using this partition
            var partitionStillActive = false;
            foreach (var otherAssignment in GroupPartitions.Values)
            {
                if (otherAssignment.PartitionId == partitionId)
                {
                    partitionStillActive = true;
                    break;
                }
            }

            if (!partitionStillActive)
            {
                _activePartitions.Remove(partitionId);
            }
        }

        if ((removedMembership || removedPartition) && GroupMembership.Count == 0 && _currentPartitionCount != _basePartitionCount)
        {
            _logger.LogDebug("Resetting group partition count to base value {PartitionCount} and epoch to 1 as no active groups remain.", _basePartitionCount);
            _currentPartitionCount = (int)_basePartitionCount;
            _state.State.CurrentPartitionCount = _currentPartitionCount;
            _partitionEpoch = 1;
            _state.State.PartitionEpoch = _partitionEpoch;
            _activePartitions.Clear();
        }
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

    private static Dictionary<string, int> EnsureOrdinalMembershipDictionary(Dictionary<string, int>? dictionary)
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
