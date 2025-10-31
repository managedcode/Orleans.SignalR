using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRConnectionHeartbeatGrain : IGrainWithStringKey
{
    Task Start(ConnectionHeartbeatRegistration registration);
    Task Stop();
}
