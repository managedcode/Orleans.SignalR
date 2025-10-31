using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupPartitionGrain : IGrainWithIntegerKey, IObserverConnectionManager
{
    [OneWay]
    Task SendToGroups(HubMessage message, string[] groupNames);

    [OneWay]
    Task SendToGroupsExcept(HubMessage message, string[] groupNames, string[] excludedConnectionIds);
    
    [OneWay]
    Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer);
    
    [OneWay]
    Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer);

    [ReadOnly]
    Task<bool> HasConnection(string connectionId);

    [OneWay]
    Task EnsureInitialized(string hubKey);
}
