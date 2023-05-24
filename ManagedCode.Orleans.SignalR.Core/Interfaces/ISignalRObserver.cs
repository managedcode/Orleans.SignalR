using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRObserver : IGrainObserver
{
    [OneWay]
    Task OnNextAsync(HubMessage message);
}