using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnection : IGrainObserver
{
    [OneWay]
    Task SendMessage(InvocationMessage message);
}

public interface ISignalRConnectionHolderGrain : IGrainWithStringKey
{
    [OneWay]
    Task AddConnection(string connectionId, ISignalRConnection connection);
    
    [OneWay]
    Task RemoveConnection(string connectionId, ISignalRConnection connection);

    [OneWay]
    Task SendToAll(InvocationMessage message);
    
    [OneWay]
    Task SendToAllExcept(InvocationMessage message, string[] excludedConnectionIds);

    Task<bool> SendToConnection(InvocationMessage message, string connectionId);
    
    [OneWay]
    Task SendToConnections(InvocationMessage message, string[] connectionIds);
}
