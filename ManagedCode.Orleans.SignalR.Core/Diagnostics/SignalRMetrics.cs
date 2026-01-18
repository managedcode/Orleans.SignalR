using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ManagedCode.Orleans.SignalR.Core.Diagnostics;

/// <summary>
/// Provides metrics for monitoring Orleans SignalR backplane behavior using System.Diagnostics.Metrics.
/// </summary>
public sealed class SignalRMetrics
{
    /// <summary>
    /// The meter name used for all Orleans SignalR metrics.
    /// </summary>
    public const string MeterName = "ManagedCode.Orleans.SignalR";
    public const string ConnectionsTotalName = "signalr.connections.total";
    public const string DisconnectionsTotalName = "signalr.disconnections.total";
    public const string ActiveConnectionsName = "signalr.connections.active";
    public const string MessagesSentTotalName = "signalr.messages.sent.total";
    public const string MessagesDroppedTotalName = "signalr.messages.dropped.total";
    public const string MessagesBufferedTotalName = "signalr.messages.buffered.total";
    public const string ObserverFailuresTotalName = "signalr.observer.failures.total";
    public const string ObserversMarkedDeadTotalName = "signalr.observer.dead.total";
    public const string CircuitBreakersOpenedTotalName = "signalr.circuit_breaker.opened.total";
    public const string CircuitBreakersClosedTotalName = "signalr.circuit_breaker.closed.total";
    public const string ObserversInGracePeriodName = "signalr.observer.grace_period";

    public const string TagHub = "hub";
    public const string TagTarget = "target";
    public const string TagReason = "reason";
    public const string TagFailureType = "failure_type";

    public static class TargetTypes
    {
        public const string All = "all";
        public const string AllExcept = "all_except";
        public const string Connection = "connection";
        public const string Connections = "connections";
        public const string Group = "group";
        public const string Groups = "groups";
        public const string GroupExcept = "group_except";
        public const string User = "user";
        public const string Users = "users";
    }

    public static class DropReasons
    {
        public const string BufferFull = "buffer_full";
        public const string CircuitOpen = "circuit_open";
        public const string OfflineQueueLimit = "offline_queue_limit";
    }

    private readonly Meter _meter;
    private readonly Counter<long> _connectionsTotal;
    private readonly Counter<long> _disconnectionsTotal;
    private readonly UpDownCounter<long> _activeConnections;
    private readonly Counter<long> _messagesSentTotal;
    private readonly Counter<long> _messagesDroppedTotal;
    private readonly Counter<long> _messagesBufferedTotal;
    private readonly Counter<long> _observerFailuresTotal;
    private readonly Counter<long> _observersMarkedDeadTotal;
    private readonly Counter<long> _circuitBreakersOpenedTotal;
    private readonly Counter<long> _circuitBreakersClosedTotal;
    private readonly UpDownCounter<long> _observersInGracePeriod;
    /// <summary>
    /// Gets the singleton instance of SignalRMetrics.
    /// </summary>
    public static SignalRMetrics Instance { get; } = new();

    private SignalRMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _connectionsTotal = _meter.CreateCounter<long>(
            ConnectionsTotalName,
            unit: "{connection}",
            description: "Total number of SignalR connections established");

        _disconnectionsTotal = _meter.CreateCounter<long>(
            DisconnectionsTotalName,
            unit: "{connection}",
            description: "Total number of SignalR connections closed");

        _activeConnections = _meter.CreateUpDownCounter<long>(
            ActiveConnectionsName,
            unit: "{connection}",
            description: "Number of currently active SignalR connections");

        _messagesSentTotal = _meter.CreateCounter<long>(
            MessagesSentTotalName,
            unit: "{message}",
            description: "Total number of messages sent to clients");

        _messagesDroppedTotal = _meter.CreateCounter<long>(
            MessagesDroppedTotalName,
            unit: "{message}",
            description: "Total number of messages dropped due to errors or backpressure");

        _messagesBufferedTotal = _meter.CreateCounter<long>(
            MessagesBufferedTotalName,
            unit: "{message}",
            description: "Total number of messages buffered during grace periods or offline queues");

        _observerFailuresTotal = _meter.CreateCounter<long>(
            ObserverFailuresTotalName,
            unit: "{failure}",
            description: "Total number of observer delivery failures");

