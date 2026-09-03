using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
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
    private const double HeartbeatIntervalDivisor = 2;
    private const double HeartbeatLeaseIntervalMultiplier = 2;
    private const double MinimumHeartbeatIntervalMilliseconds = 500;
    private readonly ILogger<SignalRConnectionHeartbeatGrain> _logger;
    private readonly IPersistentState<ConnectionHeartbeatState> _state;
    private readonly StateWriteLock _stateWriteLock = new();
    private ConnectionHeartbeatRegistration? _registration;
    private IDisposable? _timer;
    private long _leaseRenewedAtTimestamp;
    private int _registrationVersion;
    private bool _registrationPersisted;

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
            _registrationPersisted = true;
            RenewLease();
            _registrationVersion++;
            ResetTimer(stored.Interval);
            _logger.LogDebug("Heartbeat restored for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
                this.GetPrimaryKeyString(), stored.HubKey, stored.UsePartitioning, stored.PartitionId);
        }

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task Start(ConnectionHeartbeatRegistration registration)
    {
        var registrationChanged = !MatchesCurrentRegistration(registration);
        if (registrationChanged)
        {
            _registrationPersisted = false;
        }

        _registration = registration;
        RenewLease();

        if (!registrationChanged && _timer is not null && _registrationPersisted)
        {
            return;
        }

        if (registrationChanged || _timer is null)
        {
            _registrationVersion++;
            ResetTimer(registration.Interval);
            _logger.LogDebug("Heartbeat started or updated for connection grain {Key} (hub={Hub}, partitioned={Partitioned}, partitionId={PartitionId}).",
                this.GetPrimaryKeyString(), registration.HubKey, registration.UsePartitioning, registration.PartitionId);
        }

        var persistenceVersion = _registrationVersion;
        try
        {
            var persisted = await _stateWriteLock.RunAsync(() => _state.WriteStateSafeAsync(state =>
            {
                if (persistenceVersion != _registrationVersion || !MatchesCurrentRegistration(registration))
                {
                    return false;
                }

                state.Registration = registration;
                return true;
            }));

            if (persisted && persistenceVersion == _registrationVersion && MatchesCurrentRegistration(registration))
            {
                _registrationPersisted = true;
            }
        }
        catch
        {
            if (persistenceVersion == _registrationVersion && MatchesCurrentRegistration(registration))
            {
                _registrationPersisted = false;
            }

            throw;
        }
    }

    public async Task Stop()
    {
        await StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        var stoppedRegistration = _registration;
        ResetTimer(null);
        _registration = null;
        _registrationPersisted = false;
        _leaseRenewedAtTimestamp = 0;
        var stopVersion = ++_registrationVersion;
        _logger.LogDebug("Heartbeat stopped for connection grain {Key}.", this.GetPrimaryKeyString());

        if (stoppedRegistration is not null)
        {
            await RemoveRegistrationTargetsAsync(stoppedRegistration);
        }

        await _stateWriteLock.RunAsync(() => _state.WriteStateSafeAsync(state =>
        {
            if (_registrationVersion != stopVersion)
            {
                return false;
            }

            state.Registration = null;
            return true;
        }));

        if (_registrationVersion == stopVersion)
        {
            DeactivateOnIdle();
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        ResetTimer(null);
        try
        {
            await _stateWriteLock.RunAsync(async () =>
            {
                if (_state.State.Registration is null)
                {
                    await _state.ClearStateSafeAsync(cancellationToken);
                }
                else
                {
                    await _state.WriteStateSafeAsync(cancellationToken);
                }
            });
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
            var dueTime = TimeSpan.FromMilliseconds(Math.Max(
                MinimumHeartbeatIntervalMilliseconds,
                period.TotalMilliseconds / HeartbeatIntervalDivisor));
            _timer = this.RegisterGrainTimer(
                () => OnTimerTickAsync(null),
                CreateTimerOptions(dueTime));
        }
    }

    internal static GrainTimerCreationOptions CreateTimerOptions(TimeSpan period)
    {
        return new GrainTimerCreationOptions
        {
            DueTime = period,
            Period = period,
            Interleave = true,
            KeepAlive = true
        };
    }

    private async Task OnTimerTickAsync(object? _)
    {
        // Capture registration to avoid null reference if Stop() is called during reentrant execution
        var registration = _registration;
        var registrationVersion = _registrationVersion;
        if (registration is null)
        {
            return;
        }

        if (LeaseExpired(registration.Interval))
        {
            _logger.LogDebug("Heartbeat lease expired for connection grain {Key}; stopping orphaned activation.", this.GetPrimaryKeyString());
            await StopCoreAsync();
            return;
        }

        var grainIds = registration.GrainIds;
        if (grainIds.IsDefaultOrEmpty)
        {
            return;
        }

        var connectionId = registration.ConnectionId;
        var observer = registration.Observer;
        foreach (var grainId in grainIds)
        {
            if (registrationVersion != _registrationVersion)
            {
                return;
            }

            try
            {
                var grain = GrainFactory.GetGrain(grainId);
                var manager = grain.AsReference<IObserverConnectionManager>();
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await manager.AddConnection(connectionId, observer);

                    if (registrationVersion != _registrationVersion &&
                        !IsCurrentTarget(grainId, connectionId, observer))
                    {
                        await manager.RemoveConnection(connectionId, observer);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Heartbeat refresh failed for connection grain {Key} and target {TargetGrainId}.", this.GetPrimaryKeyString(), grainId);
            }
        }
    }

    private void RenewLease()
    {
        _leaseRenewedAtTimestamp = Stopwatch.GetTimestamp();
    }

    private bool LeaseExpired(TimeSpan interval)
    {
        return _leaseRenewedAtTimestamp == 0 ||
               Stopwatch.GetElapsedTime(_leaseRenewedAtTimestamp) >= interval * HeartbeatLeaseIntervalMultiplier;
    }

    private bool MatchesCurrentRegistration(ConnectionHeartbeatRegistration registration)
    {
        var current = _registration;
        if (current is null ||
            !string.Equals(current.HubKey, registration.HubKey, StringComparison.Ordinal) ||
            current.UsePartitioning != registration.UsePartitioning ||
            current.PartitionId != registration.PartitionId ||
            !current.Observer.Equals(registration.Observer) ||
            current.Interval != registration.Interval ||
            !string.Equals(current.ConnectionId, registration.ConnectionId, StringComparison.Ordinal) ||
            current.GrainIds.Length != registration.GrainIds.Length)
        {
            return false;
        }

        var sequenceMatches = true;
        for (var index = 0; index < current.GrainIds.Length; index++)
        {
            if (!current.GrainIds[index].Equals(registration.GrainIds[index]))
            {
                sequenceMatches = false;
                break;
            }
        }

        return sequenceMatches || new HashSet<GrainId>(current.GrainIds).SetEquals(registration.GrainIds);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Orphan cleanup must continue when an individual target or coordinator is unavailable.")]
    private async Task RemoveRegistrationTargetsAsync(ConnectionHeartbeatRegistration registration)
    {
        if (string.IsNullOrEmpty(registration.ConnectionId))
        {
            return;
        }

        if (!registration.GrainIds.IsDefaultOrEmpty)
        {
            var removals = new Task[registration.GrainIds.Length];
            var index = 0;
            foreach (var grainId in registration.GrainIds)
            {
                removals[index++] = RemoveRegistrationTargetAsync(registration, grainId);
            }

            await Task.WhenAll(removals);
        }

        if (registration.UsePartitioning)
        {
            try
            {
                var coordinator = GrainFactory.GetGrain<ISignalRConnectionCoordinatorGrain>(
                    NameHelperGenerator.CleanString(registration.HubKey));
                await coordinator.NotifyConnectionRemoved(registration.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Heartbeat coordinator cleanup failed for connection grain {Key} and hub {HubKey}.",
                    this.GetPrimaryKeyString(),
                    registration.HubKey);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Failure of one target must not prevent cleanup of the remaining targets.")]
    private async Task RemoveRegistrationTargetAsync(ConnectionHeartbeatRegistration registration, GrainId grainId)
    {
        try
        {
            var grain = GrainFactory.GetGrain(grainId);
            var manager = grain.AsReference<IObserverConnectionManager>();
            await manager.RemoveConnection(registration.ConnectionId, registration.Observer);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Heartbeat cleanup failed for connection grain {Key} and target {TargetGrainId}.",
                this.GetPrimaryKeyString(),
                grainId);
        }
    }

    private bool IsCurrentTarget(GrainId grainId, string connectionId, ISignalRObserver observer)
    {
        var current = _registration;
        if (current is null ||
            !string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal) ||
            !current.Observer.Equals(observer))
        {
            return false;
        }

        foreach (var currentGrainId in current.GrainIds)
        {
            if (currentGrainId.Equals(grainId))
            {
                return true;
            }
        }

        return false;
    }
}
