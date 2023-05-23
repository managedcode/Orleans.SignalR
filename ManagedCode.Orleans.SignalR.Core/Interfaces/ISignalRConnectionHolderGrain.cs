using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHolderGrain : IGrainWithStringKey
{
    Task AddConnection(string connectionId);
    Task RemoveConnection(string connectionId);

    Task SendToAll(InvocationMessage message);
    Task SendToAllExcept(InvocationMessage message, string[] excludedConnectionIds);

    Task<bool> SendToConnection(InvocationMessage message, string connectionId);
    Task SendToConnections(InvocationMessage message, string[] connectionIds);
}
