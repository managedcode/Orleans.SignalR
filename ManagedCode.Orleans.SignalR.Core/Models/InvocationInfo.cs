using System;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
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
    public string ConnectionId { get; private set; }

    [Id(1)]
    public string InvocationId { get; private set; }

    [Id(2)]
    public string Type { get; private set; }

    public Type GetResultType()
    {
        return string.IsNullOrEmpty(Type) ? typeof(object) : System.Type.GetType(Type)!;
    }

    private void SetResultType(Type type)
    {
        Type = type.FullName!;
    }
}