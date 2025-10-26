using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class CoordinatorScalingTests
{
    private readonly SmokeClusterFixture _cluster;
    private readonly ITestOutputHelper _output;

    public CoordinatorScalingTests(SmokeClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
    }

    [Fact]
    public async Task ConnectionCoordinator_Scales_With_Connection_Load()
    {
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(_cluster.Cluster.Client);
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

        var reset = await coordinator.GetPartitionCount();
        reset.ShouldBe(baseline);
    }

    [Fact]
    public async Task GroupCoordinator_Scales_With_Group_Load()
    {
        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(_cluster.Cluster.Client);
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

        var reset = await coordinator.GetPartitionCount();
        reset.ShouldBe(baseline);
    }
}
