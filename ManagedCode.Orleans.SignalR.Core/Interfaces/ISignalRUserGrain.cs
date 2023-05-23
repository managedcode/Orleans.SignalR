using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRUserGrain : IGrainWithStringKey
{
    [OneWay]
    Task AddConnection(string connectionId);
    
    [OneWay]
    Task RemoveConnection(string connectionId);

    [OneWay]
    Task SendToUser(InvocationMessage message);
}