using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionCoordinatorGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<int> GetPartitionCount();

    [ReadOnly]
    Task<int> GetPartitionForConnection(string connectionId);

    [OneWay]
    Task SendToAll(HubMessage message);

    [OneWay]
    Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds);

    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [OneWay]
    Task SendToConnections(HubMessage message, string[] connectionIds);

    [OneWay]
    Task NotifyConnectionRemoved(string connectionId);
}
