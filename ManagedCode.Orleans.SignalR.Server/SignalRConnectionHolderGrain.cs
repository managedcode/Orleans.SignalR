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
        var expiration = TimeIntervalHelper.GetObserverExpiration(orleansSignalOptions, timeSpan);
        _observerManager = new ObserverManager<ISignalRObserver>(expiration, _logger);
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.AddConnection(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.RemoveConnection(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }

    public async Task SendToAll(HubMessage message)
    {
        Logs.SendToAll(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());
        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        Logs.SendToAllExcept(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), excludedConnectionIds);
        var hashSet = new HashSet<string>();
        foreach (var connectionId in excludedConnectionIds)
        {
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        Logs.SendToConnection(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);

        if (!_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
        {
            return false;
        }

        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message),
            connection => connection.GetPrimaryKeyString() == observer));

        return true;
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        Logs.SendToConnections(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionIds);

        var hashSet = new HashSet<string>();
        foreach (var connectionId in connectionIds)
        {
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message),
            connection => hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(_logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());
        _observerManager.ClearExpired();

        if (_observerManager.Count == 0 || _stateStorage.State.ConnectionIds.Count == 0)
        {
            await _stateStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _stateStorage.WriteStateAsync(cancellationToken);
        }
    }
}
