using System.Collections.Generic;
using System.Threading;
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
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
//[GrainType($"ManagedCode.${nameof(SignalRConnectionHolderGrain<THub>)}")]
public class SignalRConnectionHolderGrain<THub> : Grain, ISignalRConnectionHolderGrain<THub>
{
    private readonly ILogger<SignalRConnectionHolderGrain<THub>> _logger;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    
    public SignalRConnectionHolderGrain(ILogger<SignalRConnectionHolderGrain<THub>> logger,  
        [PersistentState(nameof(SignalRConnectionHolderGrain<THub>), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage,
        IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _stateStorage = stateStorage;
        _options = options;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(_stateStorage.State.ConnectionIds.Count == 0)
           await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }

    public Task AddConnection(string connectionId)
    {
        _stateStorage.State.ConnectionIds.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId)
    {
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }
    
    public Task SendToAll(InvocationMessage message)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in _stateStorage.State.ConnectionIds)
        {
            var stream = NameHelperGenerator.GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider), connectionId);
            tasks.Add(stream.OnNextAsync(message));
        }


        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }

    public Task SendToAllExcept(InvocationMessage message, string[] excludedConnectionIds)
    {
        var hashSet = new HashSet<string>(excludedConnectionIds);
        var tasks = new List<Task>();

        foreach (var connectionId in _stateStorage.State.ConnectionIds)
        {
            if (hashSet.Contains(connectionId))
                continue;
            
            var stream = NameHelperGenerator.GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider), connectionId);
            tasks.Add(stream.OnNextAsync(message));
        }

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }

    public Task<bool> SendToConnection(InvocationMessage message, string connectionId)
    {
        if (!_stateStorage.State.ConnectionIds.Contains(connectionId))
            return Task.FromResult(false);

        var stream = NameHelperGenerator
            .GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider), connectionId);
        _ = Task.Run(() => stream.OnNextAsync(message));

        return Task.FromResult(true);
    }

    public Task SendToConnections(InvocationMessage message, string[] connectionIds)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in connectionIds)
        {
            var stream = NameHelperGenerator.GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider), connectionId);
            tasks.Add(stream.OnNextAsync(message));
        }

        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }
}