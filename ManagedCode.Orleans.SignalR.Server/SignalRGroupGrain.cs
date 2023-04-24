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
public class SignalRGroupGrain<THub> : Grain, ISignalRGroupGrain<THub>
{
    private readonly IGrainFactory _grainFactory;

    private readonly ILogger<SignalRGroupGrain<THub>> _logger;
    private readonly ConnectionState _state = new();


    public SignalRGroupGrain(ILogger<SignalRGroupGrain<THub>> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public Task SendToGroup(InvocationMessage message)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in _state.ConnectionIds)
            tasks.Add(NameHelperGenerator
                .GetStream<THub, InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider),
                    connectionId)
                .OnNextAsync(message));

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }

    public Task SendToGroupExcept(InvocationMessage message, string[] excludedConnectionIds)
    {
        var hashSet = new HashSet<string>(excludedConnectionIds);
        var tasks = new List<Task>();

        foreach (var connectionId in _state.ConnectionIds)
        {
            if (hashSet.Contains(connectionId))
                continue;

            tasks.Add(NameHelperGenerator
                .GetStream<THub, InvocationMessage>(this.GetStreamProvider(new OrleansSignalROptions().StreamProvider),
                    connectionId)
                .OnNextAsync(message));
        }

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
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
}