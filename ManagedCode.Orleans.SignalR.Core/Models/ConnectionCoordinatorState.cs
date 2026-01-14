using Orleans;
using System;
using System.Collections.Generic;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed class ConnectionCoordinatorState
{
    [Id(0)]
    public Dictionary<string, PartitionAssignment> ConnectionPartitions { get; set; } = new(StringComparer.Ordinal);

    [Id(1)]
    public int CurrentPartitionCount { get; set; }

    /// <summary>
    /// Epoch increments each time partition count changes, enabling detection of stale assignments.
    /// </summary>
    [Id(2)]
    public int PartitionEpoch { get; set; } = 1;
}
