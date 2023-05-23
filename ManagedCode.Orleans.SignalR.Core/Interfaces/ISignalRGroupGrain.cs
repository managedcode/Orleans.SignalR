using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRGroupGrain : IGrainWithStringKey
{
    Task SendToGroup(InvocationMessage message);
    Task SendToGroupExcept(InvocationMessage message, string[] excludedConnectionIds);

    Task AddConnection(string connectionId);
    Task RemoveConnection(string connectionId);
}