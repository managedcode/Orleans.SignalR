using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
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
    private readonly TimeSpan _idleExtension;
    private readonly TimeSpan _observerRefreshInterval;
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
    }

    protected ObserverManager<ISignalRObserver> ObserverManager { get; }

    protected ILogger<TGrain> Logger { get; }

    protected bool KeepEachConnectionAlive { get; }

    protected IReadOnlyDictionary<string, ISignalRObserver> LiveObservers => _liveObservers;

    protected abstract int TrackedConnectionCount { get; }

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

    protected void ClearObserverTracking()
    {
        ObserverManager.ClearExpired();
        _liveObservers.Clear();
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

    protected void DispatchToLiveObservers(IEnumerable<ISignalRObserver> observers, HubMessage message)
    {
        foreach (var observer in observers)
        {
            var pending = observer.OnNextAsync(message);
            _ = ObserveLiveObserverAsync(pending);
        }
    }

    private async Task ObserveLiveObserverAsync(Task pending)
    {
        try
        {
            await pending;
        }
        catch (Exception exception)
        {
            OnLiveObserverDispatchFailure(exception);
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
