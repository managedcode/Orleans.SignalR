using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Diagnostics;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;

public abstract class SignalRObserverGrainBase<TGrain> : Grain where TGrain : class, IGrain
{
    private readonly Dictionary<string, ISignalRObserver> _liveObservers = new(StringComparer.Ordinal);
    private readonly Dictionary<ISignalRObserver, string> _observerToConnectionId = new(ReferenceEqualityComparer.Instance);
    private readonly TimeSpan _idleExtension;
    private readonly TimeSpan _observerRefreshInterval;
    private readonly int _failureThreshold;
    private readonly bool _circuitBreakerEnabled;
    private readonly bool _gracePeriodEnabled;
    private IDisposable? _observerRefreshTimer;

    protected SignalRMetrics Metrics { get; } = SignalRMetrics.Instance;

    protected string? MetricsHubName => field ??= ResolveMetricsHubName();

    protected SignalRObserverGrainBase(
        ILogger<TGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions)
    {
        Logger = logger;
        KeepEachConnectionAlive = orleansSignalOptions.Value.KeepEachConnectionAlive;

        var timeout = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerRefreshInterval = timeout;
        _idleExtension = KeepEachConnectionAlive
            ? TimeIntervalHelper.AddExpirationIntervalBuffer(timeout)
            : Timeout.InfiniteTimeSpan;
        var expiration = TimeIntervalHelper.GetObserverExpiration(orleansSignalOptions, timeout);
        ObserverManager = new ObserverManager<ISignalRObserver>(expiration, Logger);

        // Initialize health tracking with circuit breaker and grace period buffering
        _failureThreshold = orleansSignalOptions.Value.ObserverFailureThreshold;
        _circuitBreakerEnabled = orleansSignalOptions.Value.EnableCircuitBreaker;
        _gracePeriodEnabled = orleansSignalOptions.Value.ObserverGracePeriod > TimeSpan.Zero;
        HealthTracker = new ObserverHealthTracker(
            _failureThreshold,
            orleansSignalOptions.Value.ObserverFailureWindow,
            _circuitBreakerEnabled,
            orleansSignalOptions.Value.CircuitBreakerOpenDuration,
            orleansSignalOptions.Value.CircuitBreakerHalfOpenTestInterval,
            orleansSignalOptions.Value.ObserverGracePeriod,
            orleansSignalOptions.Value.MaxBufferedMessagesPerObserver);
    }

    protected ObserverManager<ISignalRObserver> ObserverManager { get; }

    protected ILogger<TGrain> Logger { get; }

    protected bool KeepEachConnectionAlive { get; }

    protected IReadOnlyDictionary<string, ISignalRObserver> LiveObservers => _liveObservers;

    protected abstract int TrackedConnectionCount { get; }

    protected virtual string? ResolveMetricsHubName()
    {
        var key = this.GetPrimaryKeyString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var separatorIndex = key.IndexOf("::", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            return key[..separatorIndex];
        }

        var allDigits = true;
        foreach (var c in key)
        {
            if (!char.IsDigit(c))
            {
                allDigits = false;
                break;
            }
        }

        return allDigits ? null : key;
    }

    /// <summary>
    /// Gets the health tracker for monitoring observer failures and circuit breaker state.
    /// </summary>
    protected ObserverHealthTracker HealthTracker { get; }

    protected void TrackConnection(string connectionId, ISignalRObserver observer)
    {
        ObserverManager.Subscribe(observer, observer);

        // Remove any existing mapping if the observer was previously tracked with a different connection
        if (_liveObservers.TryGetValue(connectionId, out var existingObserver) && !ReferenceEquals(existingObserver, observer))
        {
            _observerToConnectionId.Remove(existingObserver);
        }

        _liveObservers[connectionId] = observer;
        _observerToConnectionId[observer] = connectionId;
        EnsureActiveWhileConnectionsTracked();
        EnsureObserverRefreshTimer();

        if (_gracePeriodEnabled && HealthTracker.IsInGracePeriod(connectionId))
        {
            // Critical: do NOT replay buffered SignalR messages on the Orleans scheduler.
            _ = Task.Run(() => RestoreObserverFromGracePeriodAsync(connectionId, observer));
        }
    }

    protected void UntrackConnection(string connectionId, ISignalRObserver observer)
    {
        ObserverManager.Unsubscribe(observer);
        _liveObservers.Remove(connectionId);
        _observerToConnectionId.Remove(observer);
        if (_gracePeriodEnabled && HealthTracker.IsInGracePeriod(connectionId))
        {
            Metrics.RecordGracePeriodEnded(MetricsHubName);
        }
        HealthTracker.RemoveConnection(connectionId);
        ReleaseWhenIdle();
        StopObserverRefreshTimerIfIdle();
    }

