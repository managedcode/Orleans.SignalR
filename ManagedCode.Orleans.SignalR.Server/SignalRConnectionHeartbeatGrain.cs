using System;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
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
        _state.State ??= new ConnectionHeartbeatState();
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (_state.State.Registration is { } stored)
        {
            _registration = stored;
            ResetTimer(stored.Interval);
            _logger.LogDebug("Heartbeat restored for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
                this.GetPrimaryKeyString(), stored.HubKey, stored.UsePartitioning, stored.PartitionId);
        }

        return base.OnActivateAsync(cancellationToken);
    }

    public Task Start(ConnectionHeartbeatRegistration registration)
    {
        _registration = registration;
        _state.State.Registration = registration;
        ResetTimer(registration.Interval);
        _logger.LogDebug("Heartbeat started for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
            this.GetPrimaryKeyString(), registration.HubKey, registration.UsePartitioning, registration.PartitionId);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        ResetTimer(null);
        _state.State.Registration = null;
        _registration = null;
        _logger.LogDebug("Heartbeat stopped for connection grain {Key}.", this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        ResetTimer(null);
        if (_state.State.Registration is null)
        {
            await _state.ClearStateAsync(cancellationToken);
        }
        else
        {
            await _state.WriteStateAsync(cancellationToken);
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

    private Task OnTimerTickAsync(object? state)
    {
        if (_registration is null)
        {
            return Task.CompletedTask;
        }

        var grainIds = _registration.GrainIds;
        if (grainIds.IsDefaultOrEmpty)
        {
            return Task.CompletedTask;
        }

        try
        {
            foreach (var grainId in grainIds)
            {
                var grain = GrainFactory.GetGrain(grainId);
                var manager = grain.AsReference<IObserverConnectionManager>();
                _ = manager.Ping(_registration.Observer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat ping failed for connection grain {Key}.", this.GetPrimaryKeyString());
        }

        return Task.CompletedTask;
    }
}
