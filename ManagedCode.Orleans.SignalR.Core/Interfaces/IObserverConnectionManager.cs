using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface IObserverConnectionManager : IGrain
{
    [OneWay]
    Task AddConnection(string connectionId, ISignalRObserver observer);

    [OneWay]
    Task RemoveConnection(string connectionId, ISignalRObserver observer);

    [OneWay]
    Task Ping(ISignalRObserver observer);
}
