using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
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
public class SignalRGroupPartitionGrain : SignalRObserverGrainBase<SignalRGroupPartitionGrain>, ISignalRGroupPartitionGrain
{
    private readonly IPersistentState<GroupPartitionState> _state;
    private string? _hubKey;

    public SignalRGroupPartitionGrain(
        ILogger<SignalRGroupPartitionGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRGroupPartitionGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<GroupPartitionState> state)
        : base(logger, orleansSignalOptions, hubOptions)
    {
        _state = state;
        _state.State ??= new GroupPartitionState();
    }

    protected override int TrackedConnectionCount => _state.State.ConnectionObservers.Count;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _hubKey = _state.State.HubKey;
        return base.OnActivateAsync(cancellationToken);
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

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        Logger.LogDebug("Registering connection {ConnectionId} in partition {PartitionId}", connectionId,
            this.GetPrimaryKeyLong());

        _state.State.ConnectionObservers[connectionId] = observer.GetPrimaryKeyString();
        _state.State.ConnectionGroups.TryAdd(connectionId, new HashSet<string>());
        TrackConnection(connectionId, observer);

        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logger.LogDebug("Removing connection {ConnectionId} from partition {PartitionId}", connectionId,
            this.GetPrimaryKeyLong());

        RemoveConnectionInternal(connectionId, observer);
        return Task.CompletedTask;
    }

    public Task Ping(ISignalRObserver observer)
    {
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        Logger.LogDebug("Adding connection {ConnectionId} to group {GroupName} in partition {PartitionId}",
            connectionId, groupName, this.GetPrimaryKeyLong());

        var observerKey = observer.GetPrimaryKeyString();

        _state.State.ConnectionObservers[connectionId] = observerKey;
        TrackConnection(connectionId, observer);

        if (!_state.State.Groups.TryGetValue(groupName, out var connections))
        {
            connections = new Dictionary<string, string>();
            _state.State.Groups[groupName] = connections;
        }

        connections[connectionId] = observerKey;

        if (!_state.State.ConnectionGroups.TryGetValue(connectionId, out var groups))
        {
            groups = new HashSet<string>();
            _state.State.ConnectionGroups[connectionId] = groups;
        }

        groups.Add(groupName);

        return Task.CompletedTask;
    }

    public Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        Logger.LogDebug("Removing connection {ConnectionId} from group {GroupName} in partition {PartitionId}",
            connectionId, groupName, this.GetPrimaryKeyLong());

        if (_state.State.Groups.TryGetValue(groupName, out var members))
        {
            members.Remove(connectionId);

            if (members.Count == 0)
            {
                _state.State.Groups.Remove(groupName);
                NotifyCoordinatorGroupRemoved(groupName);
            }
        }

        if (_state.State.ConnectionGroups.TryGetValue(connectionId, out var groups))
        {
            groups.Remove(groupName);
            if (groups.Count == 0)
            {
                _state.State.ConnectionGroups.Remove(connectionId);
                RemoveConnectionInternal(connectionId, observer);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasConnection(string connectionId)
    {
        var tracked = _state.State.ConnectionGroups.TryGetValue(connectionId, out var groups) && groups.Count > 0;
        return Task.FromResult(tracked);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Deactivating group partition grain {PartitionId}", this.GetPrimaryKeyLong());

        ClearObserverTracking();

        if (_state.State.IsEmpty)
        {
            return _state.ClearStateAsync(cancellationToken);
        }

        return _state.WriteStateAsync(cancellationToken);
    }

    private HashSet<string> CollectObservers(IEnumerable<string> groupNames, HashSet<string>? excludedConnections)
    {
        var observers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var groupName in groupNames)
        {
            if (!_state.State.Groups.TryGetValue(groupName, out var connections))
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
            if (!_state.State.Groups.TryGetValue(groupName, out var members))
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

    private void RemoveConnectionInternal(string connectionId, ISignalRObserver observer)
    {
        List<string>? emptiedGroups = null;

        if (_state.State.ConnectionGroups.TryGetValue(connectionId, out var groups))
        {
            foreach (var group in groups)
            {
                if (_state.State.Groups.TryGetValue(group, out var members))
                {
                    members.Remove(connectionId);

                    if (members.Count == 0)
                    {
                        _state.State.Groups.Remove(group);
                        emptiedGroups ??= new List<string>();
                        emptiedGroups.Add(group);
                    }
                }
            }

            _state.State.ConnectionGroups.Remove(connectionId);
        }

        _state.State.ConnectionObservers.Remove(connectionId);
        UntrackConnection(connectionId, observer);
        if (emptiedGroups is not null)
        {
            foreach (var group in emptiedGroups)
            {
                NotifyCoordinatorGroupRemoved(group);
            }
        }
    }

    public Task EnsureInitialized(string hubKey)
    {
        if (string.IsNullOrEmpty(_hubKey) || !string.Equals(_hubKey, hubKey, StringComparison.Ordinal))
        {
            _hubKey = hubKey;
            _state.State.HubKey = hubKey;
        }

        return Task.CompletedTask;
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
