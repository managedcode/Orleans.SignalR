using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    Task SendToGroup(HubMessage message);
    
    [OneWay]
    Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds);
}