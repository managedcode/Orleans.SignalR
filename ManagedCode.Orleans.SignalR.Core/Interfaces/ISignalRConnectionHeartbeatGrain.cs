using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHeartbeatGrain : IGrainWithStringKey
{
    [AlwaysInterleave]
    Task Start(ConnectionHeartbeatRegistration registration);

    [OneWay]
    [AlwaysInterleave]
    Task Stop();
}
