namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;

public interface IStateWriteLockGrain : IGrainWithStringKey
{
    Task WriteWithDelayAsync(TimeSpan delay);
    Task<int> GetMaxConcurrentWritesAsync();
    Task<int> GetWriteCountAsync();
    Task ResetAsync();
}
