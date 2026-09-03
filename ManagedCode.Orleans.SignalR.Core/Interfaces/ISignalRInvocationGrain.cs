using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Communication.CQRS;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRInvocationGrain : IGrainWithStringKey, IObserverConnectionManager
{
    [AlwaysInterleave]
    Task TryCompleteResult(string connectionId, HubMessage message);

    [AlwaysInterleave]
    Task<ReturnType> TryGetReturnType();

    [AlwaysInterleave]
    Task AddInvocation(ISignalRObserver? observer, InvocationInfo invocationInfo);

    [AlwaysInterleave]
    Task<InvocationInfo?> RemoveInvocation();

    [AlwaysInterleave]
    IAsyncEnumerable<CqrsStreamChunk<InvocationProgress, CompletionMessage>> WaitForCompletion(
        CancellationToken cancellationToken);
}
