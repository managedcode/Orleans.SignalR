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
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    public Task SendToUser(HubMessage message)
    {
        Logs.SendToUser(Logger, nameof(SignalRUserGrain), this.GetPrimaryKeyString());

        if (LiveObservers.Count > 0)
        {
            DispatchToLiveObservers(LiveObservers.Values, message);
            return Task.CompletedTask;
        }

        if (ObserverManager.Count == 0)
        {
            // Enforce message queue limit to prevent unbounded memory growth
            var maxMessages = _orleansSignalOptions.Value.MaxQueuedMessagesPerUser;
            if (maxMessages > 0 && messagesStorage.State.Messages.Count >= maxMessages)
            {
                // Remove oldest messages to make room
                var toRemove = messagesStorage.State.Messages.Count - maxMessages + 1;
                var oldestMessages = messagesStorage.State.Messages
                    .OrderBy(kvp => kvp.Value)
                    .Take(toRemove)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var oldMessage in oldestMessages)
                {
                    messagesStorage.State.Messages.Remove(oldMessage);
                }

                Logger.LogWarning("Dropped {Count} oldest messages for user {User} due to queue limit {Limit}",
                    toRemove, this.GetPrimaryKeyString(), maxMessages);
            }

            messagesStorage.State.Messages.Add(message, DateTime.UtcNow.Add(_orleansSignalOptions.Value.KeepMessageInterval));
            return Task.CompletedTask;
        }

        ObserverManager.Notify(s => s.OnNextAsync(message));
        return Task.CompletedTask;
    }

    public Task RequestMessage()
    {
        if (messagesStorage.State.Messages.Count == 0)
        {
            return Task.CompletedTask;
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
                    ObserverManager.Notify(s => s.OnNextAsync(message.Key));
                }
            }

            messagesStorage.State.Messages.Remove(message.Key);
        }

        return Task.CompletedTask;
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
            await stateStorage.ClearStateSafeAsync(cancellationToken);
        }
        else
        {
            await stateStorage.WriteStateSafeAsync(cancellationToken);
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
            await messagesStorage.ClearStateSafeAsync(cancellationToken);
        }
        else
        {
            await messagesStorage.WriteStateSafeAsync(cancellationToken);
        }
    }

    protected override void OnLiveObserverDispatchFailure(Exception exception)
    {
        Logger.LogWarning(exception, "Live observer send failed for user {User}.", this.GetPrimaryKeyString());
    }
}
