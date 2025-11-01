using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct CompletionMessageSurrogate(string invocationId, string? error, object? result, bool hasResult)
{
    [Id(0)] public readonly string? InvocationId = invocationId;

    [Id(1)] public readonly string? Error = error;

    [Id(2)] public readonly object? Result = result;

    [Id(3)] public readonly bool HasResult = hasResult;
}
