using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Server.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[GrainType($"ManagedCode.{nameof(SignalRUserGrain)}")]
public class SignalRUserGrain(
    ILogger<SignalRUserGrain> logger,
    IOptions<OrleansSignalROptions> orleansSignalOptions,
    IOptions<HubOptions> hubOptions,
    [PersistentState(nameof(SignalRUserGrain), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage,
    [PersistentState(nameof(SignalRUserGrain) + nameof(HubMessageState), OrleansSignalROptions.OrleansSignalRStorage)]
    IPersistentState<HubMessageState> messagesStorage)
    : SignalRObserverGrainBase<SignalRUserGrain>(logger, orleansSignalOptions, hubOptions), ISignalRUserGrain
{
    private readonly IOptions<OrleansSignalROptions> _orleansSignalOptions = orleansSignalOptions;

    protected override int TrackedConnectionCount => stateStorage.State.ConnectionIds.Count;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await stateStorage.ReadStateAsync(cancellationToken);
        await messagesStorage.ReadStateAsync(cancellationToken);
        stateStorage.State ??= new ConnectionState();
        messagesStorage.State ??= new HubMessageState();
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        TrackConnection(connectionId, observer);
        var observerKey = observer.GetPrimaryKeyString();
        var persisted = await stateStorage.WriteStateSafeAsync(state =>
        {
            var hasExisting = state.ConnectionIds.TryGetValue(connectionId, out var existing);
            var changed = !hasExisting || !string.Equals(existing, observerKey, StringComparison.Ordinal);
            state.ConnectionIds[connectionId] = observerKey;
            return changed;
        });

        if (persisted)
        {
            Logs.AddConnection(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        UntrackConnection(connectionId, observer);
        var removed = await stateStorage.WriteStateSafeAsync(state => state.ConnectionIds.Remove(connectionId));

        if (removed)
        {
            Logs.RemoveConnection(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task SendToUser(HubMessage message)
    {
        Logs.SendToUser(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString());

        if (LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return;
        }

        if (ObserverManager.Count == 0)
        {
            messagesStorage.State.Messages.Add(message, DateTime.UtcNow.Add(_orleansSignalOptions.Value.KeepMessageInterval));
            return;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task RequestMessage()
    {
        if (messagesStorage.State.Messages.Count == 0)
        {
            return;
        }

        var currentDateTime = DateTime.UtcNow;
        foreach (var message in messagesStorage.State.Messages.ToArray())
        {
            if (message.Value >= currentDateTime)
            {
                if (LiveObservers.Count > 0)
                {
                    DispatchToLiveObservers(LiveObservers.Values, message.Key);
                }
                else
                {
                    await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message.Key)));
                }
            }

            messagesStorage.State.Messages.Remove(message.Key);
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
        var hasConnections = stateStorage.State.ConnectionIds.Count > 0;
        ClearObserverTracking();

        if (!hasConnections)
        {
            await stateStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await stateStorage.WriteStateAsync(cancellationToken);
        }

        var currentDateTime = DateTime.UtcNow;
        foreach (var message in messagesStorage.State.Messages.ToArray())
        {
            if (message.Value <= currentDateTime)
            {
                messagesStorage.State.Messages.Remove(message.Key);
            }
        }

        if (messagesStorage.State.Messages.Count == 0)
        {
            await messagesStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await messagesStorage.WriteStateAsync(cancellationToken);
        }
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for user {User}.", this.GetPrimaryKeyString());
    }
}
