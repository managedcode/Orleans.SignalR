using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct CompletionMessageSurrogate
{
    [Id(0)] public readonly string? InvocationId;

    [Id(1)] public readonly string? Error;

    [Id(2)] public readonly object? Result;

    [Id(3)] public readonly bool HasResult;

    public CompletionMessageSurrogate(string invocationId, string? error, object? result, bool hasResult)
    {
        InvocationId = invocationId;
        Error = error;
        Result = result;
        HasResult = hasResult;
    }
}