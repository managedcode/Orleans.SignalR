using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Communication.CQRS;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Server.Helpers;
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
    private readonly StateWriteLock _stateWriteLock = new();
    private TaskCompletionSource<CompletionMessage> _completionAvailable = CreateCompletionSignal();

    public SignalRInvocationGrain(ILogger<SignalRInvocationGrain> logger,
        IOptions<OrleansSignalROptions> orleansSignalOptions, IOptions<HubOptions> hubOptions,
        [PersistentState(nameof(SignalRInvocationGrain), OrleansSignalROptions.OrleansSignalRStorage)]
        IPersistentState<InvocationInfo> stateStorage)
    {
        _logger = logger;
        _stateStorage = stateStorage;

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(orleansSignalOptions, hubOptions);
        var expiration = TimeIntervalHelper.GetObserverExpiration(orleansSignalOptions, timeSpan);
        _observerManager = new ObserverManager<ISignalRObserver>(expiration, _logger);
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await _stateStorage.ReadStateAsync(cancellationToken);
        _stateStorage.State ??= new InvocationInfo();
        if (_stateStorage.State.Completion is { } completion)
        {
            _completionAvailable.TrySetResult(completion);
        }
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task TryCompleteResult(string connectionId, HubMessage message)
    {
        Logs.TryCompleteResult(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString(), connectionId);
        _logger.LogInformation("Hub: {PrimaryKeyString}; TryCompleteResult: {ConnectionId}", this.GetPrimaryKeyString(),
            connectionId);
        if (_stateStorage.State == null || _stateStorage.State.ConnectionId != connectionId)
        {
            return;
        }

        if (message is CompletionMessage completionMessage)
        {
            var persisted = await _stateWriteLock.RunAsync(() => _stateStorage.WriteStateSafeAsync(state =>
                state.TryComplete(connectionId, completionMessage)));
            if (persisted)
            {
                _completionAvailable.TrySetResult(completionMessage);
            }
        }

        // Critical: do NOT execute SignalR observer notifications on the Orleans scheduler.
        await Task.Run(() => _observerManager.Notify(s => s.OnNextAsync(message)));
    }

    public Task<ReturnType> TryGetReturnType()
    {
        Logs.TryGetReturnType(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString());
        if (_stateStorage.State is not { ConnectionId.Length: > 0, InvocationId.Length: > 0, Type.Length: > 0 } state)
        {
            return Task.FromResult(new ReturnType());
        }

        return Task.FromResult(new ReturnType
        {
            Result = true,
            Type = state.Type
        });
    }

    public async Task AddInvocation(ISignalRObserver? observer, InvocationInfo invocationInfo)
    {
        if (invocationInfo.InvocationId is null || invocationInfo.ConnectionId is null)
        {
            return;
        }

        Logs.AddInvocation(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString(), invocationInfo.InvocationId, invocationInfo.ConnectionId);

        if (observer is not null)
        {
            _observerManager.Subscribe(observer, observer);
        }
        await _stateWriteLock.RunAsync(() => _stateStorage.WriteStateSafeAsync(state => state.Register(invocationInfo)));
        _completionAvailable = CreateCompletionSignal();
    }

    public async Task<InvocationInfo?> RemoveInvocation()
    {
        Logs.RemoveInvocation(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString());
        _observerManager.Clear();
        var into = _stateStorage.State;
        await _stateWriteLock.RunAsync(() => _stateStorage.ClearStateSafeAsync());
        DeactivateOnIdle();
        return into;
    }

    public IAsyncEnumerable<CqrsStreamChunk<InvocationProgress, CompletionMessage>> WaitForCompletion(
        CancellationToken cancellationToken)
    {
        return CqrsStream.Create<InvocationProgress, CompletionMessage>(async _ =>
        {
            if (_stateStorage.State.Completion is { } completion)
            {
                return completion;
            }

            // This signal is activation-local notification only. The terminal value is persisted
            // before it is signalled and is restored on the next activation after a silo failure.
            return await _completionAvailable.Task.WaitAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task Ping(ISignalRObserver observer)
    {
        Logs.Ping(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString());
        _observerManager.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task AddConnection(string connectionId, ISignalRObserver observer)
    {
        //ignore for this grain
        Logs.AddConnection(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString(), connectionId);
        return Task.CompletedTask;
    }

    public async Task RemoveConnection(string connectionId, ISignalRObserver observer)
    {
        Logs.RemoveConnection(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString(), connectionId);
        _observerManager.Unsubscribe(observer);
        _observerManager.Clear();
        await _stateWriteLock.RunAsync(() => _stateStorage.ClearStateSafeAsync());
        DeactivateOnIdle();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        Logs.OnDeactivateAsync(_logger, nameof(SignalRInvocationGrain), this.GetPrimaryKeyString());

        _observerManager.ClearExpired();

        if (string.IsNullOrEmpty(_stateStorage.State.ConnectionId) ||
            string.IsNullOrEmpty(_stateStorage.State.InvocationId))
        {
            await _stateWriteLock.RunAsync(() => _stateStorage.ClearStateSafeAsync(cancellationToken));
        }
        else
        {
            await _stateWriteLock.RunAsync(() => _stateStorage.WriteStateSafeAsync(cancellationToken));
        }
    }

    private static TaskCompletionSource<CompletionMessage> CreateCompletionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
