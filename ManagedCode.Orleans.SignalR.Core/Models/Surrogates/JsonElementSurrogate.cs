using System.Text.Json;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct JsonElementSurrogate(JsonElement element)
{
    [Id(0)] public readonly byte[] Data = JsonSerializer.SerializeToUtf8Bytes(element);
}
