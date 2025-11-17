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

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRGroupGrain)}")]
public class SignalRGroupGrain(
    ILogger<SignalRGroupGrain> logger,
    IOptions<OrleansSignalROptions> orleansSignalOptions,
    IOptions<HubOptions> hubOptions,
    [PersistentState(nameof(SignalRGroupGrain), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage)
    : SignalRObserverGrainBase<SignalRGroupGrain>(logger, orleansSignalOptions, hubOptions), ISignalRGroupGrain
{
    protected override int TrackedConnectionCount => stateStorage.State.ConnectionIds.Count;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await stateStorage.ReadStateAsync(cancellationToken);
        stateStorage.State ??= new ConnectionState();
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task SendToGroup(HubMessage message)
    {
        Logs.SendToGroup(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString());

        if (LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds)
    {
        Logs.SendToGroupExcept(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), excludedConnectionIds);

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
            Logs.AddConnection(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        UntrackConnection(connectionId, observer);
        var removed = await stateStorage.WriteStateSafeAsync(state => state.ConnectionIds.Remove(connectionId));

        if (removed)
        {
            Logs.RemoveConnection(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString());
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(Logger, nameof(SignalRGroupGrain), this.GetPrimaryKeyString());
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
        Logger.LogWarning(exception, "Live observer send failed for group {Group}.", this.GetPrimaryKeyString());
    }
}
