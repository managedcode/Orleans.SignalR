using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRUserGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [AlwaysInterleave]
    [OneWay]
    Task SendToUser(HubMessage message);

    [AlwaysInterleave]
    [OneWay]
    Task RequestMessage();
}
