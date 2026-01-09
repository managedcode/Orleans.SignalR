using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

/// <summary>
/// Tracks observer health by monitoring delivery failures with circuit breaker support.
/// Observers exceeding the failure threshold have their circuit opened to prevent cascade failures.
/// </summary>
public sealed class ObserverHealthTracker
{
    private readonly Dictionary<string, ObserverHealthState> _healthStates = new(StringComparer.Ordinal);
    private readonly int _failureThreshold;
    private readonly TimeSpan _failureWindow;
    private readonly bool _circuitBreakerEnabled;
    private readonly TimeSpan _circuitOpenDuration;
    private readonly TimeSpan _halfOpenTestInterval;
    private readonly object _lock = new();

    public ObserverHealthTracker(
        int failureThreshold,
        TimeSpan failureWindow,
        bool circuitBreakerEnabled = true,
        TimeSpan? circuitOpenDuration = null,
        TimeSpan? halfOpenTestInterval = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _failureWindow = failureWindow;
        _circuitBreakerEnabled = circuitBreakerEnabled;
        _circuitOpenDuration = circuitOpenDuration ?? TimeSpan.FromSeconds(30);
        _halfOpenTestInterval = halfOpenTestInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Gets whether health tracking is enabled.
    /// </summary>
    public bool IsEnabled => _failureThreshold > 0;

    /// <summary>
    /// Gets whether circuit breaker is enabled.
    /// </summary>
    public bool CircuitBreakerEnabled => _circuitBreakerEnabled;

    /// <summary>
    /// Records a successful delivery to an observer, resetting its failure count and closing circuit.
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
                state.RecordSuccess();
            }
        }
    }

    /// <summary>
    /// Records a delivery failure for an observer.
    /// Returns a result indicating whether the observer is dead or circuit is open.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FailureResult RecordFailure(string connectionId, Exception? exception = null)
    {
        if (!IsEnabled)
        {
            return FailureResult.Healthy;
        }

        lock (_lock)
        {
            if (!_healthStates.TryGetValue(connectionId, out var state))
            {
                state = new ObserverHealthState(
                    _failureWindow,
                    _circuitBreakerEnabled,
                    _failureThreshold,
                    _circuitOpenDuration,
                    _halfOpenTestInterval);
                _healthStates[connectionId] = state;
            }

            return state.RecordFailure(exception);
        }
    }

    /// <summary>
    /// Checks if an observer allows requests (healthy and circuit not open).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowRequest(string connectionId)
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

            return state.AllowRequest();
        }
    }

    /// <summary>
    /// Checks if an observer is healthy (not exceeding failure threshold).
    /// Note: Use AllowRequest() for circuit breaker awareness.
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

            return state.IsHealthy;
        }
    }

    /// <summary>
    /// Gets the circuit breaker state for a connection.
    /// </summary>
    public CircuitState GetCircuitState(string connectionId)
    {
        lock (_lock)
        {
            if (_healthStates.TryGetValue(connectionId, out var state))
            {
                return state.CircuitState;
            }

            return CircuitState.Closed;
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
    /// Gets all connection IDs that have exceeded the failure threshold (dead observers).
    /// </summary>
    public List<string> GetDeadObservers()
    {
        var dead = new List<string>();

        lock (_lock)
        {
            foreach (var (connectionId, state) in _healthStates)
            {
                if (state.IsDead)
                {
                    dead.Add(connectionId);
                }
            }
        }

        return dead;
    }

    /// <summary>
    /// Gets all connection IDs with open circuits.
    /// </summary>
    public List<string> GetOpenCircuits()
    {
        var open = new List<string>();

        lock (_lock)
        {
            foreach (var (connectionId, state) in _healthStates)
            {
                if (state.CircuitState == CircuitState.Open)
                {
                    open.Add(connectionId);
                }
            }
        }

        return open;
    }

    /// <summary>
    /// Gets statistics about observer health.
    /// </summary>
    public HealthStatistics GetStatistics()
    {
        lock (_lock)
        {
            var stats = new HealthStatistics();

            foreach (var state in _healthStates.Values)
            {
                stats.TotalTracked++;

                switch (state.CircuitState)
                {
                    case CircuitState.Closed:
                        stats.ClosedCircuits++;
                        break;
                    case CircuitState.Open:
                        stats.OpenCircuits++;
                        break;
                    case CircuitState.HalfOpen:
                        stats.HalfOpenCircuits++;
                        break;
                }

                if (state.IsDead)
                {
                    stats.DeadObservers++;
                }
            }

            return stats;
        }
    }

    private sealed class ObserverHealthState
    {
        private readonly TimeSpan _failureWindow;
        private readonly bool _circuitBreakerEnabled;
        private readonly int _failureThreshold;
        private readonly List<DateTime> _failureTimestamps = new();
        private readonly ObserverCircuitBreaker? _circuitBreaker;
        private Exception? _lastException;
        private bool _markedDead;

        public ObserverHealthState(
            TimeSpan failureWindow,
            bool circuitBreakerEnabled,
            int failureThreshold,
            TimeSpan circuitOpenDuration,
            TimeSpan halfOpenTestInterval)
        {
            _failureWindow = failureWindow;
            _circuitBreakerEnabled = circuitBreakerEnabled;
            _failureThreshold = failureThreshold;

            if (circuitBreakerEnabled)
            {
                _circuitBreaker = new ObserverCircuitBreaker(
                    failureThreshold,
                    circuitOpenDuration,
                    halfOpenTestInterval);
            }
        }

        public int FailureCount
        {
            get
            {
                PruneOldFailures();
                return _failureTimestamps.Count;
            }
        }

        public bool IsHealthy => !_markedDead && FailureCount < _failureThreshold;

        public bool IsDead => _markedDead;

        public CircuitState CircuitState => _circuitBreaker?.State ?? CircuitState.Closed;

        public Exception? LastException => _lastException;

        public bool AllowRequest()
        {
            if (_markedDead)
            {
                return false;
            }

            if (_circuitBreaker is not null)
            {
                return _circuitBreaker.AllowRequest();
            }

            return IsHealthy;
        }

        public FailureResult RecordFailure(Exception? exception)
        {
            PruneOldFailures();
            _failureTimestamps.Add(DateTime.UtcNow);
            _lastException = exception;

            var failureCount = _failureTimestamps.Count;
            var circuitOpened = _circuitBreaker?.RecordFailure(exception) ?? false;

            if (failureCount >= _failureThreshold)
            {
                _markedDead = true;
                return FailureResult.Dead;
            }

            if (circuitOpened)
            {
                return FailureResult.CircuitOpened;
            }

            return FailureResult.Healthy;
        }

        public void RecordSuccess()
        {
            _failureTimestamps.Clear();
            _lastException = null;
            _circuitBreaker?.RecordSuccess();

            // Allow recovery from dead state if circuit breaker succeeds in half-open
            if (_markedDead && _circuitBreaker?.State == CircuitState.Closed)
            {
                _markedDead = false;
            }
        }

        public void Reset()
        {
            _failureTimestamps.Clear();
            _lastException = null;
            _markedDead = false;
            _circuitBreaker?.Reset();
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

/// <summary>
/// Result of recording a failure.
/// </summary>
public enum FailureResult
{
    /// <summary>
    /// Observer is still healthy, failure recorded but below threshold.
    /// </summary>
    Healthy,

    /// <summary>
    /// Circuit breaker opened due to this failure.
    /// </summary>
    CircuitOpened,

    /// <summary>
    /// Observer exceeded failure threshold and is marked dead.
    /// </summary>
    Dead
}

/// <summary>
/// Statistics about observer health tracking.
/// </summary>
public sealed class HealthStatistics
{
    public int TotalTracked { get; set; }
    public int ClosedCircuits { get; set; }
    public int OpenCircuits { get; set; }
    public int HalfOpenCircuits { get; set; }
    public int DeadObservers { get; set; }
}
