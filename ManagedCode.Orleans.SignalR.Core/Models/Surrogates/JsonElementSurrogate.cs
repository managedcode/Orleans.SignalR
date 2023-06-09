using System.Text.Json;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct JsonElementSurrogate
{
    [Id(0)] public readonly byte[] Data;

    public JsonElementSurrogate(JsonElement element)
    {
        Data = JsonSerializer.SerializeToUtf8Bytes(element);
    }
}