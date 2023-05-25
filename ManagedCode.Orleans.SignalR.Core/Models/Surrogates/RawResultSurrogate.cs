using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models.Surrogates;

[Immutable]
[GenerateSerializer]
public readonly struct RawResultSurrogate
{
    [Id(0)] public readonly byte[] RawSerializedData;

    public RawResultSurrogate(byte[] rawSerializedData)
    {
        RawSerializedData = rawSerializedData;
    }
}