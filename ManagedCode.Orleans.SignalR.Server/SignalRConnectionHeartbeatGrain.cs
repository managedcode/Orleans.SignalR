using System;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Server.Helpers;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRConnectionHeartbeatGrain)}")]
public sealed class SignalRConnectionHeartbeatGrain : Grain, ISignalRConnectionHeartbeatGrain
{
    private readonly ILogger<SignalRConnectionHeartbeatGrain> _logger;
    private readonly IPersistentState<ConnectionHeartbeatState> _state;
    private ConnectionHeartbeatRegistration? _registration;
    private IDisposable? _timer;

    public SignalRConnectionHeartbeatGrain(
        ILogger<SignalRConnectionHeartbeatGrain> logger,
        [PersistentState(nameof(SignalRConnectionHeartbeatGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<ConnectionHeartbeatState> state)
    {
        _logger = logger;
        _state = state;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await _state.ReadStateAsync(cancellationToken);
        _state.State ??= new ConnectionHeartbeatState();
        if (_state.State.Registration is { } stored)
        {
            _registration = stored;
            ResetTimer(stored.Interval);
            _logger.LogDebug("Heartbeat restored for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
                this.GetPrimaryKeyString(), stored.HubKey, stored.UsePartitioning, stored.PartitionId);
        }

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task Start(ConnectionHeartbeatRegistration registration)
    {
        _registration = registration;
        ResetTimer(registration.Interval);
        _logger.LogDebug("Heartbeat started for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
            this.GetPrimaryKeyString(), registration.HubKey, registration.UsePartitioning, registration.PartitionId);
        await _state.WriteStateSafeAsync(state =>
        {
            state.Registration = registration;
            return true;
        });
    }

    public async Task Stop()
    {
        ResetTimer(null);
        _registration = null;
        _logger.LogDebug("Heartbeat stopped for connection grain {Key}.", this.GetPrimaryKeyString());
        await _state.WriteStateSafeAsync(state =>
        {
            state.Registration = null;
            return true;
        });
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        ResetTimer(null);
        try
        {
            if (_state.State.Registration is null)
            {
                await _state.ClearStateSafeAsync(cancellationToken);
            }
            else
            {
                await _state.WriteStateSafeAsync(cancellationToken);
            }
        }
        catch (OrleansMessageRejectionException ex)
        {
            // Storage grains may be unavailable during silo shutdown
            _logger.LogDebug(ex, "Unable to persist state during deactivation for grain {Key} - storage unavailable.", this.GetPrimaryKeyString());
        }
    }

    private void ResetTimer(TimeSpan? interval)
    {
        _timer?.Dispose();
        _timer = null;

        if (interval is { } period && period > TimeSpan.Zero)
        {
            var dueTime = TimeSpan.FromMilliseconds(Math.Max(500, period.TotalMilliseconds / 2));
            _timer = this.RegisterGrainTimer(
                () => OnTimerTickAsync(null),
                new GrainTimerCreationOptions
                {
                    DueTime = dueTime,
                    Period = dueTime,
                    Interleave = true
                });
        }
    }

    private Task OnTimerTickAsync(object? _)
    {
        // Capture registration to avoid null reference if Stop() is called during reentrant execution
        var registration = _registration;
        if (registration is null)
        {
            return Task.CompletedTask;
        }

        var grainIds = registration.GrainIds;
        if (grainIds.IsDefaultOrEmpty)
        {
            return Task.CompletedTask;
        }

        var connectionId = registration.ConnectionId;
        var observer = registration.Observer;
        try
        {
            foreach (var grainId in grainIds)
            {
                var grain = GrainFactory.GetGrain(grainId);
                var manager = grain.AsReference<IObserverConnectionManager>();
                if (!string.IsNullOrEmpty(connectionId))
                {
                    _ = manager.AddConnection(connectionId, observer);
                }
                _ = manager.Ping(observer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat ping failed for connection grain {Key}.", this.GetPrimaryKeyString());
        }

        return Task.CompletedTask;
    }
}
