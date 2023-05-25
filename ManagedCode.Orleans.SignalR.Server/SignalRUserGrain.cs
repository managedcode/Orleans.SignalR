using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
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
[GrainType($"ManagedCode.{nameof(SignalRUserGrain)}")]
public class SignalRUserGrain : Grain, ISignalRUserGrain
{
    private readonly ILogger<SignalRUserGrain> _logger;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    
    public SignalRUserGrain(ILogger<SignalRUserGrain> logger,  
        [PersistentState(nameof(SignalRUserGrain), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage,
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
        if(_stateStorage.State.ConnectionIds.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }

    public async Task SendToUser(HubMessage message)
    {
        await _observerManager.Notify(s => s.OnNextAsync(message));
    }
    
    public ValueTask Ping(ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer,observer);
        return ValueTask.CompletedTask;
    }
}