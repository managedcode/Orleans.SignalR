using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class ConnectionGroupState
{
    [Id(0)]
    public Dictionary<string, ConnectionState> Groups { get; set; } = new();
}