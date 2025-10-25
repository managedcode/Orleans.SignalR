using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public class GroupPartitionState
{
    [Id(0)]
    public Dictionary<string, Dictionary<string, string>> Groups { get; set; } = new();

    [Id(1)]
    public Dictionary<string, HashSet<string>> ConnectionGroups { get; set; } = new();

    [Id(2)]
    public Dictionary<string, string> ConnectionObservers { get; set; } = new();

    public bool IsEmpty =>
        Groups.Count == 0 &&
        ConnectionGroups.Count == 0 &&
        ConnectionObservers.Count == 0;
}
