using System;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public sealed record QueuedHubMessage(
    [property: Id(0)] Guid DeliveryId,
    [property: Id(1)] HubMessage Message,
    [property: Id(2)] DateTime ExpiresAtUtc);
