using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
public class SignalRConnectionHolderGrain<THub> : Grain, ISignalRConnectionHolderGrain<THub>
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalRConnectionHolderGrain<THub>> _logger;
    private readonly ConnectionState _state = new();

    Dictionary<string, string> _invocations = new();


    public SignalRConnectionHolderGrain(ILogger<SignalRConnectionHolderGrain<THub>> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public Task AddConnection(string connectionId)
    {
        _state.ConnectionIds.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId)
    {
        _state.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }

    public Task SendToAll(InvocationMessage message)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in _state.ConnectionIds)
            tasks.Add(NameHelperGenerator
                .GetStream<THub,InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), connectionId)
                .OnNextAsync(message));

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }

    public Task SendToAllExcept(InvocationMessage message, string[] excludedConnectionIds)
    {
        var hashSet = new HashSet<string>(excludedConnectionIds);
        var tasks = new List<Task>();

        foreach (var connectionId in _state.ConnectionIds)
        {
            if (hashSet.Contains(connectionId))
                continue;

            tasks.Add(NameHelperGenerator
                .GetStream<THub,InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), connectionId)
                .OnNextAsync(message));
        }

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }

    public Task SendToConnection(InvocationMessage message, string connectionId)
    {
        _ = Task.Run(() => NameHelperGenerator
            .GetStream<THub,InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), connectionId)
            .OnNextAsync(message));

        return Task.CompletedTask;
    }

    public Task SendToConnections(InvocationMessage message, string[] connectionIds)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in connectionIds)
            tasks.Add(NameHelperGenerator
                .GetStream<THub,InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), connectionId)
                .OnNextAsync(message));

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }
}

[Reentrant]
public class SignalRInvocationGrain<THub> : Grain, ISignalRInvocationGrain<THub>
{

    private InvocationInfo? _invocationInfo;
    
    public Task<bool> InvokeConnectionAsync(string connectionId, InvocationMessage message)
    {
        //if (!_state.ConnectionIds.Contains(connectionId))
        //    return Task.FromResult(false);
        
        _ = Task.Run(() => NameHelperGenerator
            .GetStream<THub,InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider), connectionId)
            .OnNextAsync(message));
        
        return Task.FromResult(true);
    }

    public async Task TryCompleteResult(string connectionId, CompletionMessage message)
    {
        var stream = NameHelperGenerator
            .GetStream<THub, CompletionMessage>(
                this.GetStreamProvider(new OrleansSignalROptions().StreamProvider),
                message.InvocationId);
        var sub = await stream.GetAllSubscriptionHandles();
        await stream.OnNextAsync(message);
    }

    public Task<ReturnType> TryGetReturnType(string connectionId)
    {
        if(_invocationInfo == null || _invocationInfo.ConnectionId != connectionId)
            return Task.FromResult<ReturnType>(new ReturnType());
        
        return Task.FromResult(new ReturnType()
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