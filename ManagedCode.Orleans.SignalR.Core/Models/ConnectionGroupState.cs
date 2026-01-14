using Orleans;
using System.Collections.Generic;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public class ConnectionGroupState
{
    [Id(0)]
    public Dictionary<string, ConnectionState> Groups { get; set; } = new();
}
