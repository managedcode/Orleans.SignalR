using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionPartitionGrain : IGrainWithIntegerKey, IObserverConnectionManager
{
    [OneWay]
    Task SendToPartition(HubMessage message);

    [OneWay]
    Task SendToPartitionExcept(HubMessage message, string[] excludedConnectionIds);

    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [OneWay]
    Task SendToConnections(HubMessage message, string[] connectionIds);
}
