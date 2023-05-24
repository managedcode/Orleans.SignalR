using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupHolderGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    Task AddConnectionToGroup(string connectionId, ISignalRObserver observer, string groupName);
    
    [OneWay]
    Task RemoveConnectionFromGroup(string connectionId, ISignalRObserver observer, string groupName);
}