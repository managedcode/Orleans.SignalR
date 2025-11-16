using ManagedCode.Orleans.SignalR.Core.Models.Surrogates;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Converters;

[RegisterConverter]
public sealed class CompletionMessageConverter : IConverter<CompletionMessage, CompletionMessageSurrogate>
{
    public CompletionMessage ConvertFromSurrogate(in CompletionMessageSurrogate surrogate)
    {
        var invocationId = surrogate.InvocationId ?? string.Empty;
        return new CompletionMessage(invocationId, surrogate.Error, surrogate.Result, surrogate.HasResult);
    }

    public CompletionMessageSurrogate ConvertToSurrogate(in CompletionMessage value)
    {
        var invocationId = value.InvocationId ?? string.Empty;
        return new CompletionMessageSurrogate(invocationId, value.Error, value.Result, value.HasResult);
    }
}
