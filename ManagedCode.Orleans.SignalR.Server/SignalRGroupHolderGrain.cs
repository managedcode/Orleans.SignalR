using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[ActivationCountBasedPlacement]
[GrainType($"ManagedCode.{nameof(SignalRGroupHolderGrain)}")]
public class SignalRGroupHolderGrain : Grain, ISignalRGroupHolderGrain
{
    private readonly ILogger<SignalRGroupHolderGrain> _logger;
    private readonly IPersistentState<ConnectionGroupState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    
    public SignalRGroupHolderGrain(ILogger<SignalRGroupHolderGrain> logger,  
        [PersistentState(nameof(SignalRGroupHolderGrain), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionGroupState> stateStorage,
        IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _stateStorage = stateStorage;
        _options = options;
        _observerManager = new ObserverManager<ISignalRObserver>(
            //_globalHubOptions.Value.KeepAliveInterval.Value, //TODO:
            TimeSpan.FromMinutes(5), 
            logger);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(_stateStorage.State.Groups.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }

    public Task AddConnectionToGroup(string connectionId, ISignalRObserver observer, string groupName)
    {
        if (_stateStorage.State.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        else
            _stateStorage.State.Groups.Add(groupName, new ConnectionState
            {
                ConnectionIds = new()
                {
                    [connectionId] = observer.GetPrimaryKeyString()
                }
            });

        return Task.CompletedTask;
    }
    
    public Task RemoveConnectionFromGroup(string connectionId, ISignalRObserver observer, string groupName)
    {
        if (_stateStorage.State.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }


    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        //ignore for this grain
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        foreach (var connections in _stateStorage.State.Groups.Values)
            connections.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }
    
    public ValueTask Ping(ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer,observer);
        return ValueTask.CompletedTask;
    }
}