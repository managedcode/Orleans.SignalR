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

[GrainType($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}")]
public class SignalRConnectionHolderGrain(
    ILogger<SignalRConnectionHolderGrain> logger,
    IOptions<OrleansSignalROptions> orleansSignalOptions,
    IOptions<HubOptions> hubOptions,
    [PersistentState(nameof(SignalRConnectionHolderGrain), OrleansSignalROptions.OrleansSignalRStorage)]
    IPersistentState<ConnectionState> stateStorage)
    : SignalRObserverGrainBase<SignalRConnectionHolderGrain>(logger, orleansSignalOptions, hubOptions), ISignalRConnectionHolderGrain
{
    protected override int TrackedConnectionCount => stateStorage.State.ConnectionIds.Count;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await stateStorage.ReadStateAsync(cancellationToken);
        stateStorage.State ??= new ConnectionState();
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
            Logs.AddConnection(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        UntrackConnection(connectionId, observer);
        var removed = await stateStorage.WriteStateSafeAsync(state => state.ConnectionIds.Remove(connectionId));

        if (removed)
        {
            Logs.RemoveConnection(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task SendToAll(HubMessage message)
    {
        Logs.SendToAll(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());

        if (LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds)
    {
        Logs.SendToAllExcept(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), excludedConnectionIds);

        if (LiveObservers.Count > 0)
        {
            var excluded = new HashSet<string>(excludedConnectionIds, StringComparer.Ordinal);
            var targets = LiveObservers.Where(kvp => !excluded.Contains(kvp.Key)).Select(kvp => kvp.Value);
            DispatchToLiveObservers(targets, message);
            return;
        }

        var hashSet = new HashSet<string>();
        foreach (var connectionId in excludedConnectionIds)
        {
            if (stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        Logs.SendToConnection(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionId);

        if (!stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
        {
            return false;
        }

        if (TryGetLiveObserver(connectionId, out var liveObserver))
        {
            _ = liveObserver.OnNextAsync(message);
            return true;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => connection.GetPrimaryKeyString() == observer));

        return true;
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        Logs.SendToConnections(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString(), connectionIds);

        if (LiveObservers.Count > 0)
        {
            List<ISignalRObserver>? targets = null;
            foreach (var connectionId in connectionIds)
            {
                if (TryGetLiveObserver(connectionId, out var observer))
                {
                    targets ??= new List<ISignalRObserver>();
                    targets.Add(observer);
                }
            }

            if (targets is not null)
            {
                DispatchToLiveObservers(targets, message);
                return;
            }
        }

        var hashSet = new HashSet<string>();
        foreach (var connectionId in connectionIds)
        {
            if (stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(Logger, nameof(SignalRConnectionHolderGrain), this.GetPrimaryKeyString());
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
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for holder {Holder}.", this.GetPrimaryKeyString());
    }
}
