using ManagedCode.Orleans.SignalR.Core.Models.Surrogates;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Converters;

[RegisterConverter]
public sealed class InvocationMessageSurrogateConverter : IConverter<InvocationMessage, InvocationMessageSurrogate>
{
    public InvocationMessage ConvertFromSurrogate(in InvocationMessageSurrogate surrogate)
    {
        return new InvocationMessage(surrogate.InvocationId, surrogate.Target, surrogate.Arguments, surrogate.StreamIds)
        {
            Headers = surrogate.Headers
        };
    }

    public InvocationMessageSurrogate ConvertToSurrogate(in InvocationMessage value)
    {
        return new InvocationMessageSurrogate(value.InvocationId, value.Target, value.Arguments, value.StreamIds,
            value.Headers);
    }
}
