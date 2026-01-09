using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

/// <summary>
/// Tracks observer health by monitoring delivery failures.
/// Observers exceeding the failure threshold within the time window are marked as dead.
/// </summary>
public sealed class ObserverHealthTracker
{
    private readonly Dictionary<string, ObserverHealthState> _healthStates = new(StringComparer.Ordinal);
    private readonly int _failureThreshold;
    private readonly TimeSpan _failureWindow;
    private readonly object _lock = new();

    public ObserverHealthTracker(int failureThreshold, TimeSpan failureWindow)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _failureWindow = failureWindow;
    }

    /// <summary>
    /// Gets whether health tracking is enabled.
    /// </summary>
    public bool IsEnabled => _failureThreshold > 0;

    /// <summary>
    /// Records a successful delivery to an observer, resetting its failure count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSuccess(string connectionId)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (_lock)
        {
            if (_healthStates.TryGetValue(connectionId, out var state))
            {
                state.Reset();
            }
        }
    }

    /// <summary>
    /// Records a delivery failure for an observer.
    /// Returns true if the observer has exceeded the failure threshold and should be removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RecordFailure(string connectionId, Exception? exception = null)
    {
        if (!IsEnabled)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_healthStates.TryGetValue(connectionId, out var state))
            {
                state = new ObserverHealthState(_failureWindow);
                _healthStates[connectionId] = state;
            }

            state.RecordFailure(exception);
            return state.FailureCount >= _failureThreshold;
        }
    }

    /// <summary>
    /// Checks if an observer is healthy (not exceeding failure threshold).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsHealthy(string connectionId)
    {
        if (!IsEnabled)
        {
            return true;
        }

        lock (_lock)
        {
            if (!_healthStates.TryGetValue(connectionId, out var state))
            {
                return true;
            }

            return state.FailureCount < _failureThreshold;
        }
    }

    /// <summary>
    /// Gets the current failure count for an observer.
    /// </summary>
    public int GetFailureCount(string connectionId)
    {
        lock (_lock)
        {
            if (_healthStates.TryGetValue(connectionId, out var state))
            {
                return state.FailureCount;
            }

            return 0;
        }
    }

    /// <summary>
    /// Removes health tracking state for a connection.
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _healthStates.Remove(connectionId);
        }
    }

    /// <summary>
    /// Clears all health tracking state.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _healthStates.Clear();
        }
    }

    /// <summary>
    /// Gets all connection IDs that have exceeded the failure threshold.
    /// </summary>
    public List<string> GetDeadObservers()
    {
        var dead = new List<string>();

        lock (_lock)
        {
            foreach (var (connectionId, state) in _healthStates)
            {
                if (state.FailureCount >= _failureThreshold)
                {
                    dead.Add(connectionId);
                }
            }
        }

        return dead;
    }

    private sealed class ObserverHealthState
    {
        private readonly TimeSpan _failureWindow;
        private readonly List<DateTime> _failureTimestamps = new();
        private Exception? _lastException;

        public ObserverHealthState(TimeSpan failureWindow)
        {
            _failureWindow = failureWindow;
        }

        public int FailureCount
        {
            get
            {
                PruneOldFailures();
                return _failureTimestamps.Count;
            }
        }

        public Exception? LastException => _lastException;

        public void RecordFailure(Exception? exception)
        {
            PruneOldFailures();
            _failureTimestamps.Add(DateTime.UtcNow);
            _lastException = exception;
        }

        public void Reset()
        {
            _failureTimestamps.Clear();
            _lastException = null;
        }

        private void PruneOldFailures()
        {
            if (_failureTimestamps.Count == 0)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - _failureWindow;
            _failureTimestamps.RemoveAll(t => t < cutoff);
        }
    }
}
