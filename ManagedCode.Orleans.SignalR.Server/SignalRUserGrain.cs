using System;
using System.Linq;
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
[GrainType($"ManagedCode.{nameof(SignalRUserGrain)}")]
public class SignalRUserGrain : Grain, ISignalRUserGrain
{
    private readonly ILogger<SignalRUserGrain> _logger;
    private readonly IOptions<OrleansSignalROptions> _orleansSignalOptions;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IPersistentState<HubMessageState> _messagesStorage;

    public SignalRUserGrain(ILogger<SignalRUserGrain> logger, 
        IOptions<OrleansSignalROptions> orleansSignalOptions, IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRUserGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage,
        [PersistentState(nameof(SignalRUserGrain)+nameof(HubMessageState), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<HubMessageState> messagesStorage)
    {
        _logger = logger;
        _orleansSignalOptions = orleansSignalOptions;
        _stateStorage = stateStorage;
        _messagesStorage = messagesStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerManager = new ObserverManager<ISignalRObserver>(timeSpan * 1.2, _logger);
    }

    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; AddConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());

        if (_messagesStorage.State.Messages.Count > 0)
        {
            var currentDateTime = DateTime.UtcNow;
            foreach (var message in _messagesStorage.State.Messages.ToArray())
            {
                _messagesStorage.State.Messages.Remove(message.Key);
                if(message.Value >= currentDateTime)
                    await SendToUser(message.Key);
            }
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; RemoveConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        _observerManager.Unsubscribe(observer);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
    }

    public async Task SendToUser(HubMessage message)
    {
        await Task.Yield();
        if (_observerManager.Count == 0)
        {
            _messagesStorage.State.Messages.Add(message, DateTime.UtcNow.Add(_orleansSignalOptions.Value.KeepMessageInterval));
            return;
        }
        
        _logger.LogInformation("Hub: {PrimaryKeyString}; SendToUser", this.GetPrimaryKeyString());
        await _observerManager.Notify(s => s.OnNextAsync(message));
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
        
        var currentDateTime = DateTime.UtcNow;
        foreach (var message in _messagesStorage.State.Messages.ToArray())
        {
            if (message.Value <= currentDateTime)
                _messagesStorage.State.Messages.Remove(message.Key);
        }
        
        if(_messagesStorage.State.Messages.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();

    }
}