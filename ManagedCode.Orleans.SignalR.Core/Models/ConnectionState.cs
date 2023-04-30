using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public class ConnectionState
{
    [Id(0)]
    public HashSet<string> ConnectionIds { get; set; } = new();
}