using System;
using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed class ConnectionCoordinatorState
{
    [Id(0)]
    public Dictionary<string, int> ConnectionPartitions { get; set; } = new(StringComparer.Ordinal);

    [Id(1)]
    public int CurrentPartitionCount { get; set; }
}
