using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
[Immutable]
public readonly struct CompletionMessageSurrogate
{
    [Id(0)] public readonly string? InvocationId;

    [Id(1)]
    public string? Error { get; }

    [Id(2)]
    public object? Result { get; }

    [Id(3)]
    public bool HasResult { get; }

    public CompletionMessageSurrogate(string invocationId, string? error, object? result, bool hasResult)
    {
        InvocationId = invocationId;
        Error = error;
        Result = result;
        HasResult = hasResult;
    }
}