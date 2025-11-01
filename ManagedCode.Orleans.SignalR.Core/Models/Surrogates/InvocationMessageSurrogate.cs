using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct InvocationMessageSurrogate(string? invocationId, string target, object?[] arguments, string[]? streamIds,
    IDictionary<string, string>? headers)
{
    [Id(0)] public readonly string? InvocationId = invocationId;

    [Id(1)] public readonly string Target = target;

    [Id(2)] public readonly object?[] Arguments = arguments;

    [Id(3)] public readonly string[]? StreamIds = streamIds;

    [Id(4)] public readonly IDictionary<string, string>? Headers = headers;
}
