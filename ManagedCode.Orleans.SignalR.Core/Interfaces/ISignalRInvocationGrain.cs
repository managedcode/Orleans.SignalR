using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRInvocationGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [OneWay]
    [AlwaysInterleave]
    Task TryCompleteResult(string connectionId, HubMessage message);

    [AlwaysInterleave]
    Task<ReturnType> TryGetReturnType();

    [AlwaysInterleave]
    Task AddInvocation(ISignalRObserver? observer, InvocationInfo invocationInfo);

    [AlwaysInterleave]
    Task<InvocationInfo?> RemoveInvocation();

    [AlwaysInterleave]
    Task<CompletionMessage?> WaitForCompletion();
}
