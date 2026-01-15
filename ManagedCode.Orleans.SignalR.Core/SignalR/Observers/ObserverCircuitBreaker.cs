using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

/// <summary>
/// Circuit breaker states following the standard pattern.
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed, requests flow through normally.
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open, requests are blocked to prevent cascade failures.
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is testing if the observer has recovered.
    /// One request is allowed through to test connectivity.
    /// </summary>
    HalfOpen
}

/// <summary>
/// Circuit breaker for an individual observer to prevent cascade failures.
/// Thread-safe implementation using lock-free operations where possible.
/// </summary>
public sealed class ObserverCircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeSpan halfOpenTestInterval)
{
    private readonly int _failureThreshold = Math.Max(1, failureThreshold);
    private readonly TimeSpan _openDuration = openDuration;
    private readonly TimeSpan _halfOpenTestInterval = halfOpenTestInterval;

    private int _failureCount;
    private int _state = (int)CircuitState.Closed; // CircuitState as int for Interlocked operations
    private long _lastFailureTimestamp;
    private long _lastHalfOpenTestTimestamp;
    private long _openedAtTimestamp;
    private readonly object _lock = new();

    /// <summary>
    /// Gets the current state of the circuit breaker.
    /// </summary>
    public CircuitState State
    {
        get
        {
            var currentState = (CircuitState)Volatile.Read(ref _state);

            // Check if we should transition from Open to HalfOpen
            if (currentState == CircuitState.Open)
            {
                if (Stopwatch.GetElapsedTime(_openedAtTimestamp) >= _openDuration)
                {
                    TryTransitionToHalfOpen();
                    return (CircuitState)Volatile.Read(ref _state);
                }
            }

            return currentState;
        }
    }

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public int FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>
    /// Gets the last exception that caused a failure.
    /// </summary>
    public Exception? LastException { get; private set; }

    /// <summary>
    /// Gets whether the circuit allows requests through.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowRequest()
    {
        var currentState = State; // This handles Open -> HalfOpen transition

        switch (currentState)
        {
            case CircuitState.Closed:
                return true;

            case CircuitState.Open:
                return false;

            case CircuitState.HalfOpen:
                // In half-open state, allow one test request periodically
                return ShouldAllowHalfOpenTest();

            default:
                return false;
        }
    }

    /// <summary>
    /// Records a successful operation, potentially closing the circuit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSuccess()
    {
        var currentState = (CircuitState)Volatile.Read(ref _state);

        if (currentState == CircuitState.HalfOpen)
        {
            // Success in half-open state closes the circuit
            Close();
        }
        else if (currentState == CircuitState.Closed)
        {
            // Reset failure count on success
            Interlocked.Exchange(ref _failureCount, 0);
            LastException = null;
        }
    }

    /// <summary>
    /// Records a failed operation, potentially opening the circuit.
    /// Returns true if the circuit just transitioned to Open state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RecordFailure(Exception? exception = null)
    {
        LastException = exception;
        _lastFailureTimestamp = Stopwatch.GetTimestamp();

        var currentState = (CircuitState)Volatile.Read(ref _state);

        if (currentState == CircuitState.HalfOpen)
        {
            // Failure in half-open state reopens the circuit
            Open();
            return true;
        }

        if (currentState == CircuitState.Closed)
        {
            var newCount = Interlocked.Increment(ref _failureCount);
            if (newCount >= _failureThreshold)
            {
                Open();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Manually opens the circuit.
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            _state = (int)CircuitState.Open;
            _openedAtTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Manually closes the circuit and resets failure count.
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            _state = (int)CircuitState.Closed;
            _failureCount = 0;
            LastException = null;
        }
    }

    /// <summary>
    /// Resets the circuit breaker to its initial state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = (int)CircuitState.Closed;
            _failureCount = 0;
            LastException = null;
            _openedAtTimestamp = 0;
            _lastFailureTimestamp = 0;
            _lastHalfOpenTestTimestamp = 0;
        }
    }

    private void TryTransitionToHalfOpen()
    {
        lock (_lock)
        {
            if (_state == (int)CircuitState.Open && Stopwatch.GetElapsedTime(_openedAtTimestamp) >= _openDuration)
            {
                _state = (int)CircuitState.HalfOpen;
                _lastHalfOpenTestTimestamp = 0; // Allow immediate test
            }
        }
    }

    private bool ShouldAllowHalfOpenTest()
    {
        lock (_lock)
        {
            if (_state != (int)CircuitState.HalfOpen)
            {
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            if (_lastHalfOpenTestTimestamp == 0 || Stopwatch.GetElapsedTime(_lastHalfOpenTestTimestamp, now) >= _halfOpenTestInterval)
            {
                _lastHalfOpenTestTimestamp = now;
                return true;
            }

            return false;
        }
    }
}