    protected void TouchObserver(ISignalRObserver observer)
    {
        ObserverManager.Subscribe(observer, observer);
        EnsureActiveWhileConnectionsTracked();
        EnsureObserverRefreshTimer();

        if (_gracePeriodEnabled && _observerToConnectionId.TryGetValue(observer, out var connectionId) &&
            HealthTracker.IsInGracePeriod(connectionId))
        {
            // Critical: do NOT replay buffered SignalR messages on the Orleans scheduler.
            _ = Task.Run(() => RestoreObserverFromGracePeriodAsync(connectionId, observer));
        }
    }

    protected bool TryGetLiveObserver(string connectionId, out ISignalRObserver observer)
    {
        return _liveObservers.TryGetValue(connectionId, out observer!);
    }

    /// <summary>
    /// Tries to get a live observer, checking circuit breaker and health status first.
    /// Returns false if the observer's circuit is open, unhealthy, or not found.
    /// </summary>
    protected bool TryGetHealthyLiveObserver(string connectionId, out ISignalRObserver observer)
    {
        if (!_liveObservers.TryGetValue(connectionId, out observer!))
        {
            return false;
        }

        // Use AllowRequest which checks circuit breaker state
        if (!HealthTracker.AllowRequest(connectionId))
        {
            var circuitState = HealthTracker.GetCircuitState(connectionId);
            if (circuitState == CircuitState.Open)
            {
                Logger.LogDebug("Circuit breaker open for connection {ConnectionId}, blocking request.", connectionId);
            }
            else
            {
                Logger.LogDebug("Observer for connection {ConnectionId} is unhealthy, skipping.", connectionId);
            }
            return false;
        }

        return true;
    }

