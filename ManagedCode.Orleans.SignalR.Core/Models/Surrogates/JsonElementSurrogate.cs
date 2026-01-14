using Orleans;
using System.Text.Json;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct JsonElementSurrogate(JsonElement element)
{
    [Id(0)] public readonly byte[] Data = JsonSerializer.SerializeToUtf8Bytes(element);
}
