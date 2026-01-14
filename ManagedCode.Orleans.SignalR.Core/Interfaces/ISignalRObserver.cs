using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;
using System.Threading.Tasks;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRObserver : IGrainObserver
{
    [OneWay]
    Task OnNextAsync(HubMessage message);
}
