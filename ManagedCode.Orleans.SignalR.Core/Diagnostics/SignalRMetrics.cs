using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace ManagedCode.Orleans.SignalR.Core.Diagnostics;

/// <summary>
/// Provides metrics for monitoring Orleans SignalR backplane performance.
/// Uses System.Diagnostics.Metrics for .NET 10 compatibility with OpenTelemetry.
/// </summary>
public sealed class SignalRMetrics : IDisposable
{
    /// <summary>
    /// The meter name used for all Orleans SignalR metrics.
    /// </summary>
    public const string MeterName = "ManagedCode.Orleans.SignalR";

    private readonly Meter _meter;

    // Connection metrics
    private readonly Counter<long> _connectionsTotal;
    private readonly Counter<long> _disconnectionsTotal;
    private readonly UpDownCounter<long> _activeConnections;

    // Message metrics
    private readonly Counter<long> _messagesSentTotal;
    private readonly Counter<long> _messagesReceivedTotal;
    private readonly Counter<long> _messagesDroppedTotal;
    private readonly Counter<long> _messagesBufferedTotal;
    private readonly Histogram<double> _messageDeliveryDuration;

    // Observer health metrics
    private readonly Counter<long> _observerFailuresTotal;
    private readonly Counter<long> _observersMarkedDeadTotal;
    private readonly Counter<long> _circuitBreakersOpenedTotal;
    private readonly Counter<long> _circuitBreakersClosedTotal;
    private readonly UpDownCounter<long> _observersInGracePeriod;

    // Partition metrics
    private readonly ObservableGauge<int> _connectionPartitionCount;
    private readonly ObservableGauge<int> _groupPartitionCount;

    // Internal state for observable gauges
    private int _currentConnectionPartitionCount;
    private int _currentGroupPartitionCount;

    /// <summary>
    /// Gets the singleton instance of SignalRMetrics.
    /// </summary>
    public static SignalRMetrics Instance { get; } = new();

    private SignalRMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        // Connection metrics
        _connectionsTotal = _meter.CreateCounter<long>(
            "signalr.connections.total",
            unit: "{connection}",
            description: "Total number of SignalR connections established");

        _disconnectionsTotal = _meter.CreateCounter<long>(
            "signalr.disconnections.total",
            unit: "{connection}",
            description: "Total number of SignalR connections closed");

        _activeConnections = _meter.CreateUpDownCounter<long>(
            "signalr.connections.active",
            unit: "{connection}",
            description: "Number of currently active SignalR connections");

        // Message metrics
        _messagesSentTotal = _meter.CreateCounter<long>(
            "signalr.messages.sent.total",
            unit: "{message}",
            description: "Total number of messages sent to clients");

        _messagesReceivedTotal = _meter.CreateCounter<long>(
            "signalr.messages.received.total",
            unit: "{message}",
            description: "Total number of messages received from clients");

        _messagesDroppedTotal = _meter.CreateCounter<long>(
            "signalr.messages.dropped.total",
            unit: "{message}",
            description: "Total number of messages dropped due to errors or backpressure");

        _messagesBufferedTotal = _meter.CreateCounter<long>(
            "signalr.messages.buffered.total",
            unit: "{message}",
            description: "Total number of messages buffered during grace periods");

        _messageDeliveryDuration = _meter.CreateHistogram<double>(
            "signalr.message.delivery.duration",
            unit: "ms",
            description: "Time taken to deliver a message to clients");

        // Observer health metrics
        _observerFailuresTotal = _meter.CreateCounter<long>(
            "signalr.observer.failures.total",
            unit: "{failure}",
            description: "Total number of observer delivery failures");

        _observersMarkedDeadTotal = _meter.CreateCounter<long>(
            "signalr.observer.dead.total",
            unit: "{observer}",
            description: "Total number of observers marked as dead");

        _circuitBreakersOpenedTotal = _meter.CreateCounter<long>(
            "signalr.circuit_breaker.opened.total",
            unit: "{circuit}",
            description: "Total number of times circuit breakers were opened");

        _circuitBreakersClosedTotal = _meter.CreateCounter<long>(
            "signalr.circuit_breaker.closed.total",
            unit: "{circuit}",
            description: "Total number of times circuit breakers were closed");

        _observersInGracePeriod = _meter.CreateUpDownCounter<long>(
            "signalr.observer.grace_period",
            unit: "{observer}",
            description: "Number of observers currently in grace period");

        // Partition metrics
        _connectionPartitionCount = _meter.CreateObservableGauge(
            "signalr.partitions.connection.count",
            () => Volatile.Read(ref _currentConnectionPartitionCount),
            unit: "{partition}",
            description: "Current number of connection partitions");