    protected IEnumerable<ISignalRObserver> GetLiveObservers(IEnumerable<string> connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            if (_liveObservers.TryGetValue(connectionId, out var observer))
            {
                yield return observer;
            }
        }
    }

    /// <summary>
    /// Gets only healthy live observers for the given connection IDs.
    /// Respects circuit breaker state.
    /// </summary>
    protected IEnumerable<(string ConnectionId, ISignalRObserver Observer)> GetHealthyLiveObservers(IEnumerable<string> connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            if (_liveObservers.TryGetValue(connectionId, out var observer) && HealthTracker.AllowRequest(connectionId))
            {
                yield return (connectionId, observer);
            }
        }
    }

    protected void ClearObserverTracking()
    {
        ObserverManager.ClearExpired();
        _liveObservers.Clear();
        _observerToConnectionId.Clear();
        HealthTracker.Clear();
        StopObserverRefreshTimer();
    }

    protected void StopObserverRefreshTimerIfIdle()
    {
        if (_liveObservers.Count == 0)
        {
            StopObserverRefreshTimer();
        }
    }

    protected void StopObserverRefreshTimer()
    {
        _observerRefreshTimer?.Dispose();
        _observerRefreshTimer = null;
    }

    /// <summary>
    /// Dispatches a message to live observers with health tracking and circuit breaker.
    /// Observers with open circuits are skipped or have messages buffered during grace period.
    /// Failed observers are tracked and may have their circuits opened or be marked dead
    /// if they exceed the failure threshold.
    /// </summary>
    protected void DispatchToLiveObservers(IEnumerable<ISignalRObserver> observers, HubMessage message)
    {
        foreach (var observer in observers)
        {
            var connectionId = FindConnectionIdForObserver(observer);

            // Check circuit breaker before dispatch
            if (connectionId is not null && !HealthTracker.AllowRequest(connectionId))
            {
                var state = HealthTracker.GetCircuitState(connectionId);
                if (state == CircuitState.Open)
                {
                    // Try to buffer the message if in grace period
                    if (HealthTracker.IsInGracePeriod(connectionId))
                    {
                        if (HealthTracker.BufferMessage(connectionId, message))
                        {
                            Metrics.RecordMessageBuffered(MetricsHubName);
                            if (Logger.IsEnabled(LogLevel.Debug))
                            {
                                Logger.LogDebug("Buffered message for connection {ConnectionId} in grace period.", connectionId);
                            }
                        }
                        else
                        {
                            Metrics.RecordMessageDropped(MetricsHubName, SignalRMetrics.DropReasons.BufferFull);
                        }
                    }
                    else
                    {
                        if (Logger.IsEnabled(LogLevel.Debug))
                        {
                            Logger.LogDebug("Skipping dispatch to connection {ConnectionId} - circuit breaker open.", connectionId);
                        }

                        Metrics.RecordMessageDropped(MetricsHubName, SignalRMetrics.DropReasons.CircuitOpen);
                    }
                    continue;
                }
            }

            var pending = observer.OnNextAsync(message);
            _ = ObserveLiveObserverAsync(pending, connectionId, observer);
        }
    }

    /// <summary>
    /// Dispatches a message to live observers with connection ID tracking for health monitoring.
    /// Respects circuit breaker state and buffers messages during grace period.
    /// </summary>
    protected void DispatchToLiveObserversWithTracking(IEnumerable<(string ConnectionId, ISignalRObserver Observer)> observers, HubMessage message)
    {
        foreach (var (connectionId, observer) in observers)
        {
            // Check circuit breaker before dispatch
            if (!HealthTracker.AllowRequest(connectionId))
            {
                var state = HealthTracker.GetCircuitState(connectionId);
                if (state == CircuitState.Open)
                {
                    // Try to buffer the message if in grace period
                    if (HealthTracker.IsInGracePeriod(connectionId))
                    {
                        if (HealthTracker.BufferMessage(connectionId, message))
                        {
                            Metrics.RecordMessageBuffered(MetricsHubName);
                            if (Logger.IsEnabled(LogLevel.Debug))
                            {
                                Logger.LogDebug("Buffered message for connection {ConnectionId} in grace period.", connectionId);
                            }
                        }
                        else
                        {
                            Metrics.RecordMessageDropped(MetricsHubName, SignalRMetrics.DropReasons.BufferFull);
                        }
                    }
                    {
                        if (Logger.IsEnabled(LogLevel.Debug))
                        {
                            Logger.LogDebug("Skipping dispatch to connection {ConnectionId} - circuit breaker open.", connectionId);
                        }

                        Metrics.RecordMessageDropped(MetricsHubName, SignalRMetrics.DropReasons.CircuitOpen);
                    }
                }
                continue;
            }

            var pending = observer.OnNextAsync(message);
            _ = ObserveLiveObserverAsync(pending, connectionId, observer);
        }
    }

    /// <summary>
    /// Finds the connection ID for an observer using O(1) reverse lookup.
    /// </summary>
    private string? FindConnectionIdForObserver(ISignalRObserver observer)
    {
        return _observerToConnectionId.GetValueOrDefault(observer);
    }

    private async Task ObserveLiveObserverAsync(Task pending, string? connectionId, ISignalRObserver observer)
    {
        try
        {
            var circuitState = connectionId is null
                ? CircuitState.Closed
                : HealthTracker.GetCircuitState(connectionId);
            await pending;

            // Record success - this closes circuit breaker if in half-open state
            if (connectionId is not null)
            {
                HealthTracker.RecordSuccess(connectionId);
                if (circuitState == CircuitState.HalfOpen &&
                    HealthTracker.GetCircuitState(connectionId) == CircuitState.Closed)
                {
                    Metrics.RecordCircuitBreakerClosed(MetricsHubName);
                }
            }
        }
        catch (Exception exception)
        {
            if (connectionId is null)
            {
                OnLiveObserverDispatchFailure(exception);
                return;
            }

            Metrics.RecordObserverFailure(MetricsHubName, exception.GetType().Name);
            // Record failure and handle result
            var result = HealthTracker.RecordFailure(connectionId, exception);

            switch (result)
            {
                case FailureResult.Dead:
                    Logger.LogWarning(
                        exception,
                        "Observer for connection {ConnectionId} exceeded failure threshold ({Threshold}), marking as dead.",
                        connectionId,
                        _failureThreshold);
                    OnObserverDead(connectionId, observer, exception);
                    break;

                case FailureResult.CircuitOpened:
                    Logger.LogWarning(
                        exception,
                        "Circuit breaker opened for connection {ConnectionId} after failure threshold reached. Will retry after cooldown.",
                        connectionId);
                    OnCircuitOpened(connectionId, observer, exception);
                    break;

                case FailureResult.Healthy:
                default:
                    OnLiveObserverDispatchFailure(exception);
                    break;
            }
        }
    }

    /// <summary>
    /// Called when a circuit breaker opens for an observer.
    /// Starts grace period for message buffering if enabled.
    /// Override in derived classes to handle circuit open events.
    /// </summary>
    protected virtual void OnCircuitOpened(string connectionId, ISignalRObserver observer, Exception lastException)
    {
        Metrics.RecordCircuitBreakerOpened(MetricsHubName);
        // Start grace period buffering if enabled
        if (_gracePeriodEnabled && HealthTracker.StartGracePeriod(connectionId))
        {
            Metrics.RecordGracePeriodStarted(MetricsHubName);
            Logger.LogDebug(
                "Started grace period for connection {ConnectionId}. Messages will be buffered until recovery or expiration.",
                connectionId);
        }
    }

    /// <summary>
    /// Called when an observer exceeds the failure threshold and is marked dead.
    /// Override in derived classes to handle dead observer cleanup.
    /// </summary>
    protected virtual void OnObserverDead(string connectionId, ISignalRObserver observer, Exception lastException)
    {
        Metrics.RecordObserverDead(MetricsHubName);
        // Remove from live observers - connection cleanup will happen via normal disconnect flow
        _liveObservers.Remove(connectionId);
        _observerToConnectionId.Remove(observer);
        ObserverManager.Unsubscribe(observer);
        HealthTracker.RemoveConnection(connectionId);

        Logger.LogWarning(
            "Removed dead observer for connection {ConnectionId} due to repeated failures.",
            connectionId);
    }

    /// <summary>
    /// Restores an observer from grace period and replays any buffered messages.
    /// Call this when an observer recovers (e.g., reconnects or sends heartbeat).
    /// </summary>
    /// <returns>Number of buffered messages replayed.</returns>
    protected async Task<int> RestoreObserverFromGracePeriodAsync(string connectionId, ISignalRObserver observer)
    {
        var wasInGracePeriod = _gracePeriodEnabled && HealthTracker.IsInGracePeriod(connectionId);
        var bufferedMessages = HealthTracker.RestoreFromGracePeriod(connectionId);
        if (bufferedMessages.Count == 0)
        {
            if (wasInGracePeriod)
            {
                Metrics.RecordGracePeriodEnded(MetricsHubName);
            }
            return 0;
        }

        Logger.LogInformation(
            "Restoring connection {ConnectionId} from grace period with {MessageCount} buffered messages.",
            connectionId,
            bufferedMessages.Count);

        var replayedCount = 0;
        foreach (var message in bufferedMessages)
        {
            try
            {
                await observer.OnNextAsync(message);
                replayedCount++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to replay buffered message to connection {ConnectionId}. Stopping replay.",
                    connectionId);
                break;
            }
        }

        if (replayedCount > 0)
        {
            Logger.LogDebug(
                "Replayed {ReplayedCount}/{TotalCount} buffered messages to connection {ConnectionId}.",
                replayedCount,
                bufferedMessages.Count,
                connectionId);
        }

        if (wasInGracePeriod)
        {
            Metrics.RecordGracePeriodEnded(MetricsHubName);
        }

        return replayedCount;
    }

    /// <summary>
    /// Called when grace periods expire for observers.
    /// Override to implement custom cleanup logic.
    /// </summary>
    protected virtual void OnGracePeriodsExpired(IReadOnlyList<string> expiredConnectionIds)
    {
        if (expiredConnectionIds.Count == 0)
        {
            return;
        }

        Logger.LogInformation(
            "Grace periods expired for {Count} connections. Buffered messages discarded.",
            expiredConnectionIds.Count);

        foreach (var connectionId in expiredConnectionIds)
        {
            Metrics.RecordGracePeriodEnded(MetricsHubName);
            if (_liveObservers.TryGetValue(connectionId, out var observer))
            {
                OnObserverDead(connectionId, observer, new TimeoutException("Observer grace period expired."));
            }
            else
            {
                HealthTracker.RemoveConnection(connectionId);
            }
        }
    }

    protected abstract void OnLiveObserverDispatchFailure(Exception exception);

    private void EnsureActiveWhileConnectionsTracked()
    {
        if (KeepEachConnectionAlive)
        {
            return;
        }

        if (TrackedConnectionCount > 0)
        {
            DelayDeactivation(_idleExtension);
        }
    }

    private void ReleaseWhenIdle()
    {
        if (KeepEachConnectionAlive)
        {
            return;
        }

        if (TrackedConnectionCount == 0)
        {
            DeactivateOnIdle();
            StopObserverRefreshTimer();
        }
    }

    private void EnsureObserverRefreshTimer()
    {
        if (KeepEachConnectionAlive || _observerRefreshInterval <= TimeSpan.Zero || _liveObservers.Count == 0)
        {
            return;
        }

        if (_observerRefreshTimer is not null)
        {
            return;
        }

        var dueTime = TimeSpan.FromMilliseconds(Math.Max(500, _observerRefreshInterval.TotalMilliseconds / 2));
        _observerRefreshTimer = this.RegisterGrainTimer(
            RefreshObserversAsync,
            new GrainTimerCreationOptions
            {
                DueTime = dueTime,
                Period = dueTime,
                Interleave = true
            });
    }

    private Task RefreshObserversAsync()
    {
        if (_liveObservers.Count == 0)
        {
            StopObserverRefreshTimer();
            return Task.CompletedTask;
        }

        foreach (var observer in _liveObservers.Values)
        {
            ObserverManager.Subscribe(observer, observer);
        }

        // Cleanup expired grace periods
        if (_gracePeriodEnabled)
        {
            var expiredConnectionIds = HealthTracker.CleanupExpiredGracePeriods();
            if (expiredConnectionIds.Count > 0)
            {
                OnGracePeriodsExpired(expiredConnectionIds);
            }
        }

        DelayDeactivation(_idleExtension);
        return Task.CompletedTask;
    }
}
