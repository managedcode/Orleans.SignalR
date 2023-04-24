using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
public class SignalRInvocationGrain<THub> : Grain, ISignalRInvocationGrain<THub>
{
    private InvocationInfo? _invocationInfo;
    
    public async Task TryCompleteResult(string connectionId, CompletionMessage message)
    {
        if (_invocationInfo == null || _invocationInfo.ConnectionId != connectionId)
            return;
        
        _ = Task.Run(() => NameHelperGenerator
            .GetStream<THub, CompletionMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), _invocationInfo.InvocationId)
            .OnNextAsync(message));

        var stream = NameHelperGenerator
            .GetStream<THub, CompletionMessage>(
                this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), _invocationInfo.InvocationId);

        var ssssss = stream.StreamId.ToString(); // STREAMID
        var subs = await stream.GetAllSubscriptionHandles();

        await stream.OnNextAsync(message);
    }

    public Task<ReturnType> TryGetReturnType()
    {
        if (_invocationInfo == null)
            return Task.FromResult(new ReturnType());

        return Task.FromResult(new ReturnType
        {
            Result = true,
            Type = _invocationInfo.Type
        });
    }

    public ValueTask AddInvocation(InvocationInfo invocationInfo)
    {
        _invocationInfo = invocationInfo;
        return ValueTask.CompletedTask;
    }

    public ValueTask<InvocationInfo?> RemoveInvocation()
    {
        var into = _invocationInfo;
        _invocationInfo = null;
        DeactivateOnIdle();
        return ValueTask.FromResult(into);
    }
}