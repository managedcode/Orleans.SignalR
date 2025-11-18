using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionPartitionGrain : IGrainWithIntegerKey, IObserverConnectionManager
{
    [AlwaysInterleave]
    [OneWay]
    Task SendToPartition(HubMessage message);

    [AlwaysInterleave]
    [OneWay]
    Task SendToPartitionExcept(HubMessage message, string[] excludedConnectionIds);

    [AlwaysInterleave]
    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [AlwaysInterleave]
    [OneWay]
    Task SendToConnections(HubMessage message, string[] connectionIds);
}
