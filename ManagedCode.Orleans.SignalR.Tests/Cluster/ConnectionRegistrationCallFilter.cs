using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

internal sealed class ConnectionRegistrationCallFilter : IIncomingGrainCallFilter
{
    private static GateState? _activeGate;

    public static GateLease Arm()
    {
        var state = new GateState();
        if (Interlocked.CompareExchange(ref _activeGate, state, null) is not null)
        {
            throw new InvalidOperationException("A connection registration gate is already active.");
        }

        return new GateLease(state);
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var state = Volatile.Read(ref _activeGate);
        if (state is null)
        {
            await context.Invoke();
            return;
        }

        var isLookup = string.Equals(context.MethodName,
            nameof(ISignalRConnectionCoordinatorGrain.GetPartitionForConnection), StringComparison.Ordinal);
        if (isLookup && context.Request.GetArgument(0) is string connectionId)
        {
            Interlocked.CompareExchange(ref state.ConnectionId, connectionId, null);
            state.Entered.TrySetResult(connectionId);
            await state.Released.Task;
        }

        var isConnectionMutation =
            string.Equals(context.MethodName, nameof(IObserverConnectionManager.AddConnection), StringComparison.Ordinal) ||
            string.Equals(context.MethodName, nameof(ISignalRConnectionCoordinatorGrain.NotifyConnectionRemoved), StringComparison.Ordinal);
        var isTarget = isConnectionMutation && context.Request.GetArgument(0) is string targetConnectionId &&
            string.Equals(targetConnectionId, Volatile.Read(ref state.ConnectionId), StringComparison.Ordinal);
        if (isTarget &&
            string.Equals(context.MethodName, nameof(IObserverConnectionManager.AddConnection), StringComparison.Ordinal) &&
            context.Request.GetArgument(1) is not ISignalRObserver)
        {
            Interlocked.Increment(ref state.NullObserverAttempts);
        }

        var isCleanup = isTarget && state.LookupReturned.Task.IsCompleted &&
            string.Equals(context.MethodName, nameof(ISignalRConnectionCoordinatorGrain.NotifyConnectionRemoved), StringComparison.Ordinal);
        try
        {
            await context.Invoke();
        }
        finally
        {
            if (isLookup)
            {
                state.LookupReturned.TrySetResult();
            }
            if (isCleanup)
            {
                state.CleanupCompleted.TrySetResult();
            }
        }
    }

    internal sealed class GateState
    {
        public string? ConnectionId;
        public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int NullObserverAttempts;
        public TaskCompletionSource LookupReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class GateLease(GateState state) : IDisposable
    {
        public Task<string> WaitUntilPausedAsync(TimeSpan timeout) => state.Entered.Task.WaitAsync(timeout);

        public int NullObserverAttempts => Volatile.Read(ref state.NullObserverAttempts);

        public Task WaitForCleanupAsync(TimeSpan timeout) => state.CleanupCompleted.Task.WaitAsync(timeout);

        public void Release() => state.Released.TrySetResult();

        public void Dispose()
        {
            Release();
            Interlocked.CompareExchange(ref _activeGate, null, state);
        }
    }
}
