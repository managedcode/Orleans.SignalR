using System;
using System.Globalization;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public sealed class GrainPersistenceTests
{
    private readonly SmokeClusterFixture _cluster;
    private readonly ITestOutputHelper _output;

    public GrainPersistenceTests(SmokeClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
    }

    [Fact]
    public async Task ConnectionPartitionPersistsConnectionStateAfterDeactivation()
    {
        var client = _cluster.Cluster.Client;
        var management = client.GetGrain<IManagementGrain>(0);
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(client);

        var connectionId = $"conn-{Guid.NewGuid():N}";
        var partitionId = await coordinator.GetPartitionForConnection(connectionId);
        var partition = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, partitionId);

        var observer = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(_ => Task.CompletedTask));

        try
        {
            await partition.AddConnection(connectionId, observer);

            _output.WriteLine($"Evicting partition {partitionId} for connection {connectionId}.");
            await management.ForceActivationCollection(TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await AssertRoutedAsync(
                () => partition.SendToConnection(new InvocationMessage("state-check", Array.Empty<object?>()), connectionId),
                $"partition {partitionId} connection {connectionId} after eviction");
        }
        finally
        {
            await partition.RemoveConnection(connectionId, observer);
        }
    }

    [Fact]
    public async Task ConnectionPartitionRetainsMultipleConnectionsThroughSequentialEvictions()
    {
        var client = _cluster.Cluster.Client;
        var management = client.GetGrain<IManagementGrain>(0);
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(client);

        var connectionA = $"conn-A-{Guid.NewGuid():N}";
        var partitionAId = await coordinator.GetPartitionForConnection(connectionA);
        var partitionA = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, partitionAId);

        var connectionB = await FindConnectionInDifferentPartitionAsync(coordinator, partitionAId);
        var partitionBId = await coordinator.GetPartitionForConnection(connectionB);
        var partitionB = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, partitionBId);

        var observerA = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(_ => Task.CompletedTask));
        var observerB = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(_ => Task.CompletedTask));

        try
        {
            await partitionA.AddConnection(connectionA, observerA);

            _output.WriteLine("First eviction covering initial connection.");
            await management.ForceActivationCollection(TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await AssertRoutedAsync(
                () => partitionA.SendToConnection(new InvocationMessage("cycle-one", Array.Empty<object?>()), connectionA),
                $"partition {partitionAId} connection {connectionA} after first eviction");

            await partitionB.AddConnection(connectionB, observerB);

            _output.WriteLine("Second eviction covering both active connections.");
            await management.ForceActivationCollection(TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await AssertRoutedAsync(
                () => partitionA.SendToConnection(new InvocationMessage("cycle-two-A", Array.Empty<object?>()), connectionA),
                $"partition {partitionAId} connection {connectionA} after second eviction");
            await AssertRoutedAsync(
                () => partitionB.SendToConnection(new InvocationMessage("cycle-two-B", Array.Empty<object?>()), connectionB),
                $"partition {partitionBId} connection {connectionB} after second eviction");
        }
        finally
        {
            await partitionA.RemoveConnection(connectionA, observerA);
            await partitionB.RemoveConnection(connectionB, observerB);
        }
    }

    [Fact]
    public async Task ConnectionsForDistinctHubsDoNotInterfere()
    {
        var client = _cluster.Cluster.Client;
        var sharedConnectionId = $"conn-shared-{Guid.NewGuid():N}";

        var coordinatorA = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(client);
        var coordinatorB = NameHelperGenerator.GetConnectionCoordinatorGrain<InterfaceTestHub>(client);
        var coordinatorC = NameHelperGenerator.GetConnectionCoordinatorGrain<StressTestHub>(client);

        var partitionAId = await coordinatorA.GetPartitionForConnection(sharedConnectionId);
        var partitionBId = await coordinatorB.GetPartitionForConnection(sharedConnectionId);
        var partitionCId = await coordinatorC.GetPartitionForConnection(sharedConnectionId);

        var partitionA = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, partitionAId);
        var partitionB = NameHelperGenerator.GetConnectionPartitionGrain<InterfaceTestHub>(client, partitionBId);
        var partitionC = NameHelperGenerator.GetConnectionPartitionGrain<StressTestHub>(client, partitionCId);

        var routedA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routedB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routedC = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var observerA = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedA.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        }));

        var observerB = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedB.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        }));
        var observerC = client.CreateObjectReference<ISignalRObserver>(new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedC.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        }));

        try
        {
            await partitionA.AddConnection(sharedConnectionId, observerA);
            await partitionB.AddConnection(sharedConnectionId, observerB);
            await partitionC.AddConnection(sharedConnectionId, observerC);

            await AssertRoutedAsync(
                () => partitionA.SendToConnection(new InvocationMessage("hubA", Array.Empty<object?>()), sharedConnectionId),
                $"partition {partitionAId} connection {sharedConnectionId} (hub A)");
            await AssertRoutedAsync(
                () => partitionB.SendToConnection(new InvocationMessage("hubB", Array.Empty<object?>()), sharedConnectionId),
                $"partition {partitionBId} connection {sharedConnectionId} (hub B)");
            await AssertRoutedAsync(
                () => partitionC.SendToConnection(new InvocationMessage("hubC", Array.Empty<object?>()), sharedConnectionId),
                $"partition {partitionCId} connection {sharedConnectionId} (hub C)");

            (await routedA.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("hubA");
            (await routedB.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("hubB");
            (await routedC.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("hubC");
        }
        finally
        {
            await partitionA.RemoveConnection(sharedConnectionId, observerA);
            await partitionB.RemoveConnection(sharedConnectionId, observerB);
            await partitionC.RemoveConnection(sharedConnectionId, observerC);
        }
    }

    private static async Task AssertRoutedAsync(Func<Task<bool>> sendAction, string reason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await sendAction().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        (await sendAction().ConfigureAwait(false)).ShouldBeTrue($"Routing failed for {reason} after retries.");
    }

    private static async Task<string> FindConnectionInDifferentPartitionAsync(
        ISignalRConnectionCoordinatorGrain coordinator,
        int excludedPartition)
    {
        for (var i = 0; i < 1024; i++)
        {
            var candidate = $"conn-B-{Guid.NewGuid():N}";
            var partition = await coordinator.GetPartitionForConnection(candidate);
            if (partition != excludedPartition)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to find a connection id that hashes to a different partition.");
    }
}
