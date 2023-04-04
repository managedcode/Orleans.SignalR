using System;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class ReturnType
{
    [Id(0)]
    public bool Result { get; set; }
    [Id(1)]
    public string? Type { get; set; }
    
    public Type GetReturnType()
    {
        return string.IsNullOrEmpty(Type) ? typeof(object) : System.Type.GetType(Type);
    }
}

[GenerateSerializer]
public class InvocationInfo
{
    
    public InvocationInfo(string invocationId, string connectionId, Type type)
    {
        InvocationId = invocationId;
        ConnectionId = connectionId;
        SetResultType(type);
    }

    [Id(0)]
    public string InvocationId { get; private set; }
    
    [Id(1)]
    public string ConnectionId { get; private set; }
    
    [Id(2)]
    public string Type { get; private set; }
    
    public Type GetResultType()
    {
        return string.IsNullOrEmpty(Type) ? typeof(object) : System.Type.GetType(Type);
    }
    
    public void SetResultType(Type type)
    {
        Type = type.FullName;
    }
}
