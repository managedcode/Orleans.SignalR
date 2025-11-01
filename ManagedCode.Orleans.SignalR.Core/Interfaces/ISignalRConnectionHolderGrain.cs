using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHolderGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    Task SendToAll(HubMessage message);

    [OneWay]
    Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds);

    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [OneWay]
    Task SendToConnections(HubMessage message, string[] connectionIds);
}
