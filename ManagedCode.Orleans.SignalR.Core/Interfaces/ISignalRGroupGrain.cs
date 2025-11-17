using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    [AlwaysInterleave]
    Task SendToGroup(HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds);
}
