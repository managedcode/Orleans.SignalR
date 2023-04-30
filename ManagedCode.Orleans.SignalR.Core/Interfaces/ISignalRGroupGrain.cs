using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupGrain<THub> : IGrainWithStringKey
{
    [OneWay]
    Task SendToGroup(InvocationMessage message);
    
    [OneWay]
    Task SendToGroupExcept(InvocationMessage message, string[] excludedConnectionIds);

    [OneWay]
    Task AddConnection(string connectionId);
    
    [OneWay]
    Task RemoveConnection(string connectionId);
}