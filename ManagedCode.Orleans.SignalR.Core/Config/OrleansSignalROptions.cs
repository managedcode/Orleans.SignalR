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
    ///     If true each connection should be kept alive by sending a message to the orleans every
    ///     <see cref="HubOptions.KeepAliveInterval" />.
    ///     The default value is true.
    ///     Set to false only if you don't want to send messages to the specific connectionId.
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
    ///     Number of consecutive failures before an observer is considered dead and removed.
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

    /// <summary>
    ///     Maximum number of connections allowed per partition grain.
    ///     New connections are rejected when the limit is exceeded.
    ///     Set to 0 to disable connection limits (not recommended for production).
    ///     The default value is 100,000.
    /// </summary>
    public int MaxConnectionsPerPartition { get; set; } = 100_000;

    /// <summary>
    ///     Maximum number of groups per partition grain.
    ///     New groups are rejected when the limit is exceeded.
    ///     Set to 0 to disable group limits.
    ///     The default value is 50,000.
    /// </summary>
    public int MaxGroupsPerPartition { get; set; } = 50_000;

    /// <summary>
    ///     Timeout for slow client message delivery.
    ///     Connections that cannot receive messages within this time may be terminated.
    ///     The default value is 10 seconds.
    /// </summary>
    public TimeSpan SlowClientTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Enables backpressure handling for slow clients.
    ///     When enabled, messages to slow clients are dropped or the connection is terminated.
    ///     The default value is true.
    /// </summary>
    public bool EnableSlowClientHandling { get; set; } = true;

    /// <summary>
    ///     Maximum number of pending messages allowed per connection before backpressure is applied.
    ///     The default value is 1000.
    /// </summary>
    public int MaxPendingMessagesPerConnection { get; set; } = 1000;

    /// <summary>
    ///     Enables metrics collection for monitoring and diagnostics.
    ///     The default value is true.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
}