        _groupPartitionCount = _meter.CreateObservableGauge(
            "signalr.partitions.group.count",
            () => Volatile.Read(ref _currentGroupPartitionCount),
            unit: "{partition}",
            description: "Current number of group partitions");
    }

    /// <summary>
    /// Records a new connection.
    /// </summary>
    public void RecordConnectionEstablished(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _connectionsTotal.Add(1, tags);
        _activeConnections.Add(1, tags);
    }

    /// <summary>
    /// Records a connection disconnection.
    /// </summary>
    public void RecordConnectionClosed(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _disconnectionsTotal.Add(1, tags);
        _activeConnections.Add(-1, tags);
    }

    /// <summary>
    /// Records a message sent to clients.
    /// </summary>
    public void RecordMessageSent(string hubName, string targetType, int recipientCount = 1)
    {
        var tags = new TagList
        {
            { "hub", hubName },
            { "target", targetType }
        };
        _messagesSentTotal.Add(recipientCount, tags);
    }

    /// <summary>
    /// Records a message received from a client.
    /// </summary>
    public void RecordMessageReceived(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _messagesReceivedTotal.Add(1, tags);
    }

    /// <summary>
    /// Records a dropped message.
    /// </summary>
    public void RecordMessageDropped(string hubName, string reason)
    {
        var tags = new TagList
        {
            { "hub", hubName },
            { "reason", reason }
        };
        _messagesDroppedTotal.Add(1, tags);
    }

    /// <summary>
    /// Records a buffered message during grace period.
    /// </summary>
    public void RecordMessageBuffered(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _messagesBufferedTotal.Add(1, tags);
    }

    /// <summary>
    /// Records the duration of message delivery.
    /// </summary>
    public void RecordMessageDeliveryDuration(string hubName, double durationMs)
    {
        var tags = new TagList { { "hub", hubName } };
        _messageDeliveryDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records an observer failure.
    /// </summary>
    public void RecordObserverFailure(string hubName, string failureType)
    {
        var tags = new TagList
        {
            { "hub", hubName },
            { "failure_type", failureType }
        };
        _observerFailuresTotal.Add(1, tags);
    }

    /// <summary>
    /// Records an observer marked as dead.
    /// </summary>
    public void RecordObserverDead(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _observersMarkedDeadTotal.Add(1, tags);
    }

    /// <summary>
    /// Records a circuit breaker opening.
    /// </summary>
    public void RecordCircuitBreakerOpened(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _circuitBreakersOpenedTotal.Add(1, tags);
    }

    /// <summary>
    /// Records a circuit breaker closing.
    /// </summary>
    public void RecordCircuitBreakerClosed(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _circuitBreakersClosedTotal.Add(1, tags);
    }

    /// <summary>
    /// Records an observer entering grace period.
    /// </summary>
    public void RecordGracePeriodStarted(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _observersInGracePeriod.Add(1, tags);
    }

    /// <summary>
    /// Records an observer exiting grace period.
    /// </summary>
    public void RecordGracePeriodEnded(string hubName)
    {
        var tags = new TagList { { "hub", hubName } };
        _observersInGracePeriod.Add(-1, tags);
    }

    /// <summary>
    /// Updates the current connection partition count.
    /// </summary>
    public void SetConnectionPartitionCount(int count)
    {
        Volatile.Write(ref _currentConnectionPartitionCount, count);
    }

    /// <summary>
    /// Updates the current group partition count.
    /// </summary>
    public void SetGroupPartitionCount(int count)
    {
        Volatile.Write(ref _currentGroupPartitionCount, count);
    }

    /// <summary>
    /// Creates a scope for measuring message delivery duration.
    /// </summary>
    public MessageDeliveryScope StartMessageDelivery(string hubName)
    {
        return new MessageDeliveryScope(this, hubName);
    }

    /// <summary>
    /// Disposes the metrics meter.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }

    /// <summary>
    /// Scope for measuring message delivery duration.
    /// </summary>
    public readonly struct MessageDeliveryScope : IDisposable
    {
        private readonly SignalRMetrics _metrics;
        private readonly string _hubName;
        private readonly long _startTimestamp;

        internal MessageDeliveryScope(SignalRMetrics metrics, string hubName)
        {
            _metrics = metrics;
            _hubName = hubName;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Completes the measurement and records the duration.
        /// </summary>
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            _metrics.RecordMessageDeliveryDuration(_hubName, elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>
/// Activity source for distributed tracing of SignalR operations.
/// </summary>
public static class SignalRActivitySource
{
    /// <summary>
    /// The activity source name.
    /// </summary>
    public const string SourceName = "ManagedCode.Orleans.SignalR";

    /// <summary>
    /// Gets the activity source for SignalR operations.
    /// </summary>
    public static ActivitySource Source { get; } = new(SourceName, "1.0.0");

    /// <summary>
    /// Starts an activity for sending a message.
    /// </summary>
    public static Activity? StartSendMessage(string hubName, string targetType)
    {
        var activity = Source.StartActivity("SignalR.SendMessage", ActivityKind.Producer);
        activity?.SetTag("signalr.hub", hubName);
        activity?.SetTag("signalr.target_type", targetType);
        return activity;
    }

    /// <summary>
    /// Starts an activity for a grain operation.
    /// </summary>
    public static Activity? StartGrainOperation(string grainType, string operation)
    {
        var activity = Source.StartActivity($"SignalR.{grainType}.{operation}", ActivityKind.Internal);
        activity?.SetTag("signalr.grain_type", grainType);
        activity?.SetTag("signalr.operation", operation);
        return activity;
    }
}
