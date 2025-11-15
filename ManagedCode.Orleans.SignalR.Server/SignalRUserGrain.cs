using System;
using System.Collections.Generic;
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

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRUserGrain)}")]
public class SignalRUserGrain : SignalRObserverGrainBase<SignalRUserGrain>, ISignalRUserGrain
{
    private readonly IOptions<OrleansSignalROptions> _orleansSignalOptions;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IPersistentState<HubMessageState> _messagesStorage;

    public SignalRUserGrain(ILogger<SignalRUserGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRUserGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage,
        [PersistentState(nameof(SignalRUserGrain) + nameof(HubMessageState), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<HubMessageState> messagesStorage)
        : base(logger, orleansSignalOptions, hubOptions)
    {
        _orleansSignalOptions = orleansSignalOptions;
        _stateStorage = stateStorage;
        _messagesStorage = messagesStorage;
    }

    protected override int TrackedConnectionCount => _stateStorage.State.ConnectionIds.Count;

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.AddConnection(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString(), connectionId);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        TrackConnection(connectionId, observer);
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.RemoveConnection(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString(), connectionId);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        UntrackConnection(connectionId, observer);
        return Task.CompletedTask;
    }

    public async Task SendToUser(HubMessage message)
    {
        Logs.SendToUser(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString());

        if (!KeepEachConnectionAlive && LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return;
        }

        if (ObserverManager.Count == 0)
        {
            _messagesStorage.State.Messages.Add(message, DateTime.UtcNow.Add(_orleansSignalOptions.Value.KeepMessageInterval));
            return;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task RequestMessage()
    {
        if (_messagesStorage.State.Messages.Count == 0)
        {
            return;
        }

        var currentDateTime = DateTime.UtcNow;
        foreach (var message in _messagesStorage.State.Messages.ToArray())
        {
            if (message.Value >= currentDateTime)
            {
                if (!KeepEachConnectionAlive && LiveObservers.Count > 0)
                {
                    DispatchToLiveObservers(LiveObservers.Values, message.Key);
                }
                else
                {
                    await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message.Key)));
                }
            }

            _messagesStorage.State.Messages.Remove(message.Key);
        }
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString());
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString());
        ClearObserverTracking();

        if (ObserverManager.Count == 0 || _stateStorage.State.ConnectionIds.Count == 0)
        {
            await _stateStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _stateStorage.WriteStateAsync(cancellationToken);
        }

        var currentDateTime = DateTime.UtcNow;
        foreach (var message in _messagesStorage.State.Messages.ToArray())
        {
            if (message.Value <= currentDateTime)
            {
                _messagesStorage.State.Messages.Remove(message.Key);
            }
        }

        if (_messagesStorage.State.Messages.Count == 0)
        {
            await _messagesStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _messagesStorage.WriteStateAsync(cancellationToken);
        }
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for user {User}.", this.GetPrimaryKeyString());
    }
}
