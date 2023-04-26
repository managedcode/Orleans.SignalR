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
public class SignalRInvocationGrain<THub> : Grain, ISignalRInvocationGrain<THub>
{
    private readonly ILogger<SignalRInvocationGrain<THub>> _logger;
    private readonly IPersistentState<InvocationInfo> _stateStorage;
    private readonly IOptions<OrleansSignalROptions> _options;
    
    public SignalRInvocationGrain(ILogger<SignalRInvocationGrain<THub>> logger,  
        [PersistentState(nameof(SignalRInvocationGrain<THub>), OrleansSignalROptions.OrleansSignalRStorage)] IPersistentState<InvocationInfo> stateStorage,
        IOptions<OrleansSignalROptions> options)
    {
        _logger = logger;
        _stateStorage = stateStorage;
        _options = options;
    }
    
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(string.IsNullOrEmpty(_stateStorage.State.ConnectionId) || string.IsNullOrEmpty(_stateStorage.State.InvocationId))
            await _stateStorage.ClearStateAsync();
        else
            await _stateStorage.WriteStateAsync();
    }
    
    public Task TryCompleteResult(string connectionId, CompletionMessage message)
    {
        if (_stateStorage.State == null || _stateStorage.State.ConnectionId != connectionId)
            return Task.CompletedTask;

        var stream = NameHelperGenerator
            .GetStream<THub, CompletionMessage>(this.GetStreamProvider(_options.Value.StreamProvider), _stateStorage.State.InvocationId);
        
        _ = Task.Run(() => stream.OnNextAsync(message));

        //await RemoveInvocation();
        return Task.CompletedTask;
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

    public ValueTask AddInvocation(InvocationInfo invocationInfo)
    {
        _stateStorage.State = invocationInfo;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<InvocationInfo?> RemoveInvocation()
    {
        var into = _stateStorage.State;
        await _stateStorage.ClearStateAsync();
        DeactivateOnIdle();
        return into;
    }
}