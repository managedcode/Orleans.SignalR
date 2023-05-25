using System.Buffers;
using System.Text.Json;
using ManagedCode.Orleans.SignalR.Core.Models.Surrogates;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

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

[RegisterConverter]
public sealed class JsonElementConverter : IConverter<JsonElement, JsonElementSurrogate>
{
    public JsonElement ConvertFromSurrogate(in JsonElementSurrogate surrogate)
    {
        return JsonSerializer.Deserialize<JsonElement>(surrogate.Data);
    }

    public JsonElementSurrogate ConvertToSurrogate(in JsonElement value)
    {
        return new JsonElementSurrogate(value);
    }
}

