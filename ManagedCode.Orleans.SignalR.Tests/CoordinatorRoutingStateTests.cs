using System.Diagnostics.CodeAnalysis;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class CoordinatorRoutingStateTests(SmokeClusterFixture cluster)
{
    private readonly SmokeClusterFixture _cluster = cluster;

    [Fact]
    public async Task SendingToUnknownConnectionsShouldNotGrowCoordinatorStateAsync()
    {
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<UnknownConnectionRoutingHub>(_cluster.Cluster.Client);
        var baseline = await coordinator.GetPartitionCount();
        var message = new InvocationMessage("unknown-connection", []);
        var connectionIds = Enumerable.Range(0, TestDefaults.ConnectionsPerPartitionHint * 8)
            .Select(index => $"unknown-connection-{index}")
            .ToArray();

        foreach (var connectionId in connectionIds)
        {
            (await coordinator.SendToConnection(message, connectionId)).ShouldBeFalse();
        }

        await coordinator.SendToConnections(message, connectionIds);
        await coordinator.SendToAllExcept(message, connectionIds);

        (await coordinator.GetPartitionCount()).ShouldBe(baseline);
    }

    [Fact]
    public async Task SendingToUnknownGroupsShouldNotGrowCoordinatorStateAsync()
    {
        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<UnknownGroupRoutingHub>(_cluster.Cluster.Client);
        var baseline = await coordinator.GetPartitionCount();
        var message = new InvocationMessage("unknown-group", []);
        var groupNames = Enumerable.Range(0, TestDefaults.GroupsPerPartitionHint * 8)
            .Select(index => $"unknown-group-{index}")
            .ToArray();

        foreach (var groupName in groupNames)
        {
            await coordinator.SendToGroup(groupName, message);
            await coordinator.SendToGroupExcept(groupName, message, []);
        }

        await coordinator.SendToGroups(groupNames, message);

        (await coordinator.GetPartitionCount()).ShouldBe(baseline);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used as a closed generic hub identity for grain-key tests.")]
    private sealed class UnknownConnectionRoutingHub : Hub;

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used as a closed generic hub identity for grain-key tests.")]
    private sealed class UnknownGroupRoutingHub : Hub;
}
