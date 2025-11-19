using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface IObserverConnectionManager : IGrain
{
    [AlwaysInterleave]
    Task AddConnection(string connectionId, ISignalRObserver observer);

    [OneWay]
    [AlwaysInterleave]
    Task RemoveConnection(string connectionId, ISignalRObserver observer);

    [OneWay]
    [AlwaysInterleave]
    Task Ping(ISignalRObserver observer);
}
