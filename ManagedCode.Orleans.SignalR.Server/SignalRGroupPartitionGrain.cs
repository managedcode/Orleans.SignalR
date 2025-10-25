using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRGroupPartitionGrain)}")]
public class SignalRGroupPartitionGrain : Grain, ISignalRGroupPartitionGrain
{
    private readonly ILogger<SignalRGroupPartitionGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<GroupPartitionState> _state;

    public SignalRGroupPartitionGrain(
        ILogger<SignalRGroupPartitionGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRGroupPartitionGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<GroupPartitionState> state)
    {
        _logger = logger;
        _state = state;
        _state.State ??= new GroupPartitionState();

        var timeout = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        var expiration = TimeIntervalHelper.AddExpirationIntervalBuffer(timeout);
        _observerManager = new ObserverManager<ISignalRObserver>(expiration, _logger);
    }

    public async Task SendToGroups(HubMessage message, string[] groupNames)
    {
        var targetObservers = CollectObservers(groupNames, excludedConnections: null);

        await Task.Run(() => _observerManager.Notify(
            observer => observer.OnNextAsync(message),
            observer => targetObservers.Contains(observer.GetPrimaryKeyString())));
    }

    public async Task SendToGroupsExcept(HubMessage message, string[] groupNames, string[] excludedConnectionIds)
    {
        var excluded = new HashSet<string>(excludedConnectionIds);
        var targetObservers = CollectObservers(groupNames, excluded);

        await Task.Run(() => _observerManager.Notify(
            observer => observer.OnNextAsync(message),
            observer => targetObservers.Contains(observer.GetPrimaryKeyString())));
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        _logger.LogDebug("Registering connection {ConnectionId} in partition {PartitionId}", connectionId,
            this.GetPrimaryKeyLong());

        _observerManager.Subscribe(observer, observer);
        _state.State.ConnectionObservers[connectionId] = observer.GetPrimaryKeyString();
        _state.State.ConnectionGroups.TryAdd(connectionId, new HashSet<string>());

        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        _logger.LogDebug("Removing connection {ConnectionId} from partition {PartitionId}", connectionId,
            this.GetPrimaryKeyLong());

        RemoveConnectionInternal(connectionId, observer);
        return Task.CompletedTask;
    }

    public Task Ping(ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer)
    {
        _logger.LogDebug("Adding connection {ConnectionId} to group {GroupName} in partition {PartitionId}",
            connectionId, groupName, this.GetPrimaryKeyLong());

        var observerKey = observer.GetPrimaryKeyString();

        _observerManager.Subscribe(observer, observer);
        _state.State.ConnectionObservers[connectionId] = observerKey;

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
        _logger.LogDebug("Removing connection {ConnectionId} from group {GroupName} in partition {PartitionId}",
            connectionId, groupName, this.GetPrimaryKeyLong());

        if (_state.State.Groups.TryGetValue(groupName, out var members))
        {
            members.Remove(connectionId);

            if (members.Count == 0)
            {
                _state.State.Groups.Remove(groupName);
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
        _logger.LogDebug("Deactivating group partition grain {PartitionId}", this.GetPrimaryKeyLong());

        _observerManager.ClearExpired();

        if (_state.State.IsEmpty)
        {
            return _state.ClearStateAsync();
        }

        return _state.WriteStateAsync();
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

    private void RemoveConnectionInternal(string connectionId, ISignalRObserver observer)
    {
        _observerManager.Unsubscribe(observer);

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
                    }
                }
            }

            _state.State.ConnectionGroups.Remove(connectionId);
        }

        _state.State.ConnectionObservers.Remove(connectionId);
    }
}
