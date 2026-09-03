using System;

namespace ManagedCode.Orleans.SignalR.Core.Config;

public class OrleansSignalROptions
{
    public const string OrleansSignalRStorage = "ManagedCode.Orleans.SignalR.Storage";

    /// <summary>
    ///     Gets or sets the time window clients have to send a message before the server closes the connection.
    ///     The default timeout is 30 seconds.
    /// </summary>
    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     When true, each local SignalR connection renews a bounded lease on a dedicated Orleans
    ///     heartbeat grain, which refreshes the connection's observer registrations.
    ///     The default value is true. Set it to false to avoid the per-connection heartbeat grain
    ///     and rely on the regular observer and activation lifecycle instead.
    /// </summary>
    public bool KeepEachConnectionAlive { get; set; } = true;

    /// <summary>
    ///     This property determines the duration for which messages are stored when a client is disconnected.
    ///     The default timeout is 1.1 minute.
    /// </summary>
    public TimeSpan KeepMessageInterval { get; set; } = TimeSpan.FromMinutes(1.1);

    /// <summary>
    ///     Number of partitions to use for connection distribution.
    ///     Set to 1 to disable partitioning.
    ///     Increase this value for better scalability with millions of connections.
    ///     The default value is 4.
    /// </summary>
    public uint ConnectionPartitionCount { get; set; } = 4;

    /// <summary>
    ///     Target number of concurrent connections per partition.
    ///     Used as a hint when determining how many partitions to allocate dynamically.
    ///     Lower values result in more partitions.
    /// </summary>
    public int ConnectionsPerPartitionHint { get; set; } = 10_000;

    /// <summary>
    ///     Number of partitions to use for group distribution.
    ///     Set to 1 to disable partitioning.
    ///     Increase this value for better scalability with millions of groups.
    ///     The default value is 4.
    /// </summary>
    public uint GroupPartitionCount { get; set; } = 4;

    /// <summary>
    ///     Target number of groups per partition.
    ///     Used as a hint when determining how many partitions to allocate dynamically.
    /// </summary>
    public int GroupsPerPartitionHint { get; set; } = 1_000;

    /// <summary>
    ///     Maximum number of messages to queue per user when they are disconnected.
    ///     Oldest messages are dropped when the limit is exceeded.
    ///     The default value is 100.
    /// </summary>
    public int MaxQueuedMessagesPerUser { get; set; } = 100;

    /// <summary>
    ///     Number of consecutive failures before the circuit breaker opens.
    ///     When the circuit breaker is disabled or the grace period is zero, the observer is removed.
    ///     Set to 0 to disable failure tracking.
    ///     The default value is 3.
    /// </summary>
    public int ObserverFailureThreshold { get; set; } = 3;

    /// <summary>
    ///     Time window for counting observer failures. Failures older than this are forgotten.
    ///     The default value is 30 seconds.
    /// </summary>
    public TimeSpan ObserverFailureWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Enables circuit breaker pattern for observers to prevent cascade failures.
    ///     When enabled, failing observers are temporarily blocked from receiving messages.
    ///     The default value is true.
    /// </summary>
    public bool EnableCircuitBreaker { get; set; } = true;

    /// <summary>
    ///     Duration to keep the circuit open (blocking requests) after failure threshold is reached.
    ///     After this duration, the circuit transitions to half-open state for testing.
    ///     The default value is 30 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Interval between test requests when circuit is in half-open state.
    ///     The default value is 5 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerHalfOpenTestInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Grace period before an observer is hard-removed after a failure.
    ///     During this period, messages are buffered and replayed if the observer recovers.
    ///     This handles timing edge cases like GC pauses, network latency, or silo overload.
    ///     Set to TimeSpan.Zero to disable grace period buffering.
    ///     The default value is 10 seconds.
    /// </summary>
    public TimeSpan ObserverGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Maximum number of messages to buffer per observer during the grace period.
    ///     Oldest messages are dropped when the limit is exceeded.
    ///     The default value is 50.
    /// </summary>
    public int MaxBufferedMessagesPerObserver { get; set; } = 50;

}
