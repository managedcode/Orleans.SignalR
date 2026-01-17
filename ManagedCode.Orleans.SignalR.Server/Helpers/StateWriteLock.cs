using System;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCode.Orleans.SignalR.Server.Helpers;

internal sealed class StateWriteLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _semaphore.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _semaphore.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
