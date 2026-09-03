using System;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class InvocationInfo
{
    public InvocationInfo()
    {
        // we need it for TryGetReturnType because of parameterless constructor
    }

    public InvocationInfo(string connectionId, string invocationId, Type type)
    {
        ConnectionId = connectionId;
        InvocationId = invocationId;
        SetResultType(type);
    }

    [Id(0)]
    public string ConnectionId { get; set; } = string.Empty;

    [Id(1)]
    public string InvocationId { get; set; } = string.Empty;

    [Id(2)]
    public string Type { get; set; } = string.Empty;

    [Id(3)]
    public CompletionMessage? Completion { get; set; }

    public Type GetResultType() => string.IsNullOrEmpty(Type) ? typeof(object) : System.Type.GetType(Type)!;

    public bool Register(InvocationInfo invocationInfo)
    {
        ArgumentNullException.ThrowIfNull(invocationInfo);
        ConnectionId = invocationInfo.ConnectionId;
        InvocationId = invocationInfo.InvocationId;
        Type = invocationInfo.Type;
        Completion = null;
        return true;
    }

    public bool TryComplete(string connectionId, CompletionMessage completion)
    {
        if (!string.Equals(ConnectionId, connectionId, StringComparison.Ordinal) || Completion is not null)
        {
            return false;
        }

        Completion = completion;
        return true;
    }

    private void SetResultType(Type type) => Type = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
}
