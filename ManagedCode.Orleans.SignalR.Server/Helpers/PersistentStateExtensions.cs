using Orleans.Runtime;
using Orleans.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCode.Orleans.SignalR.Server.Helpers;

internal static class PersistentStateExtensions
{
    private const int MaxRetries = 5;

    /// <summary>
    /// Safely writes state with retry on ETag conflicts.
    /// Handles both InconsistentStateException (persistent storage) and
    /// MemoryStorageEtagMismatchException (memory storage) for development scenarios.
    /// </summary>
    public static async Task<bool> WriteStateSafeAsync<TState>(this IPersistentState<TState> state, Func<TState, bool> applyChanges)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(applyChanges);

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                if (!applyChanges(state.State))
                {
                    return false;
                }

                await state.WriteStateAsync();
                return true;
            }
            catch (InconsistentStateException)
            {
                // Persistent storage ETag conflict
                await state.ReadStateAsync();
            }
            catch (Exception ex) when (IsEtagMismatch(ex))
            {
                // Memory storage ETag conflict (development/testing)
                await state.ReadStateAsync();
            }
        }

        // Final attempt without catching - let it throw if still failing
        if (!applyChanges(state.State))
        {
            return false;
        }
        await state.WriteStateAsync();
        return true;
    }

    /// <summary>
    /// Safely writes state with retry on ETag conflicts (no-change-detection version).
    /// Use this when state has already been modified and just needs to be persisted.
    /// </summary>
    public static async Task WriteStateSafeAsync<TState>(this IPersistentState<TState> state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                await state.WriteStateAsync(cancellationToken);
                return;
            }
            catch (InconsistentStateException)
            {
                await state.ReadStateAsync(cancellationToken);
            }
            catch (Exception ex) when (IsEtagMismatch(ex))
            {
                await state.ReadStateAsync(cancellationToken);
            }
        }

        // Final attempt - let it throw if still failing
        await state.WriteStateAsync(cancellationToken);
    }

    /// <summary>
    /// Safely clears state with retry on ETag conflicts.
    /// </summary>
    public static async Task ClearStateSafeAsync<TState>(this IPersistentState<TState> state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                await state.ClearStateAsync(cancellationToken);
                return;
            }
            catch (InconsistentStateException)
            {
                await state.ReadStateAsync(cancellationToken);
            }
            catch (Exception ex) when (IsEtagMismatch(ex))
            {
                await state.ReadStateAsync(cancellationToken);
            }
        }

        // Final attempt - let it throw if still failing
        await state.ClearStateAsync(cancellationToken);
    }

    private static bool IsEtagMismatch(Exception ex)
    {
        // Check for MemoryStorageEtagMismatchException without taking a hard dependency
        return ex.GetType().Name == "MemoryStorageEtagMismatchException";
    }
}
