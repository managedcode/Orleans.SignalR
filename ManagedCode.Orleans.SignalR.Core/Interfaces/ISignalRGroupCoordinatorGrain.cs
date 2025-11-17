using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupCoordinatorGrain : IGrainWithStringKey
{
    [OneWay]
    [AlwaysInterleave]
    Task<int> GetPartitionCount();

    [OneWay]
    [AlwaysInterleave]
    Task<int> GetPartitionForGroup(string groupName);

    [OneWay]
    [AlwaysInterleave]
    Task SendToGroup(string groupName, HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task SendToGroupExcept(string groupName, HubMessage message, string[] excludedConnectionIds);

    [OneWay]
    [AlwaysInterleave]
    Task SendToGroups(string[] groupNames, HubMessage message);

    [OneWay]
    [AlwaysInterleave]
    Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer);

    [OneWay]
    [AlwaysInterleave]
    Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer);

    [OneWay]
    [AlwaysInterleave]
    Task NotifyGroupRemoved(string groupName);
}
