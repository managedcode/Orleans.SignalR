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
[GrainType($"ManagedCode.{nameof(SignalRGroupGrain)}")]
public class SignalRGroupGrain : Grain, ISignalRGroupGrain
{
    private readonly ILogger<SignalRGroupGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<ConnectionState> _stateStorage;

    public SignalRGroupGrain(ILogger<SignalRGroupGrain> logger, IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRGroupGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage)
    {
        _logger = logger;
        _stateStorage = stateStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerManager = new ObserverManager<ISignalRObserver>(timeSpan * 1.2, _logger);
    }

    public async Task SendToGroup(HubMessage message)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; SendToGroup", this.GetPrimaryKeyString());
        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public async Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; SendToGroupExcept: {ConnectionId}", this.GetPrimaryKeyString(),
            excludedConnectionIds);
        var hashSet = new HashSet<string>();
        foreach (var connectionId in excludedConnectionIds)
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
                hashSet.Add(observer);

        await _observerManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString()));
    }

    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; AddConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; RemoveConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
    }

    public async Task Ping(ISignalRObserver observer)
    {
        await Task.Yield();
        _observerManager.Subscribe(observer, observer);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _observerManager.ClearExpired();
        
        if (_observerManager.Count == 0 || _stateStorage.State.ConnectionIds.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}