using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupHolderGrain<THub> : IGrainWithStringKey
{
    [OneWay]
    Task AddConnectionToGroup(string connectionId, string groupName);
    
    [OneWay]
    Task RemoveConnectionFromGroup(string connectionId, string groupName);

    [OneWay]
    Task RemoveConnection(string connectionId);
}