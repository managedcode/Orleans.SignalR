using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;
using System.Threading.Tasks;

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
