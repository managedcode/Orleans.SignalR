using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Diagnostics;
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
    private readonly StateWriteLock _stateWriteLock = new();

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
        var persisted = await _stateWriteLock.RunAsync(() => stateStorage.WriteStateSafeAsync(state =>
        {
            var hasExisting = state.ConnectionIds.TryGetValue(connectionId, out var existing);
            var changed = !hasExisting || !string.Equals(existing, observerKey, StringComparison.Ordinal);
            state.ConnectionIds[connectionId] = observerKey;
            return changed;
        }));

        if (persisted)
        {
            Logs.AddConnection(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString(), connectionId);
        }
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        UntrackConnection(connectionId, observer);
        var removed = await _stateWriteLock.RunAsync(() => stateStorage.WriteStateSafeAsync(state => state.ConnectionIds.Remove(connectionId)));

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

        var deliveryId = Guid.NewGuid();
        var expiresAtUtc = DateTime.UtcNow.Add(_orleansSignalOptions.Value.KeepMessageInterval);
        var maxMessages = _orleansSignalOptions.Value.MaxQueuedMessagesPerUser;
        var removedByLimit = 0;
        var persisted = await _stateWriteLock.RunAsync(() => messagesStorage.WriteStateSafeAsync(state =>
        {
            if (state.Queue.Any(item => item.DeliveryId == deliveryId))
            {
                return false;
            }

            RemoveExpiredMessages(state, DateTime.UtcNow);
            state.Queue.Add(new QueuedHubMessage(deliveryId, message, expiresAtUtc));
            removedByLimit = 0;
            if (maxMessages > 0 && state.Queue.Count > maxMessages)
            {
                removedByLimit = state.Queue.Count - maxMessages;
                state.Queue.RemoveRange(0, removedByLimit);
            }

            return true;
        }));

        if (!persisted)
        {
            return;
        }

        Metrics.RecordMessageBuffered(MetricsHubName);
        if (removedByLimit > 0)
        {
            Metrics.RecordMessageDropped(MetricsHubName, SignalRMetrics.DropReasons.OfflineQueueLimit, removedByLimit);
            Logger.LogWarning(
                "Dropped {Count} oldest messages for user {User} due to queue limit {Limit}",
                removedByLimit,
                this.GetPrimaryKeyString(),
                maxMessages);
        }
    }

    public async Task RequestMessage()
    {
        List<QueuedHubMessage> pendingMessages = [];
        await _stateWriteLock.RunAsync(() => messagesStorage.WriteStateSafeAsync(state =>
        {
            var removedExpired = RemoveExpiredMessages(state, DateTime.UtcNow);
            pendingMessages = [.. state.Queue];
            return removedExpired > 0;
        }));

        if (pendingMessages.Count == 0 || LiveObservers.Count == 0)
        {
            return;
        }

        var observers = LiveObservers.Values.ToArray();
        var userGrainKey = this.GetPrimaryKeyString();
        var sourceGrainId = this.GetGrainId();
        // Critical: do NOT execute SignalR observer notifications on the Orleans scheduler.
        _ = Task.Run(() => ReplayPendingMessagesAsync(observers, pendingMessages, userGrainKey, sourceGrainId));
    }

    public async Task AcknowledgeMessage(Guid deliveryId)
    {
        await _stateWriteLock.RunAsync(() => messagesStorage.WriteStateSafeAsync(state =>
            state.Queue.RemoveAll(item => item.DeliveryId == deliveryId) > 0));
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
            await _stateWriteLock.RunAsync(() => stateStorage.ClearStateSafeAsync(cancellationToken));
        }
        else
        {
            await _stateWriteLock.RunAsync(() => stateStorage.WriteStateSafeAsync(cancellationToken));
        }

        RemoveExpiredMessages(messagesStorage.State, DateTime.UtcNow);

        if (messagesStorage.State.Queue.Count == 0)
        {
            await _stateWriteLock.RunAsync(() => messagesStorage.ClearStateSafeAsync(cancellationToken));
        }
        else
        {
            await _stateWriteLock.RunAsync(() => messagesStorage.WriteStateSafeAsync(cancellationToken));
        }
    }

    private async Task ReplayPendingMessagesAsync(
        IReadOnlyList<ISignalRObserver> observers,
        IReadOnlyList<QueuedHubMessage> messages,
        string userGrainKey,
        GrainId sourceGrainId)
    {
        foreach (var pending in messages)
        {
            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnNextWithAcknowledgementAsync(
                        pending.Message,
                        pending.DeliveryId,
                        userGrainKey,
                        sourceGrainId);
                }
                catch (Exception exception)
                {
                    OnLiveObserverDispatchFailure(exception);
                }
            }
        }
    }

    private static int RemoveExpiredMessages(HubMessageState state, DateTime utcNow)
    {
        return state.Queue.RemoveAll(item => item.ExpiresAtUtc <= utcNow);
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for user {User}.", this.GetPrimaryKeyString());
    }
}
