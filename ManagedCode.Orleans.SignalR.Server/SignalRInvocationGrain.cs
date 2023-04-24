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

    public Task<bool> InvokeConnectionAsync(string connectionId, InvocationMessage message)
    {
        //if (!_state.ConnectionIds.Contains(connectionId))
        //    return Task.FromResult(false);

        _ = Task.Run(() => NameHelperGenerator
            .GetStream<THub, InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider),
                connectionId)
            .OnNextAsync(message));

        return Task.FromResult(true);
    }

    public async Task TryCompleteResult(string connectionId, CompletionMessage message)
    {
        var stream = NameHelperGenerator
            .GetStream<THub, CompletionMessage>(
                this.GetStreamProvider(new OrleansSignalROptions().StreamProvider),
                connectionId);
        var sub = await stream.GetAllSubscriptionHandles();
        await stream.OnNextAsync(message);
    }

    public Task<ReturnType> TryGetReturnType(string connectionId)
    {
        if (_invocationInfo == null || _invocationInfo.ConnectionId != connectionId)
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