using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
[Immutable]
public readonly struct InvocationMessageSurrogate
{
    [Id(0)] public readonly string? InvocationId;

    [Id(1)] public readonly string Target;

    [Id(2)] public readonly object?[] Arguments;

    [Id(3)] public readonly string[]? StreamIds;

    [Id(4)] public readonly IDictionary<string, string>? Headers;

    public InvocationMessageSurrogate(string? invocationId, string target, object?[] arguments, string[]? streamIds,
        IDictionary<string, string>? headers)
    {
        InvocationId = invocationId;
        Target = target;
        Arguments = arguments;
        StreamIds = streamIds;
        Headers = headers;
    }
}