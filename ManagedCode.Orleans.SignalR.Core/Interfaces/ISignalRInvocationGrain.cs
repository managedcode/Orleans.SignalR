using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRInvocationGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    Task TryCompleteResult(string connectionId, HubMessage message);

    Task<ReturnType> TryGetReturnType();

    Task AddInvocation(ISignalRObserver? observer, InvocationInfo invocationInfo);

    Task<InvocationInfo?> RemoveInvocation();

    Task<CompletionMessage?> WaitForCompletion();
}
