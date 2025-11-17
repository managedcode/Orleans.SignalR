using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionPartitionGrain : IGrainWithIntegerKey, IObserverConnectionManager
{
    [OneWay]
    [AlwaysInterleave]
    Task SendToPartition(HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task SendToPartitionExcept(HubMessage message, string[] excludedConnectionIds);

    [OneWay]
    [AlwaysInterleave]
    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [OneWay]
    [AlwaysInterleave]
    Task SendToConnections(HubMessage message, string[] connectionIds);
}
