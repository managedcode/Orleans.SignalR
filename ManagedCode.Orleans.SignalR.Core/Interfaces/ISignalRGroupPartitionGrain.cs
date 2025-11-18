using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupPartitionGrain : IGrainWithIntegerKey, IObserverConnectionManager
{
    [AlwaysInterleave]
    [OneWay]
    Task SendToGroups(HubMessage message, string[] groupNames);

    [AlwaysInterleave]
    [OneWay]
    Task SendToGroupsExcept(HubMessage message, string[] groupNames, string[] excludedConnectionIds);

    [AlwaysInterleave]
    Task AddConnectionToGroup(string groupName, string connectionId, ISignalRObserver observer);

    [AlwaysInterleave]
    Task RemoveConnectionFromGroup(string groupName, string connectionId, ISignalRObserver observer);

    [ReadOnly]
    [AlwaysInterleave]
    Task<bool> HasConnection(string connectionId);

    [AlwaysInterleave]
    Task EnsureInitialized(string hubKey);
}
