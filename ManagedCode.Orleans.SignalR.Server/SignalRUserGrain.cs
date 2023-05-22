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
[GrainType($"ManagedCode.${nameof(SignalRUserGrain<THub>)}")]
public class SignalRUserGrain<THub> : Grain, ISignalRUserGrain<THub>
{
    private readonly ILogger<SignalRUserGrain<THub>> _logger;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    
    public SignalRUserGrain(ILogger<SignalRUserGrain<THub>> logger,  
        [PersistentState(nameof(SignalRUserGrain<THub>), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage,
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

    public Task SendToUser(InvocationMessage message)
    {
        var tasks = new List<Task>();

        foreach (var connectionId in _stateStorage.State.ConnectionIds)
        {
            var stream = NameHelperGenerator
                .GetStream<THub, InvocationMessage>(this.GetStreamProvider(_options.Value.StreamProvider),
                    connectionId);
            tasks.Add(stream.OnNextAsync(message));
        }


        _ = Task.Run(() => Task.WhenAll(tasks));

        return Task.CompletedTask;
    }
}