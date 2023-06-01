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
[GrainType($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}")]
public class SignalRConnectionHolderGrain : Grain, ISignalRConnectionHolderGrain
{
    private readonly ILogger<SignalRConnectionHolderGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<ConnectionState> _stateStorage;

    public SignalRConnectionHolderGrain(ILogger<SignalRConnectionHolderGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions, IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRConnectionHolderGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage)
    {
        _logger = logger;
        _stateStorage = stateStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerManager = new ObserverManager<ISignalRObserver>(TimeIntervalHelper.AddExpirationIntervalBuffer(timeSpan), _logger);
    }
    
    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        Logs.AddConnection(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString(), connectionId);
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        Logs.RemoveConnection(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString(), connectionId);
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
    }

    public async Task SendToAll(HubMessage message)
    {
        await Task.Yield();
        Logs.SendToAll(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString());
        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        await Task.Yield();
        Logs.SendToAllExcept(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString(), excludedConnectionIds);
        var hashSet = new HashSet<string>();
        foreach (var connectionId in excludedConnectionIds)
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
                hashSet.Add(observer);

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString()));
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        await Task.Yield();
        Logs.SendToConnection(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString(), connectionId);
        
        if (!_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            return false;

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => connection.GetPrimaryKeyString() == observer);

        return true;
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        await Task.Yield();
        Logs.SendToConnections(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString(), connectionIds);
       
        var hashSet = new HashSet<string>();
        foreach (var connectionId in connectionIds)
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
                hashSet.Add(observer);

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => hashSet.Contains(connection.GetPrimaryKeyString()));
    }

    public async Task Ping(ISignalRObserver observer)
    {
        await Task.Yield();
        Logs.Ping(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
    }
    
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(_logger, nameof(SignalRConnectionHolderGrain),this.GetPrimaryKeyString());
        _observerManager.ClearExpired();
        
        if (_observerManager.Count == 0 || _stateStorage.State.ConnectionIds.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}