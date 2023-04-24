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