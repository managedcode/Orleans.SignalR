using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRUserGrain : IGrainWithStringKey
{
    Task AddConnection(string connectionId);
    Task RemoveConnection(string connectionId);

    Task SendToUser(InvocationMessage message);
}