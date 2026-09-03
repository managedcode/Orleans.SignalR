using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public sealed record InvocationProgress([property: Id(0)] string InvocationId);
