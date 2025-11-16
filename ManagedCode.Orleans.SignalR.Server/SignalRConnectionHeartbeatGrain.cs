using System;
using System.Threading;
using System.Threading.Tasks;
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
public class SignalRConnectionHeartbeatGrain(
    ILogger<SignalRConnectionHeartbeatGrain> logger) : Grain, ISignalRConnectionHeartbeatGrain
{
    private ConnectionHeartbeatRegistration? _registration;
    private IDisposable? _timer;

    public Task Start(ConnectionHeartbeatRegistration registration)
    {
        _registration = registration;
        ResetTimer(registration.Interval);
        logger.LogDebug("Heartbeat started for connection grain {Key} (partitioned={Partitioned}, partitionId={PartitionId}).",
            this.GetPrimaryKeyString(), registration.UsePartitioning, registration.PartitionId);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        ResetTimer(null);
        _registration = null;
        logger.LogDebug("Heartbeat stopped for connection grain {Key}.", this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        ResetTimer(null);
        _registration = null;
        return base.OnDeactivateAsync(reason, cancellationToken);
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
            logger.LogDebug(ex, "Heartbeat ping failed for connection grain {Key}.", this.GetPrimaryKeyString());
        }

        return Task.CompletedTask;
    }
}
