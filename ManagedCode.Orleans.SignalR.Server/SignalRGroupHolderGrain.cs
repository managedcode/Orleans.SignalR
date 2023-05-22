using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[GrainType($"ManagedCode.${nameof(SignalRGroupHolderGrain<THub>)}")]
public class SignalRGroupHolderGrain<THub> : Grain, ISignalRGroupHolderGrain<THub>
{
    private readonly ILogger<SignalRGroupHolderGrain<THub>> _logger;
    private readonly IPersistentState<ConnectionGroupState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    
    public SignalRGroupHolderGrain(ILogger<SignalRGroupHolderGrain<THub>> logger,  
        [PersistentState(nameof(SignalRGroupHolderGrain<THub>), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionGroupState> stateStorage,
        IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _stateStorage = stateStorage;
        _options = options;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(_stateStorage.State.Groups.Count == 0)
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }

    public Task AddConnectionToGroup(string connectionId, string groupName)
    {
        if (_stateStorage.State.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Add(connectionId);
        else
            _stateStorage.State.Groups.Add(groupName, new ConnectionState
            {
                ConnectionIds = new HashSet<string> { connectionId }
            });

        return Task.CompletedTask;
    }

    public Task RemoveConnectionFromGroup(string connectionId, string groupName)
    {
        if (_stateStorage.State.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }


    public Task RemoveConnection(string connectionId)
    {
        foreach (var connections in _stateStorage.State.Groups.Values)
            connections.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }
}