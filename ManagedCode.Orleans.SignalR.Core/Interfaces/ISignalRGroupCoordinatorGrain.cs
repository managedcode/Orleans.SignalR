using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupCoordinatorGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<int> GetPartitionCount();
    
    [ReadOnly]
    Task<int> GetPartitionForGroup(string groupName);
    
    [OneWay]
    Task SendToGroup(string groupName, HubMessage message);

    [OneWay]
    Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds);
    
    [OneWay]
    Task SendToGroups(string[] groupNames, HubMessage message);
    
    Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer);
    
    Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer);
}
