using System.Globalization;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Server.Helpers;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains;

[Reentrant]
public sealed class StateWriteLockGrain(
    [PersistentState(nameof(StateWriteLockGrain), OrleansSignalROptions.OrleansSignalRStorage)]
    IPersistentState<ConnectionState> state)
    : Grain, IStateWriteLockGrain
{
    private readonly StateWriteLock _stateWriteLock = new();
    // Reuse production state models to avoid test-only Orleans serializers.
    private const string WriteCountKey = "__state_write_lock_count";
    private int _activeWrites;
    private int _maxConcurrentWrites;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);
        state.State ??= new ConnectionState();
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task WriteWithDelayAsync(TimeSpan delay)
    {
        await _stateWriteLock.RunAsync(async () =>
        {
            var active = Interlocked.Increment(ref _activeWrites);
            if (active > _maxConcurrentWrites)
            {
                _maxConcurrentWrites = active;
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                var count = GetWriteCount(state.State) + 1;
                SetWriteCount(state.State, count);
                await state.WriteStateSafeAsync();
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        });
    }

    public Task<int> GetMaxConcurrentWritesAsync()
    {
        return Task.FromResult(_maxConcurrentWrites);
    }

    public Task<int> GetWriteCountAsync()
    {
        return Task.FromResult(GetWriteCount(state.State));
    }

    public Task ResetAsync()
    {
        _activeWrites = 0;
        _maxConcurrentWrites = 0;
        state.State = new ConnectionState();
        return _stateWriteLock.RunAsync(() => state.WriteStateSafeAsync());
    }

    private static int GetWriteCount(ConnectionState state)
    {
        if (state.ConnectionIds.TryGetValue(WriteCountKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return 0;
    }

    private static void SetWriteCount(ConnectionState state, int value)
    {
        state.ConnectionIds[WriteCountKey] = value.ToString(CultureInfo.InvariantCulture);
    }
}
