using System;
using System.Collections.Generic;
using System.Globalization;
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
[GrainType($"ManagedCode.{nameof(SignalRConnectionPartitionGrain)}")]
public class SignalRConnectionPartitionGrain : SignalRObserverGrainBase<SignalRConnectionPartitionGrain>, ISignalRConnectionPartitionGrain
{
    private readonly IPersistentState<ConnectionState> _stateStorage;

    public SignalRConnectionPartitionGrain(
        ILogger<SignalRConnectionPartitionGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRConnectionPartitionGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionState> stateStorage)
        : base(logger, orleansSignalOptions, hubOptions)
    {
        _stateStorage = stateStorage;

        _stateStorage.State ??= new ConnectionState();
    }

    protected override int TrackedConnectionCount => _stateStorage.State.ConnectionIds.Count;

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.AddConnection(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), connectionId);
        _stateStorage.State.ConnectionIds.Add(connectionId, observer.GetPrimaryKeyString());
        TrackConnection(connectionId, observer);
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.RemoveConnection(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), connectionId);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        UntrackConnection(connectionId, observer);
        return Task.CompletedTask;
    }

    public async Task SendToPartition(HubMessage message)
    {
        Logs.SendToAll(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture));

        if (LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message)));
    }

    public async Task SendToPartitionExcept(HubMessage message, string[] excludedConnectionIds)
    {
        Logs.SendToAllExcept(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), excludedConnectionIds);

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
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => !hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public async Task<bool> SendToConnection(HubMessage message, string connectionId)
    {
        Logs.SendToConnection(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), connectionId);

        if (!_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
        {
            return false;
        }

        if (TryGetLiveObserver(connectionId, out var live))
        {
            _ = live.OnNextAsync(message);
            return true;
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => connection.GetPrimaryKeyString() == observer));

        return true;
    }

    public async Task SendToConnections(HubMessage message, string[] connectionIds)
    {
        Logs.SendToConnections(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), connectionIds);

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
            if (_stateStorage.State.ConnectionIds.TryGetValue(connectionId, out var observer))
            {
                hashSet.Add(observer);
            }
        }

        await Task.Run(() => ObserverManager.Notify(s => s.OnNextAsync(message),
            connection => hashSet.Contains(connection.GetPrimaryKeyString())));
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture));
        TouchObserver(observer);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(Logger, nameof(SignalRConnectionPartitionGrain), this.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture));
        ClearObserverTracking();

        if (ObserverManager.Count == 0 || _stateStorage.State.ConnectionIds.Count == 0)
        {
            await _stateStorage.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _stateStorage.WriteStateAsync(cancellationToken);
        }
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for partition {PartitionId}.", this.GetPrimaryKeyLong());
    }
}
