using ManagedCode.Orleans.SignalR.Core.Models.Surrogates;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using System.Buffers;

namespace ManagedCode.Orleans.SignalR.Core.Models.Converters;

[RegisterConverter]
public sealed class RawResultConverter : IConverter<RawResult, RawResultSurrogate>
{
    public RawResult ConvertFromSurrogate(in RawResultSurrogate surrogate)
    {
        return new RawResult(new ReadOnlySequence<byte>(surrogate.RawSerializedData));
    }

    public RawResultSurrogate ConvertToSurrogate(in RawResult value)
    {
        return new RawResultSurrogate(value.RawSerializedData.ToArray());
    }
}
