using System;
using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed class GroupCoordinatorState
{
    [Id(0)]
    public Dictionary<string, int> GroupPartitions { get; set; } = new(StringComparer.Ordinal);

    [Id(1)]
    public Dictionary<string, int> GroupMembership { get; set; } = new(StringComparer.Ordinal);

    [Id(2)]
    public int CurrentPartitionCount { get; set; }
}
