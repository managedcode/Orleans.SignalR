using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRUserGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    [AlwaysInterleave]
    Task SendToUser(HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task RequestMessage();
}
