using System.Diagnostics;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class CoordinatorScalingTests(SmokeClusterFixture cluster, ITestOutputHelper output)
{
    private readonly SmokeClusterFixture _cluster = cluster;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task ConnectionCoordinator_Scales_With_Connection_Load()
    {
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<ScalingTestHub>(_cluster.Cluster.Client);
        var baseline = await coordinator.GetPartitionCount();
        baseline.ShouldBeGreaterThan(0);

        var required = TestDefaults.ConnectionsPerPartitionHint * 4 + 1;
        var connections = Enumerable.Range(0, required)
            .Select(index => $"scale-conn-{Guid.NewGuid():N}-{index}")
            .ToArray();

        foreach (var id in connections)
        {
            await coordinator.GetPartitionForConnection(id);
        }

        var scaled = await coordinator.GetPartitionCount();
        _output.WriteLine($"Tracked {connections.Length} connections caused partition count to scale from {baseline} to {scaled}.");
        scaled.ShouldBeGreaterThan(baseline);

        foreach (var id in connections)
        {
            await coordinator.NotifyConnectionRemoved(id);
        }

        var reset = await WaitForPartitionCountAsync(coordinator.GetPartitionCount, baseline, _output);
        reset.ShouldBe(baseline);
    }

    [Fact]
    public async Task GroupCoordinator_Scales_With_Group_Load()
    {
        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<ScalingTestHub>(_cluster.Cluster.Client);
        var baseline = await coordinator.GetPartitionCount();
        baseline.ShouldBeGreaterThan(0);

        var required = TestDefaults.GroupsPerPartitionHint * 4 + 1;
        var groups = Enumerable.Range(0, required)
            .Select(index => $"scale-group-{index}")
            .ToArray();

        foreach (var group in groups)
        {
            await coordinator.GetPartitionForGroup(group);
        }

        var scaled = await coordinator.GetPartitionCount();
        _output.WriteLine($"Tracked {groups.Length} groups caused partition count to scale from {baseline} to {scaled}.");
        scaled.ShouldBeGreaterThan(baseline);

        foreach (var group in groups)
        {
            await coordinator.NotifyGroupRemoved(group);
        }

        var reset = await WaitForPartitionCountAsync(coordinator.GetPartitionCount, baseline, _output);
        reset.ShouldBe(baseline);
    }

    private static async Task<int> WaitForPartitionCountAsync(Func<Task<int>> accessor, int expected, ITestOutputHelper output)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var poll = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();
        var current = await accessor();

        while (current != expected && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(poll);
            current = await accessor();
        }

        if (current != expected)
        {
            output.WriteLine($"Partition count not reset to {expected} within {timeout.TotalSeconds}s. Current={current}.");
        }

        return current;
    }
}
