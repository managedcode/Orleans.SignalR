using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Server.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRGroupPartitionGrain)}")]
public class SignalRGroupPartitionGrain(
    ILogger<SignalRGroupPartitionGrain> logger,
    IOptions<OrleansSignalROptions> orleansSignalOptions,
    IOptions<HubOptions> hubOptions,
    [PersistentState(nameof(SignalRGroupPartitionGrain), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<GroupPartitionState> state)
    : SignalRObserverGrainBase<SignalRGroupPartitionGrain>(logger, orleansSignalOptions, hubOptions), ISignalRGroupPartitionGrain
{
    private string? _hubKey;
    protected override int TrackedConnectionCount => state.State.ConnectionObservers.Count;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);
        state.State ??= new GroupPartitionState();
        _hubKey = state.State.HubKey;
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task SendToGroups(HubMessage message, string[] groupNames)
    {
        Logger.LogDebug("SendToGroups invoked for partition {PartitionId} with groups {Groups} (keepAlive={KeepEachConnectionAlive}, liveObservers={LiveObserversCount}, trackedConnections={TrackedConnectionCount})",
            this.GetPrimaryKeyLong(),
            string.Join(",", groupNames),
            KeepEachConnectionAlive,
            LiveObservers.Count,
            TrackedConnectionCount);

        if (LiveObservers.Count > 0)
        {
            var targetConnections = CollectConnectionIds(groupNames, excludedConnections: null);
            DispatchToLiveObservers(GetLiveObservers(targetConnections), message);
            return;
        }

        var targetObservers = CollectObservers(groupNames, excludedConnections: null);

        await Task.Run(() => ObserverManager.Notify(
            observer => observer.OnNextAsync(message),
            observer => targetObservers.Contains(observer.GetPrimaryKeyString())));
    }

    public async Task SendToGroupsExcept(HubMessage message, string[] groupNames, string[] excludedConnectionIds)
    {
        Logger.LogDebug("SendToGroupsExcept invoked for partition {PartitionId} with groups {Groups}, excluded {Excluded} (keepAlive={KeepEachConnectionAlive}, liveObservers={LiveObserversCount}, trackedConnections={TrackedConnectionCount})",
            this.GetPrimaryKeyLong(),
            string.Join(",", groupNames),
            string.Join(",", excludedConnectionIds),
            KeepEachConnectionAlive,
            LiveObservers.Count,
            TrackedConnectionCount);

        if (LiveObservers.Count > 0)
        {
            var targetConnections = CollectConnectionIds(groupNames, new HashSet<string>(excludedConnectionIds, StringComparer.Ordinal));
            DispatchToLiveObservers(GetLiveObservers(targetConnections), message);
            return;
        }

        var excluded = new HashSet<string>(excludedConnectionIds);
        var targetObservers = CollectObservers(groupNames, excluded);

        await Task.Run(() => ObserverManager.Notify(
            observer => observer.OnNextAsync(message),
            observer => targetObservers.Contains(observer.GetPrimaryKeyString())));
    }

    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        TrackConnection(connectionId, observer);
        var observerKey = observer.GetPrimaryKeyString();
        var persisted = await state.WriteStateSafeAsync(state =>
        {
            var observerChanged = !state.ConnectionObservers.TryGetValue(connectionId, out var existing) ||
                                  !string.Equals(existing, observerKey, StringComparison.Ordinal);
            state.ConnectionObservers[connectionId] = observerKey;
            var groupsRegistered = state.ConnectionGroups.TryAdd(connectionId, new HashSet<string>());
            return observerChanged || groupsRegistered;
        });

        if (persisted)
        {
            Logger.LogDebug("Registering connection {ConnectionId} in partition {PartitionId}", connectionId,
                this.GetPrimaryKeyLong());
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        UntrackConnection(connectionId, observer);
        List<string>? emptiedGroups = null;
        var removed = await state.WriteStateSafeAsync(state => RemoveConnectionState(state, connectionId, out emptiedGroups));
        if (removed)
        {
            Logger.LogDebug("Removing connection {ConnectionId} from partition {PartitionId}", connectionId,
                this.GetPrimaryKeyLong());
            NotifyRemovedGroups(emptiedGroups);
        }
    }

    public Task Ping(ISignalRObserver observer)
    {
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public async Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        TrackConnection(connectionId, observer);
        var observerKey = observer.GetPrimaryKeyString();

        var persisted = await state.WriteStateSafeAsync(state =>
        {
            var observerChanged = !state.ConnectionObservers.TryGetValue(connectionId, out var existingObserver) ||
                                  !string.Equals(existingObserver, observerKey, StringComparison.Ordinal);
            state.ConnectionObservers[connectionId] = observerKey;

            if (!state.Groups.TryGetValue(groupName, out var connections))
            {
                connections = new Dictionary<string, string>();
                state.Groups[groupName] = connections;
            }

            var groupUpdated = !connections.TryGetValue(connectionId, out var mappedObserver) ||
                               !string.Equals(mappedObserver, observerKey, StringComparison.Ordinal);
            connections[connectionId] = observerKey;

            if (!state.ConnectionGroups.TryGetValue(connectionId, out var groups))
            {
                groups = new HashSet<string>();
                state.ConnectionGroups[connectionId] = groups;
            }

            var membershipAdded = groups.Add(groupName);

            return observerChanged || groupUpdated || membershipAdded;
        });

        if (persisted)
        {
            Logger.LogDebug("Adding connection {ConnectionId} to group {GroupName} in partition {PartitionId}",
                connectionId, groupName, this.GetPrimaryKeyLong());
        }
    }

    public async Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        List<string>? emptiedGroups = null;
        var connectionRemoved = false;

        var stateChanged = await state.WriteStateSafeAsync(state =>
        {
            emptiedGroups = null;
            connectionRemoved = false;
            var changed = false;

            if (state.Groups.TryGetValue(groupName, out var members) && members.Remove(connectionId))
            {
                changed = true;
                if (members.Count == 0)
                {
                    state.Groups.Remove(groupName);
                    emptiedGroups ??= new List<string>();
                    emptiedGroups.Add(groupName);
                }
            }

            if (state.ConnectionGroups.TryGetValue(connectionId, out var groups) && groups.Remove(groupName))
            {
                changed = true;
                if (groups.Count == 0)
                {
                    state.ConnectionGroups.Remove(connectionId);
                    connectionRemoved = RemoveConnectionState(state, connectionId, out var additionalGroups);
                    if (additionalGroups is not null)
                    {
                        if (emptiedGroups is null)
                        {
                            emptiedGroups = additionalGroups;
                        }
                        else
                        {
                            emptiedGroups.AddRange(additionalGroups);
                        }
                    }
                }
            }

            return changed || connectionRemoved;
        });

        if (stateChanged)
        {
            Logger.LogDebug("Removing connection {ConnectionId} from group {GroupName} in partition {PartitionId}",
                connectionId, groupName, this.GetPrimaryKeyLong());

            if (connectionRemoved)
            {
                UntrackConnection(connectionId, observer);
            }

            NotifyRemovedGroups(emptiedGroups);
        }
    }

    public Task<bool> HasConnection(string connectionId)
    {
        var tracked = state.State.ConnectionGroups.TryGetValue(connectionId, out var groups) && groups.Count > 0;
        return Task.FromResult(tracked);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Deactivating group partition grain {PartitionId}", this.GetPrimaryKeyLong());

        var hasState = !state.State.IsEmpty;
        ClearObserverTracking();

        if (!hasState)
        {
            return state.ClearStateAsync(cancellationToken);
        }

        return state.WriteStateAsync(cancellationToken);
    }

    private HashSet<string> CollectObservers(IEnumerable<string> groupNames, HashSet<string>? excludedConnections)
    {
        var observers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var groupName in groupNames)
        {
            if (!state.State.Groups.TryGetValue(groupName, out var connections))
            {
                continue;
            }

            foreach (var (connectionId, observerKey) in connections)
            {
                if (excludedConnections is not null && excludedConnections.Contains(connectionId))
                {
                    continue;
                }

                observers.Add(observerKey);
            }
        }

        return observers;
    }

    private HashSet<string> CollectConnectionIds(IEnumerable<string> groupNames, HashSet<string>? excludedConnections)
    {
        var connections = new HashSet<string>(StringComparer.Ordinal);

        foreach (var groupName in groupNames)
        {
            if (!state.State.Groups.TryGetValue(groupName, out var members))
            {
                continue;
            }

            foreach (var (connectionId, _) in members)
            {
                if (excludedConnections is not null && excludedConnections.Contains(connectionId))
                {
                    continue;
                }

                connections.Add(connectionId);
            }
        }

        return connections;
    }

    private static bool RemoveConnectionState(GroupPartitionState state, string connectionId, out List<string>? emptiedGroups)
    {
        var stateChanged = false;
        emptiedGroups = null;

        if (state.ConnectionGroups.TryGetValue(connectionId, out var groups))
        {
            foreach (var group in groups)
            {
                if (state.Groups.TryGetValue(group, out var members))
                {
                    members.Remove(connectionId);

                    if (members.Count == 0)
                    {
                        state.Groups.Remove(group);
                        emptiedGroups ??= new List<string>();
                        emptiedGroups.Add(group);
                        stateChanged = true;
                    }
                }
            }

            state.ConnectionGroups.Remove(connectionId);
            stateChanged = true;
        }

        if (state.ConnectionObservers.Remove(connectionId))
        {
            stateChanged = true;
        }

        return stateChanged;
    }

    public Task EnsureInitialized(string hubKey)
    {
        if (string.IsNullOrEmpty(_hubKey) || !string.Equals(_hubKey, hubKey, StringComparison.Ordinal))
        {
            _hubKey = hubKey;
            state.State.HubKey = hubKey;
        }

        return Task.CompletedTask;
    }

    private void NotifyRemovedGroups(List<string>? groups)
    {
        if (groups is null)
        {
            return;
        }

        foreach (var group in groups)
        {
            NotifyCoordinatorGroupRemoved(group);
        }
    }

    private void NotifyCoordinatorGroupRemoved(string groupName)
    {
        if (string.IsNullOrEmpty(_hubKey))
        {
            return;
        }

        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain(GrainFactory, _hubKey);
        _ = NotifyCoordinatorAsync(coordinator, groupName);
    }

    private async Task NotifyCoordinatorAsync(ISignalRGroupCoordinatorGrain coordinator, string groupName)
    {
        try
        {
            await coordinator.NotifyGroupRemoved(groupName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to notify coordinator about group {GroupName} removal.", groupName);
        }
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for group partition {PartitionId}.", this.GetPrimaryKeyLong());
    }
}
