using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR;
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
[GrainType($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}")]
public class SignalRConnectionHolderGrain : Grain, ISignalRConnectionHolderGrain
{
    private readonly IOptions<HubOptions>? _globalHubOptions;
    private readonly ILogger<SignalRConnectionHolderGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly IPersistentState<ConnectionState> _stateStorage;

    public SignalRConnectionHolderGrain(ILogger<SignalRConnectionHolderGrain> logger,
        IOptions<HubOptions>? globalHubOptions,
        [PersistentState(nameof(SignalRConnectionHolderGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage, IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _globalHubOptions = globalHubOptions;
        _stateStorage = stateStorage;
        _options = options;
        _observerManager = new ObserverManager<ISignalRObserver>(
            //_globalHubOptions.Value.KeepAliveInterval.Value, //TODO:
            TimeSpan.FromMinutes(5), logger);
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

    public async Task SendToAll(HubMessage message)
    {
        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        var hashSet = new HashSet<string>();
        foreach (var connectionId in excludedConnectionIds)
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
                hashSet.Add(observer);

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString()));
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        if (!_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            return false;

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => connection.GetPrimaryKeyString() == observer);

        return true;
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        var hashSet = new HashSet<string>();

        foreach (var connectionId in connectionIds)
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
                hashSet.Add(observer);

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => hashSet.Contains(connection.GetPrimaryKeyString()));
    }

    public ValueTask Ping(ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        return ValueTask.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _observerManager.ClearExpired();

        //foreach (var observer in _observerManager)
        //{
        //connection = _stateStorage.State.ConnectionIds.FirstOrDefault(f=>f.Value == observer.GetPrimaryKeyString())
        //}

        if (_stateStorage.State.ConnectionIds.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}