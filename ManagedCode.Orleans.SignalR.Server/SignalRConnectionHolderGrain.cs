using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;



[Reentrant]
[GrainType($"ManagedCode.${nameof(SignalRConnectionHolderGrain<THub>)}")]
public class SignalRConnectionHolderGrain<THub> : Grain, ISignalRConnectionHolderGrain<THub>
{
    private readonly ILogger<SignalRConnectionHolderGrain<THub>> _logger;
    private readonly IOptions<HubOptions>? _globalHubOptions;
    private readonly IPersistentState<ConnectionState> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly ObserverManager<ISignalRConnection<THub>> _observerManager;
    
    public SignalRConnectionHolderGrain(ILogger<SignalRConnectionHolderGrain<THub>> logger, IOptions<HubOptions>? globalHubOptions, 
        [PersistentState(nameof(SignalRConnectionHolderGrain<THub>), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<ConnectionState> stateStorage,
        IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _globalHubOptions = globalHubOptions;
        _stateStorage = stateStorage;
        _options = options;
        _observerManager = new ObserverManager<ISignalRConnection<THub>>(
            //_globalHubOptions.Value.KeepAliveInterval.Value,
            TimeSpan.FromMinutes(5), 
            logger);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(_stateStorage.State.ConnectionIds.Count == 0)
           await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
    
    public Task AddConnection(string connectionId, ISignalRConnection<THub> connection)
    {
        _observerManager.Subscribe(connection, connection);
        _stateStorage.State.ConnectionIds.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveConnection(string connectionId, ISignalRConnection<THub> connection)
    {
        _observerManager.Subscribe(connection, connection);
        _stateStorage.State.ConnectionIds.Remove(connectionId);
        return Task.CompletedTask;
    }
    
    public async Task SendToAll(InvocationMessage message)
    {
        foreach (var xxxx in  _observerManager)
        {
            Task.Factory.StartNew(()=>xxxx.SendMessage(message));
        }
    
        //_ = _observerManager.Notify(s => s.SendMessage(message));
        var x = 5;
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