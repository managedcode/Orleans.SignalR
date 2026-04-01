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
using ManagedCode.Orleans.SignalR.Server.Helpers;
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
    private readonly StateWriteLock _stateWriteLock = new();
    private Dictionary<string, PartitionAssignment> GroupPartitions { get; } = new(StringComparer.Ordinal);
    private Dictionary<string, int> GroupMembership { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<int> _activePartitions = [];
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
        _groupsPerPartitionHint = Math.Max(1, _options.Value.GroupsPerPartitionHint);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await _state.ReadStateAsync(cancellationToken);
        _state.State ??= new GroupCoordinatorState();

        // Copy persisted state to local dictionaries
        var persistedPartitions = EnsureOrdinalDictionary(_state.State.GroupPartitions);
        var persistedMembership = EnsureOrdinalMembershipDictionary(_state.State.GroupMembership);

        GroupPartitions.Clear();
        GroupMembership.Clear();
        _activePartitions.Clear();

        foreach (var kvp in persistedPartitions)
        {
            GroupPartitions[kvp.Key] = kvp.Value;
            _activePartitions.Add(kvp.Value.PartitionId);
        }

        foreach (var kvp in persistedMembership)
        {
            GroupMembership[kvp.Key] = kvp.Value;
        }

        // Set state to reference local dictionaries
        _state.State.GroupPartitions = GroupPartitions;
        _state.State.GroupMembership = GroupMembership;

        _basePartitionCount = Math.Max(1u, _options.Value.GroupPartitionCount);
        _currentPartitionCount = _state.State.CurrentPartitionCount;
        _partitionEpoch = Math.Max(1, _state.State.PartitionEpoch);

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
        await AddConnectionToGroups([groupName], connectionId, observer);
    }

    public async Task<int[]> AddConnectionToGroups(string[] groupNames, string connectionId, ISignalRObserver observer)
    {
        var groupsByPartition = GetPartitionsForGroups(groupNames, assignIfMissing: true);
        if (groupsByPartition.Count == 0)
        {
            return [];
        }

        var tasks = ArrayPool<Task<string[]>>.Shared.Rent(groupsByPartition.Count);
        var partitions = ArrayPool<int>.Shared.Rent(groupsByPartition.Count);

        try
        {
            var taskIndex = 0;
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = await GetPartitionGrainAsync(kvp.Key);
                tasks[taskIndex] = partitionGrain.AddConnectionToGroups(connectionId, CollectionsMarshal.AsSpan(kvp.Value).ToArray(), observer);
                partitions[taskIndex] = kvp.Key;
                taskIndex++;
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));

            for (var index = 0; index < taskIndex; index++)
            {
                foreach (var affectedGroupName in tasks[index].Result)
                {
                    var membership = GroupMembership.TryGetValue(affectedGroupName, out var count) ? count + 1 : 1;
                    GroupMembership[affectedGroupName] = membership;
                }
            }

            await PersistCoordinatorStateIfDirtyAsync();

            return partitions.AsSpan(0, taskIndex).ToArray();
        }
        finally
        {
            ArrayPool<Task<string[]>>.Shared.Return(tasks, clearArray: true);
            ArrayPool<int>.Shared.Return(partitions, clearArray: true);
        }
    }

    public async Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        await RemoveConnectionFromGroups([groupName], connectionId, observer);
    }

    public async Task<int[]> RemoveConnectionFromGroups(string[] groupNames, string connectionId, ISignalRObserver observer)
    {
        var groupsByPartition = GetPartitionsForGroups(groupNames, assignIfMissing: false);
        if (groupsByPartition.Count == 0)
        {
            return [];
        }

        var tasks = ArrayPool<Task<string[]>>.Shared.Rent(groupsByPartition.Count);
        var partitions = ArrayPool<int>.Shared.Rent(groupsByPartition.Count);

        try
        {
            var taskIndex = 0;
            foreach (var kvp in groupsByPartition)
            {
                var partitionGrain = await GetPartitionGrainAsync(kvp.Key);
                tasks[taskIndex] = partitionGrain.RemoveConnectionFromGroups(connectionId, CollectionsMarshal.AsSpan(kvp.Value).ToArray(), observer);
                partitions[taskIndex] = kvp.Key;
                taskIndex++;
            }

            await Task.WhenAll(tasks.AsSpan(0, taskIndex));

            for (var index = 0; index < taskIndex; index++)
            {
                foreach (var affectedGroupName in tasks[index].Result)
                {
                    if (GroupMembership.TryGetValue(affectedGroupName, out var count))
                    {
                        if (count <= 1)
                        {
                            ReleaseGroup(affectedGroupName);
                        }
                        else
                        {
                            GroupMembership[affectedGroupName] = count - 1;
                        }
                    }
                }
            }

            await PersistCoordinatorStateIfDirtyAsync();

            return partitions.AsSpan(0, taskIndex).ToArray();
        }
        finally
        {
            ArrayPool<Task<string[]>>.Shared.Return(tasks, clearArray: true);
            ArrayPool<int>.Shared.Return(partitions, clearArray: true);
        }
    }

    public async Task NotifyGroupRemoved(string groupName)
    {
        ReleaseGroup(groupName);

        await PersistCoordinatorStateIfDirtyAsync();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _state.State.CurrentPartitionCount = _currentPartitionCount;
        _state.State.PartitionEpoch = _partitionEpoch;

        await _stateWriteLock.RunAsync(async () =>
        {
            if (GroupPartitions.Count == 0)
            {
                await _state.ClearStateSafeAsync(cancellationToken);
            }
            else
            {
                await _state.WriteStateSafeAsync(cancellationToken);
            }
        });
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

            // Stale epoch - keep existing partition to preserve routing stability
            var updatedAssignment = PartitionAssignment.Create(existingAssignment.PartitionId, _partitionEpoch);
            GroupPartitions[groupName] = updatedAssignment;
            _stateDirty = true;
            _logger.LogDebug(
                "Updated group {GroupName} epoch from {OldEpoch} to {NewEpoch} (partition {Partition} unchanged)",
                groupName, existingAssignment.Epoch, _partitionEpoch, existingAssignment.PartitionId);
            return (existingAssignment.PartitionId, false, true);
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

    private Dictionary<int, List<string>> GetPartitionsForGroups(string[] groupNames, bool assignIfMissing)
    {
        var normalizedGroupNames = NormalizeGroupNames(groupNames);
        var groupsByPartition = new Dictionary<int, List<string>>();

        foreach (var groupName in normalizedGroupNames)
        {
            int partition;
            if (assignIfMissing)
            {
                (partition, _, _) = GetOrAssignPartitionWithEpoch(groupName);
            }
            else if (GroupPartitions.TryGetValue(groupName, out var existingAssignment))
            {
                partition = existingAssignment.PartitionId;
            }
            else
            {
                continue;
            }

            ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(groupsByPartition, partition, out var exists);
            if (!exists)
            {
                list = [];
            }

            list!.Add(groupName);
        }

        return groupsByPartition;
    }

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

    private async Task PersistCoordinatorStateIfDirtyAsync()
    {
        if (!_stateDirty)
        {
            return;
        }

        await _stateWriteLock.RunAsync(() => _state.WriteStateSafeAsync(state =>
        {
            // Re-sync local dictionaries to state on each retry (ReadStateAsync creates new state object)
            state.GroupPartitions = GroupPartitions;
            state.GroupMembership = GroupMembership;
            state.CurrentPartitionCount = _currentPartitionCount;
            state.PartitionEpoch = _partitionEpoch;
            return true;
        }));

        _stateDirty = false;
    }

    private static string[] NormalizeGroupNames(string[] groupNames)
    {
        if (groupNames.Length == 0)
        {
            return [];
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(groupNames.Length);
        foreach (var groupName in groupNames)
        {
            if (unique.Add(groupName))
            {
                normalized.Add(groupName);
            }
        }

        return normalized.ToArray();
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
