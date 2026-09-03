using ManagedCode.Orleans.SignalR.Core.Interfaces;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

internal sealed class BatchGroupJoinCallFilter : IIncomingGrainCallFilter
{
    private static GateState? _activeGate;

    public static GateLease Arm(string groupNamePrefix)
    {
        var state = new GateState(groupNamePrefix);
        if (Interlocked.CompareExchange(ref _activeGate, state, null) is not null)
        {
            throw new InvalidOperationException("A batch group join gate is already active.");
        }

        return new GateLease(state);
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var state = Volatile.Read(ref _activeGate);
        if (state is null ||
            !string.Equals(context.MethodName, nameof(ISignalRGroupPartitionGrain.AddConnectionToGroups), StringComparison.Ordinal) ||
            context.Request.GetArgument(1) is not string[] groupNames ||
            !groupNames.Any(groupName => groupName.StartsWith(state.GroupNamePrefix, StringComparison.Ordinal)))
        {
            await context.Invoke();
            return;
        }

        var callNumber = Interlocked.Increment(ref state._callCount);
        if (callNumber == 1)
        {
            try
            {
                await context.Invoke();
            }
            finally
            {
                state.FirstPartitionCompleted.TrySetResult();
            }

            return;
        }

        if (callNumber == 2)
        {
            await state.FirstPartitionCompleted.Task;
            state.JoinPaused.TrySetResult();
            await state.ReleaseJoin.Task;
        }

        await context.Invoke();
    }

    internal sealed class GateState(string groupNamePrefix)
    {
        public string GroupNamePrefix { get; } = groupNamePrefix;

        public TaskCompletionSource FirstPartitionCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource JoinPaused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseJoin { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int _callCount;
    }

    internal sealed class GateLease : IDisposable
    {
        private GateState? _state;

        internal GateLease(GateState state)
        {
            _state = state;
        }

        public Task WaitUntilPausedAsync(TimeSpan timeout)
        {
            var state = _state ?? throw new ObjectDisposedException(nameof(GateLease));
            return state.JoinPaused.Task.WaitAsync(timeout);
        }

        public void Release()
        {
            _state?.ReleaseJoin.TrySetResult();
        }

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is null)
            {
                return;
            }

            state.ReleaseJoin.TrySetResult();
            Interlocked.CompareExchange(ref _activeGate, null, state);
        }
    }
}
