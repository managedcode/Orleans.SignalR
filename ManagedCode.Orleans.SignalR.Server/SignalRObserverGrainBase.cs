using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
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
    private readonly ObserverHealthTracker _healthTracker;
    private readonly TimeSpan _idleExtension;
    private readonly TimeSpan _observerRefreshInterval;
    private readonly int _failureThreshold;
    private IDisposable? _observerRefreshTimer;

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

        // Initialize health tracking
        _failureThreshold = orleansSignalOptions.Value.ObserverFailureThreshold;
        _healthTracker = new ObserverHealthTracker(
            _failureThreshold,
            orleansSignalOptions.Value.ObserverFailureWindow);
    }

    protected ObserverManager<ISignalRObserver> ObserverManager { get; }

    protected ILogger<TGrain> Logger { get; }

    protected bool KeepEachConnectionAlive { get; }

    protected IReadOnlyDictionary<string, ISignalRObserver> LiveObservers => _liveObservers;

    protected abstract int TrackedConnectionCount { get; }

    /// <summary>
    /// Gets the health tracker for monitoring observer failures.
    /// </summary>
    protected ObserverHealthTracker HealthTracker => _healthTracker;

    protected void TrackConnection(string connectionId, ISignalRObserver observer)
    {
        ObserverManager.Subscribe(observer, observer);
        _liveObservers[connectionId] = observer;
        EnsureActiveWhileConnectionsTracked();
        EnsureObserverRefreshTimer();
    }

    protected void UntrackConnection(string connectionId, ISignalRObserver observer)
    {
        ObserverManager.Unsubscribe(observer);
        _liveObservers.Remove(connectionId);
        _healthTracker.RemoveConnection(connectionId);
        ReleaseWhenIdle();
        StopObserverRefreshTimerIfIdle();
    }

    protected void TouchObserver(ISignalRObserver observer)
    {
        ObserverManager.Subscribe(observer, observer);
        EnsureActiveWhileConnectionsTracked();
        EnsureObserverRefreshTimer();
    }

    protected bool TryGetLiveObserver(string connectionId, out ISignalRObserver observer)
    {
        return _liveObservers.TryGetValue(connectionId, out observer!);
    }

    /// <summary>
    /// Tries to get a live observer, checking health status first.
    /// Returns false if the observer is unhealthy or not found.
    /// </summary>
    protected bool TryGetHealthyLiveObserver(string connectionId, out ISignalRObserver observer)
    {
        if (!_liveObservers.TryGetValue(connectionId, out observer!))
        {
            return false;
        }

        if (!_healthTracker.IsHealthy(connectionId))
        {
            Logger.LogDebug("Observer for connection {ConnectionId} is unhealthy, skipping.", connectionId);
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
    /// </summary>
    protected IEnumerable<(string ConnectionId, ISignalRObserver Observer)> GetHealthyLiveObservers(IEnumerable<string> connectionIds)
    {
        foreach (var connectionId in connectionIds)
        {
            if (_liveObservers.TryGetValue(connectionId, out var observer) && _healthTracker.IsHealthy(connectionId))
            {
                yield return (connectionId, observer);
            }
        }
    }

    protected void ClearObserverTracking()
    {
        ObserverManager.ClearExpired();
        _liveObservers.Clear();
        _healthTracker.Clear();
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
    /// Dispatches a message to live observers with health tracking.
    /// Observers that fail are tracked and removed if they exceed the failure threshold.
    /// </summary>
    protected void DispatchToLiveObservers(IEnumerable<ISignalRObserver> observers, HubMessage message)
    {
        foreach (var observer in observers)
        {
            var connectionId = FindConnectionIdForObserver(observer);
            var pending = observer.OnNextAsync(message);
            _ = ObserveLiveObserverAsync(pending, connectionId, observer);
        }
    }

    /// <summary>
    /// Dispatches a message to live observers with connection ID tracking for health monitoring.
    /// </summary>
    protected void DispatchToLiveObserversWithTracking(IEnumerable<(string ConnectionId, ISignalRObserver Observer)> observers, HubMessage message)
    {
        foreach (var (connectionId, observer) in observers)
        {
            // Skip unhealthy observers
            if (!_healthTracker.IsHealthy(connectionId))
            {
                continue;
            }

            var pending = observer.OnNextAsync(message);
            _ = ObserveLiveObserverAsync(pending, connectionId, observer);
        }
    }

    private string? FindConnectionIdForObserver(ISignalRObserver observer)
    {
        foreach (var (connectionId, obs) in _liveObservers)
        {
            if (ReferenceEquals(obs, observer))
            {
                return connectionId;
            }
        }

        return null;
    }

    private async Task ObserveLiveObserverAsync(Task pending, string? connectionId, ISignalRObserver observer)
    {
        try
        {
            await pending;

            // Record success if we have connection tracking
            if (connectionId is not null)
            {
                _healthTracker.RecordSuccess(connectionId);
            }
        }
        catch (Exception exception)
        {
            // Record failure and check if observer should be removed
            if (connectionId is not null && _healthTracker.RecordFailure(connectionId, exception))
            {
                Logger.LogWarning(
                    exception,
                    "Observer for connection {ConnectionId} exceeded failure threshold ({Threshold}), marking as dead.",
                    connectionId,
                    _failureThreshold);

                // Trigger removal callback
                OnObserverDead(connectionId, observer, exception);
            }
            else
            {
                OnLiveObserverDispatchFailure(exception);
            }
        }
    }

    /// <summary>
    /// Called when an observer exceeds the failure threshold and should be removed.
    /// Override in derived classes to handle dead observer cleanup.
    /// </summary>
    protected virtual void OnObserverDead(string connectionId, ISignalRObserver observer, Exception lastException)
    {
        // Remove from live observers - connection cleanup will happen via normal disconnect flow
        _liveObservers.Remove(connectionId);
        ObserverManager.Unsubscribe(observer);

        Logger.LogWarning(
            "Removed dead observer for connection {ConnectionId} due to repeated failures.",
            connectionId);
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
            () => RefreshObserversAsync(),
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

        DelayDeactivation(_idleExtension);
        return Task.CompletedTask;
    }
}
