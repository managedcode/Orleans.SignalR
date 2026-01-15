using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class ConnectionState
{
    [Id(0)]
    public Dictionary<string, string> ConnectionIds { get; set; } = new();
}
