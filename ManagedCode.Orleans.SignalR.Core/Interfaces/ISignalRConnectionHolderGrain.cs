using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRObserver : IGrainObserver
{
    //[OneWay]
    Task OnNextAsync(HubMessage message);
}

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

public interface IObserverConnectionManager
{
    [OneWay]
    Task AddConnection(string connectionId, ISignalRObserver observer);
    
    [OneWay]
    Task RemoveConnection(string connectionId, ISignalRObserver observer);
    
    [OneWay]
    ValueTask Ping(ISignalRObserver observer);
}
