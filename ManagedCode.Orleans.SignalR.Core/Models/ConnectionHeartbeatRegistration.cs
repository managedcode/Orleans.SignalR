using System;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed record ConnectionHeartbeatRegistration(
    [property: Id(0)] string HubKey,
    [property: Id(1)] bool UsePartitioning,
    [property: Id(2)] int PartitionId,
    [property: Id(3)] ISignalRObserver Observer,
    [property: Id(4)] TimeSpan Interval);
