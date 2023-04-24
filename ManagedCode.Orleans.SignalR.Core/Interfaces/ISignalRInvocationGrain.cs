using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRInvocationGrain<THub> : IGrainWithStringKey
{
    //Task<bool> InvokeConnectionAsync(string connectionId, InvocationMessage message);
    Task TryCompleteResult(string connectionId, CompletionMessage message);
    Task<ReturnType> TryGetReturnType();
    ValueTask AddInvocation(InvocationInfo invocationInfo);
    ValueTask<InvocationInfo?> RemoveInvocation();
}