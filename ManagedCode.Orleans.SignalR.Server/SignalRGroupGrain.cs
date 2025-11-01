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
        _observerManager = new ObserverManager<ISignalRObserver>(TimeIntervalHelper.AddExpirationIntervalBuffer(timeSpan), _logger);
    }

    public async Task SendToGroup(HubMessage message)
    {
        Logs.SendToGroup(_logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString());
        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds)
    {
        Logs.SendToGroupExcept(_logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), excludedConnectionIds);
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

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.AddConnection(_logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), connectionId);
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.RemoveConnection(_logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), connectionId);
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(_logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(_logger, nameof(DeactivationReason), this.GetPrimaryKeyString());
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
