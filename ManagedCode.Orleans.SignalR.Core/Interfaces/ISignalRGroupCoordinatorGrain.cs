using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupCoordinatorGrain : IGrainWithStringKey
{
    [ReadOnly]
    [AlwaysInterleave]
    Task<int> GetPartitionCount();

    [ReadOnly]
    [AlwaysInterleave]
    Task<int> GetPartitionForGroup(string groupName);

    [AlwaysInterleave]
    [OneWay]
    Task SendToGroup(string groupName, HubMessage message);

    [AlwaysInterleave]
    [OneWay]
    Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds);

    [AlwaysInterleave]
    [OneWay]
    Task SendToGroups(string[] groupNames, HubMessage message);

    [AlwaysInterleave]
    Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer);

    [AlwaysInterleave]
    Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer);

    [AlwaysInterleave]
    Task NotifyGroupRemoved(string groupName);
}
