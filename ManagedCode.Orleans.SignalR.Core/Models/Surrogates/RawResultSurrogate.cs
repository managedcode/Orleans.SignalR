using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct RawResultSurrogate(byte[] rawSerializedData)
{
    [Id(0)] public readonly byte[] RawSerializedData = rawSerializedData;
}
