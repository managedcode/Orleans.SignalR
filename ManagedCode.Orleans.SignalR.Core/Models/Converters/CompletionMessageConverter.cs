using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[RegisterConverter]
public sealed class CompletionMessageConverter : IConverter<CompletionMessage, CompletionMessageSurrogate>
{
    public CompletionMessage ConvertFromSurrogate(in CompletionMessageSurrogate surrogate)
    {
        return new CompletionMessage(
            surrogate.InvocationId,
            surrogate.Error,
            surrogate.Result,
            surrogate.HasResult);
    }

    public CompletionMessageSurrogate ConvertToSurrogate(in CompletionMessage value)
    {
        return new CompletionMessageSurrogate(
            value.InvocationId,
            value.Error,
            value.Result,
            value.HasResult);
    }
}