using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHolderGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    [AlwaysInterleave]
    Task SendToAll(HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task SendToAllExcept(HubMessage message, string[] excludedConnectionIds);

    [AlwaysInterleave]
    Task<bool> SendToConnection(HubMessage message, string connectionId);

    [OneWay]
    [AlwaysInterleave]
    Task SendToConnections(HubMessage message, string[] connectionIds);
}
