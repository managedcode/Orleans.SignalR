using System.Collections.Generic;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
public class SignalRUserGrain<THub> : Grain, ISignalRUserGrain<THub>
{
    private readonly IGrainFactory _grainFactory;
    private readonly IOptions<OrleansSignalROptions> _options;

    private readonly ILogger<SignalRUserGrain<THub>> _logger;
    private readonly ConnectionState _state = new();


    public SignalRUserGrain(ILogger<SignalRUserGrain<THub>> logger, IGrainFactory grainFactory, IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _grainFactory = grainFactory;
        _options = options;
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

    public Task SendToUser(InvocationMessage message)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in _state.ConnectionIds)
            tasks.Add(NameHelperGenerator
                .GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider),
                    connectionId)
                .OnNextAsync(message));

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }
}