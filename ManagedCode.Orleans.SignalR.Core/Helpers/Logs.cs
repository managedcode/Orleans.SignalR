using Microsoft.Extensions.Logging;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

public static partial class Logs
{
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{grainType}:{grainId} OnDeactivateAsync")]
    public static partial void OnDeactivateAsync(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{grainType}:{grainId} Ping")]
    public static partial void Ping(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} AddConnection `{connectionId}`")]
    public static partial void AddConnection(ILogger logger, string grainType, string grainId, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} RemoveConnection `{connectionId}`")]
    public static partial void RemoveConnection(ILogger logger, string grainType, string grainId, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToAll")]
    public static partial void SendToAll(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToAllExcept {expectedConnectionIds}")]
    public static partial void SendToAllExcept(ILogger logger, string grainType, string grainId, string[] expectedConnectionIds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToConnection `{connectionId}`")]
    public static partial void SendToConnection(ILogger logger, string grainType, string grainId, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToConnections {expectedConnectionIds}")]
    public static partial void SendToConnections(ILogger logger, string grainType, string grainId, string[] expectedConnectionIds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToGroup")]
    public static partial void SendToGroup(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToGroupExcept {expectedConnectionIds}")]
    public static partial void SendToGroupExcept(ILogger logger, string grainType, string grainId, string[] expectedConnectionIds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} TryCompleteResult `{connectionId}`")]
    public static partial void TryCompleteResult(ILogger logger, string grainType, string grainId, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} TryGetReturnType")]
    public static partial void TryGetReturnType(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} AddInvocation `{invocationId}` `{connectionId}`")]
    public static partial void AddInvocation(ILogger logger, string grainType, string grainId, string invocationId, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{grainType}:{grainId} RemoveInvocation")]
    public static partial void RemoveInvocation(ILogger logger, string grainType, string grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{grainType}:{grainId} SendToUser")]
    public static partial void SendToUser(ILogger logger, string grainType, string grainId);

}
