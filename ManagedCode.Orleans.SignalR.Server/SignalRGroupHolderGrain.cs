using System.Collections.Generic;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
public class SignalRGroupHolderGrain<THub> : Grain, ISignalRGroupHolderGrain<THub>
{
    private readonly IGrainFactory _grainFactory;
    private readonly ConnectionGroupState _groups = new();

    private readonly ILogger<SignalRGroupHolderGrain<THub>> _logger;


    public SignalRGroupHolderGrain(ILogger<SignalRGroupHolderGrain<THub>> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }


    public Task AddConnectionToGroup(string connectionId, string groupName)
    {
        if (_groups.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Add(connectionId);
        else
            _groups.Groups.Add(groupName, new ConnectionState
            {
                ConnectionIds = new HashSet<string> { connectionId }
            });

        return Task.CompletedTask;
    }

    public Task RemoveConnectionFromGroup(string connectionId, string groupName)
    {
        if (_groups.Groups.TryGetValue(groupName, out var state))
            state.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }


    public Task RemoveConnection(string connectionId)
    {
        foreach (var connections in _groups.Groups.Values)
            connections.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }
}