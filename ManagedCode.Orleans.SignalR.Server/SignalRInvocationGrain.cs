using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
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
[GrainType($"ManagedCode.{nameof(SignalRInvocationGrain)}")]
public class SignalRInvocationGrain : Grain, ISignalRInvocationGrain
{
    private readonly ILogger<SignalRInvocationGrain> _logger;
    private readonly ObserverManager<ISignalRObserver> _observerManager;
    private readonly IPersistentState<InvocationInfo> _stateStorage;

    public SignalRInvocationGrain(ILogger<SignalRInvocationGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions, IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRInvocationGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<InvocationInfo> stateStorage)
    {
        _logger = logger;
        _stateStorage = stateStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        _observerManager = new ObserverManager<ISignalRObserver>(timeSpan * 1.2, _logger);
    }

    public async Task TryCompleteResult(string connectionId, HubMessage message)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; TryCompleteResult: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        if (_stateStorage.State == null || _stateStorage.State.ConnectionId != connectionId)
            return;

        await _observerManager.Notify(s => s.OnNextAsync(message));
    }

    public async Task<ReturnType> TryGetReturnType()
    {
        await Task.Yield();
        if (_stateStorage.State == null)
            return new ReturnType();

        return new ReturnType
        {
            Result = true,
            Type = _stateStorage.State.Type
        };
    }

    public async Task AddInvocation(ISignalRObserver observer, InvocationInfo invocationInfo)
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; AddInvocation", this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
        _stateStorage.State = invocationInfo;
    }

    public async Task<InvocationInfo?> RemoveInvocation()
    {
        await Task.Yield();
        _logger.LogInformation("Hub: {PrimaryKeyString}; RemoveInvocation", this.GetPrimaryKeyString());
        _observerManager.Clear();
        var into = _stateStorage.State;
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
        return into;
    }

    public async Task Ping(ISignalRObserver observer)
    {
        await Task.Yield();
        _observerManager.Subscribe(observer, observer);
    }

    public async Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        //ignore for this grain
        _logger.LogInformation("Hub: {PrimaryKeyString}; AddConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        await Task.Yield();
        //ignore connectionId
        _logger.LogInformation("Hub: {PrimaryKeyString}; RemoveConnection: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        _observerManager.Unsubscribe(observer);
        _observerManager.Clear();
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _observerManager.ClearExpired();

        if (string.IsNullOrEmpty(_stateStorage.State.ConnectionId) ||
            string.IsNullOrEmpty(_stateStorage.State.InvocationId))
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
}