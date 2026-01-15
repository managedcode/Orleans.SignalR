using System;
using System.Collections.Immutable;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed record ConnectionHeartbeatRegistration(
    [property: Id(0)] string HubKey,
    [property: Id(1)] bool UsePartitioning,
    [property: Id(2)] int PartitionId,
    [property: Id(3)] ISignalRObserver Observer,
    [property: Id(4)] TimeSpan Interval,
    [property: Id(5)] ImmutableArray<GrainId> GrainIds,
    [property: Id(6)] string ConnectionId);
