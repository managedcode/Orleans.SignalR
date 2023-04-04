using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHolderGrain<THub> : IGrainWithStringKey
{
    Task AddConnection(string connectionId);
    Task RemoveConnection(string connectionId);

    Task SendToAll(InvocationMessage message);
    Task SendToAllExcept(InvocationMessage message, string[] excludedConnectionIds);

    Task SendToConnection(InvocationMessage message, string connectionId);
    Task SendToConnections(InvocationMessage message, string[] connectionIds);
}

public interface ISignalRInvocationGrain<THub> : IGrainWithStringKey
{
    Task<bool> InvokeConnectionAsync(string connectionId, InvocationMessage message);
    Task TryCompleteResult(string connectionId, CompletionMessage message);
    Task<ReturnType> TryGetReturnType(string connectionId);
    ValueTask AddInvocation(InvocationInfo invocationInfo);
    ValueTask<InvocationInfo?> RemoveInvocation();
}