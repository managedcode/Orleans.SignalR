using System.Text.Json;
using ManagedCode.Orleans.SignalR.Core.Models.Surrogates;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Converters;

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
