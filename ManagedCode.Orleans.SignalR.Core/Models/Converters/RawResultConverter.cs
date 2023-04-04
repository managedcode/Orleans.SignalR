using System.Buffers;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

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