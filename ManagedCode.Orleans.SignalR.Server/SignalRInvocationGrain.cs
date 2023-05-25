using System;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Utilities;

namespace ManagedCode.Orleans.SignalR.Server;

[Reentrant]
[ActivationCountBasedPlacement]
[GrainType($"ManagedCode.{nameof(SignalRInvocationGrain)}")]
public class SignalRInvocationGrain : Grain, ISignalRInvocationGrain
{
    private readonly ILogger<SignalRInvocationGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IOptions<OrleansSignalROptions> _options;
    private readonly IPersistentState<InvocationInfo> _stateStorage;

    public SignalRInvocationGrain(ILogger<SignalRInvocationGrain> logger,
        [PersistentState(nameof(SignalRInvocationGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<InvocationInfo> stateStorage, IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _stateStorage = stateStorage;
        _options = options;
        _observerManager = new ObserverManager<ISignalRObserver>(
            //_globalHubOptions.Value.KeepAliveInterval.Value, //TODO:
            TimeSpan.FromMinutes(5), logger);
    }

    public async Task TryCompleteResult(string connectionId, HubMessage message)
    {
        if (_stateStorage.State == null || _stateStorage.State.ConnectionId != connectionId)
            return;

        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public Task<ReturnType> TryGetReturnType()
    {
        if (_stateStorage.State == null)
            return Task.FromResult(new ReturnType());

        return Task.FromResult(new ReturnType
        {
            Result = true,
            Type = _stateStorage.State.Type
        });
    }

    public ValueTask AddInvocation(ISignalRObserver observer, InvocationInfo invocationInfo)
    {
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State = invocationInfo;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<InvocationInfo?> RemoveInvocation()
    {
        _observerManager.Clear();
        var into = _stateStorage.State;
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
        return into;
    }

    public ValueTask Ping(ISignalRObserver observer)
    {
        _observerManager.Subscribe(observer, observer);
        return ValueTask.CompletedTask;
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        //ignore for this grain
        return Task.CompletedTask;
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        //ignore connectionId
        _observerManager.Unsubscribe(observer);
        _observerManager.Clear();
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_stateStorage.State.ConnectionId) ||
            string.IsNullOrEmpty(_stateStorage.State.InvocationId))
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}