        _observersMarkedDeadTotal = _meter.CreateCounter<long>(
            ObserversMarkedDeadTotalName,
            unit: "{observer}",
            description: "Total number of observers marked as dead");

        _circuitBreakersOpenedTotal = _meter.CreateCounter<long>(
            CircuitBreakersOpenedTotalName,
            unit: "{circuit}",
            description: "Total number of times circuit breakers were opened");

        _circuitBreakersClosedTotal = _meter.CreateCounter<long>(
            CircuitBreakersClosedTotalName,
            unit: "{circuit}",
            description: "Total number of times circuit breakers were closed");

        _observersInGracePeriod = _meter.CreateUpDownCounter<long>(
            ObserversInGracePeriodName,
            unit: "{observer}",
            description: "Number of observers currently in grace period");
    }

    public void RecordConnectionEstablished(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _connectionsTotal.Add(1);
            _activeConnections.Add(1);
            return;
        }

        _connectionsTotal.Add(1, tags);
        _activeConnections.Add(1, tags);
    }

    public void RecordConnectionClosed(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _disconnectionsTotal.Add(1);
            _activeConnections.Add(-1);
            return;
        }

        _disconnectionsTotal.Add(1, tags);
        _activeConnections.Add(-1, tags);
    }

    public void RecordMessageSent(string? hubName, string targetType, int recipientCount = 1)
    {
        if (recipientCount <= 0)
        {
            return;
        }

        var tags = CreateHubTargetTags(hubName, targetType);
        if (tags.Count == 0)
        {
            _messagesSentTotal.Add(recipientCount);
            return;
        }

        _messagesSentTotal.Add(recipientCount, tags);
    }

    public void RecordMessageDropped(string? hubName, string reason, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        var tags = CreateHubReasonTags(hubName, reason);
        if (tags.Count == 0)
        {
            _messagesDroppedTotal.Add(count);
            return;
        }

        _messagesDroppedTotal.Add(count, tags);
    }

    public void RecordMessageBuffered(string? hubName, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _messagesBufferedTotal.Add(count);
            return;
        }

        _messagesBufferedTotal.Add(count, tags);
    }

    public void RecordObserverFailure(string? hubName, string failureType)
    {
        var tags = CreateHubFailureTags(hubName, failureType);
        if (tags.Count == 0)
        {
            _observerFailuresTotal.Add(1);
            return;
        }

        _observerFailuresTotal.Add(1, tags);
    }

    public void RecordObserverDead(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _observersMarkedDeadTotal.Add(1);
            return;
        }

        _observersMarkedDeadTotal.Add(1, tags);
    }

    public void RecordCircuitBreakerOpened(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _circuitBreakersOpenedTotal.Add(1);
            return;
        }

        _circuitBreakersOpenedTotal.Add(1, tags);
    }

    public void RecordCircuitBreakerClosed(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _circuitBreakersClosedTotal.Add(1);
            return;
        }

        _circuitBreakersClosedTotal.Add(1, tags);
    }

    public void RecordGracePeriodStarted(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _observersInGracePeriod.Add(1);
            return;
        }

        _observersInGracePeriod.Add(1, tags);
    }

    public void RecordGracePeriodEnded(string? hubName)
    {
        var tags = CreateHubTags(hubName);
        if (tags.Count == 0)
        {
            _observersInGracePeriod.Add(-1);
            return;
        }

        _observersInGracePeriod.Add(-1, tags);
    }

    private static TagList CreateHubTags(string? hubName)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(hubName))
        {
            tags.Add(TagHub, hubName);
        }

        return tags;
    }

    private static TagList CreateHubTargetTags(string? hubName, string targetType)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(hubName))
        {
            tags.Add(TagHub, hubName);
        }

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            tags.Add(TagTarget, targetType);
        }

        return tags;
    }

    private static TagList CreateHubReasonTags(string? hubName, string reason)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(hubName))
        {
            tags.Add(TagHub, hubName);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            tags.Add(TagReason, reason);
        }

        return tags;
    }

    private static TagList CreateHubFailureTags(string? hubName, string failureType)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(hubName))
        {
            tags.Add(TagHub, hubName);
        }

        if (!string.IsNullOrWhiteSpace(failureType))
        {
            tags.Add(TagFailureType, failureType);
        }

        return tags;
    }
}
