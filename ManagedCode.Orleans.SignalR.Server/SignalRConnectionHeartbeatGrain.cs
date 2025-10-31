using System;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.{nameof(SignalRConnectionHeartbeatGrain)}")]
public class SignalRConnectionHeartbeatGrain(
    ILogger<SignalRConnectionHeartbeatGrain> logger) : Grain, ISignalRConnectionHeartbeatGrain
{
    private readonly ILogger<SignalRConnectionHeartbeatGrain> _logger = logger;
    private ConnectionHeartbeatRegistration? _registration;
    private IDisposable? _timer;

    public Task Start(ConnectionHeartbeatRegistration registration)
    {
        _registration = registration;
        ResetTimer(registration.Interval);
        _logger.LogDebug("Heartbeat started for connection grain {Key} (partitioned={Partitioned}, partitionId={PartitionId}).",
            this.GetPrimaryKeyString(), registration.UsePartitioning, registration.PartitionId);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        ResetTimer(null);
        _registration = null;
        _logger.LogDebug("Heartbeat stopped for connection grain {Key}.", this.GetPrimaryKeyString());
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
            _timer = RegisterTimer(OnTimerTickAsync, null, dueTime, dueTime);
        }
    }

    private async Task OnTimerTickAsync(object? _)
    {
        if (_registration is null)
        {
            return;
        }

        try
        {
            if (_registration.UsePartitioning)
            {
                var partition = NameHelperGenerator.GetConnectionPartitionGrain(GrainFactory, _registration.HubKey, _registration.PartitionId);
                await partition.Ping(_registration.Observer);
            }
            else
            {
                var holder = NameHelperGenerator.GetConnectionHolderGrain(GrainFactory, _registration.HubKey);
                await holder.Ping(_registration.Observer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat ping failed for connection grain {Key}.", this.GetPrimaryKeyString());
        }
    }
}
