using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [AlwaysInterleave]
    [OneWay]
    Task SendToGroup(HubMessage message);

    [AlwaysInterleave]
    [OneWay]
    Task SendToGroupExcept(HubMessage message, string[] excludedConnectionIds);
}
