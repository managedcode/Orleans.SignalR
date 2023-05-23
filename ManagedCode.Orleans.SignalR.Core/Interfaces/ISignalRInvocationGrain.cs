using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRInvocationGrain : IGrainWithStringKey
{
    [OneWay]
    Task TryCompleteResult(string connectionId, CompletionMessage message);
    Task<ReturnType> TryGetReturnType();
    
    [OneWay]
    ValueTask AddInvocation(InvocationInfo invocationInfo);
    
    [OneWay]
    ValueTask<InvocationInfo?> RemoveInvocation();
}