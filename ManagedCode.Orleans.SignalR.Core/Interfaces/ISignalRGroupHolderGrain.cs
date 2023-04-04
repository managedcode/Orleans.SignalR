using System.Threading.Tasks;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupHolderGrain<THub> : IGrainWithStringKey
{
    Task AddConnectionToGroup(string connectionId, string groupName);
    Task RemoveConnectionFromGroup(string connectionId, string groupName);

    Task RemoveConnection(string connectionId);
}