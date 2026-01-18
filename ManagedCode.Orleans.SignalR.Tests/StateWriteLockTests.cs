using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class StateWriteLockTests(SmokeClusterFixture cluster)
{
    private readonly SmokeClusterFixture _cluster = cluster;

    [Fact]
    public async Task ReentrantStateWritesAreSerializedAsync()
    {
        var grain = _cluster.Cluster.GrainFactory.GetGrain<IStateWriteLockGrain>("state-write-lock");
        await grain.ResetAsync();

        const int writeCount = 12;
        const int delayMilliseconds = 50;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);

        var tasks = Enumerable.Range(0, writeCount)
            .Select(_ => grain.WriteWithDelayAsync(delay))
            .ToArray();

        await Task.WhenAll(tasks);

        (await grain.GetMaxConcurrentWritesAsync()).ShouldBe(1);
        (await grain.GetWriteCountAsync()).ShouldBe(writeCount);
    }
}
