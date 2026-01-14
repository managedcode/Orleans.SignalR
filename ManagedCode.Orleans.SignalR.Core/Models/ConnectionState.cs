using Orleans;
using System.Collections.Generic;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class ConnectionState
{
    [Id(0)]
    public Dictionary<string, string> ConnectionIds { get; set; } = new();
}
