using System.Collections.Concurrent;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SharedStorageCluster))]
public sealed class GrainPersistenceTests(SharedStorageClusterFixture cluster, ITestOutputHelper output)
{
    private readonly SharedStorageClusterFixture _cluster = cluster;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task ObserverDeliveryFeedbackOpensCircuitAndRecoveryReplaysBufferedMessageAsync()
    {
        var client = _cluster.Cluster.Client;
        var connectionId = $"feedback-{Guid.NewGuid():N}";
        var partition = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, 0);
        var delivered = new TaskCompletionSource<InvocationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var localObserver = new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                delivered.TrySetResult(invocation);
            }

            return Task.CompletedTask;
        });
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);
        var observerId = observer.GetPrimaryKeyString();

        try
        {
            await partition.AddConnection(connectionId, observer);
            for (var failure = 0; failure < 3; failure++)
            {
                await partition.AsReference<IObserverDeliveryFailureReporter>().ReportObserverDeliveryFailure(
                    connectionId,
                    observerId,
                    nameof(IOException),
                    "simulated SignalR WriteAsync failure");
            }

            (await partition.SendToConnection(
                new InvocationMessage("buffered-during-open-circuit", Array.Empty<object?>()),
                connectionId)).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            delivered.Task.IsCompleted.ShouldBeFalse("An open observer circuit must block immediate delivery.");

            await partition.AddConnection(connectionId, observer);
            var replayed = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            replayed.Target.ShouldBe("buffered-during-open-circuit");
        }
        finally
        {
            await partition.RemoveConnection(connectionId, observer);
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    [Fact]
    public async Task ObserverDeliveryFailureIsReportedOnlyToTheSendingGrainAsync()
    {
        var client = _cluster.Cluster.Client;
        var connectionId = $"feedback-source-{Guid.NewGuid():N}";
        var userId = $"feedback-user-{Guid.NewGuid():N}";
        var partition = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, 0);
        var user = NameHelperGenerator.GetSignalRUserGrain<SimpleTestHub>(client, userId);
        var failureReports = 0;
        var allFailuresReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var userDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscription? subscription = null;
        using var localObserver = new SignalRObserver(
            message => message is InvocationMessage { Target: "partition-failure" }
                ? Task.FromException(new IOException("write failed"))
                : CompleteUserDeliveryAsync(message, userDelivery),
            async (source, exception) =>
            {
                await ObserverDeliveryFeedback.ReportFailureAsync(
                    client,
                    source,
                    connectionId,
                    subscription,
                    exception,
                    NullLogger.Instance);
                if (Interlocked.Increment(ref failureReports) == 3)
                {
                    allFailuresReported.TrySetResult();
                }
            },
            source => ObserverDeliveryFeedback.RestoreAsync(
                client,
                source,
                connectionId,
                subscription,
                NullLogger.Instance));
        subscription = new Subscription(localObserver);
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);
        subscription.SetReference(observer);

        try
        {
            await partition.AddConnection(connectionId, observer);
            await user.AddConnection(connectionId, observer);

            for (var failure = 0; failure < 3; failure++)
            {
                (await partition.SendToConnection(
                    new InvocationMessage("partition-failure", Array.Empty<object?>()),
                    connectionId)).ShouldBeTrue();
            }

            await allFailuresReported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await user.SendToUser(new InvocationMessage("user-probe", Array.Empty<object?>()));
            await userDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await partition.RemoveConnection(connectionId, observer);
            await user.RemoveConnection(connectionId, observer);
            subscription.DisposeReference(client.DeleteObjectReference<ISignalRObserver>);
        }
    }

    [Fact]
    public async Task OfflineUserMessageSurvivesReactivationAndIsRemovedOnlyAfterDeliveryAcknowledgementAsync()
    {
        var client = _cluster.Cluster.Client;
        var management = client.GetGrain<IManagementGrain>(0);
        var userId = $"offline/user?{Guid.NewGuid():N}";
        var connectionId = $"offline-connection-{Guid.NewGuid():N}";
        var user = NameHelperGenerator.GetSignalRUserGrain<SimpleTestHub>(client, userId);
        var message = new InvocationMessage("offline-durable", ["payload"]);
        var firstDelivery = new TaskCompletionSource<InvocationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await user.SendToUser(message);
        await management.ForceActivationCollection(TimeSpan.Zero);
        await Task.Delay(TimeSpan.FromSeconds(2));

        using var firstLocalObserver = new SignalRObserver(
            delivered =>
            {
                if (delivered is InvocationMessage invocation)
                {
                    firstDelivery.TrySetResult(invocation);
                }

                return Task.CompletedTask;
            },
            onDeliveryAcknowledged: (deliveryId, userGrainKey) =>
            {
                userGrainKey.ShouldBe(user.GetPrimaryKeyString());
                return user.AcknowledgeMessage(deliveryId);
            });
        var firstObserver = client.CreateObjectReference<ISignalRObserver>(firstLocalObserver);

        try
        {
            await user.AddConnection(connectionId, firstObserver);
            await user.RequestMessage();

            var delivered = await firstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10));
            delivered.Target.ShouldBe("offline-durable");
            delivered.Arguments.ShouldBe(["payload"]);

            await user.RemoveConnection(connectionId, firstObserver);
            await management.ForceActivationCollection(TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromSeconds(2));

            var duplicateDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var secondLocalObserver = new SignalRObserver(_ =>
            {
                duplicateDelivery.TrySetResult();
                return Task.CompletedTask;
            });
            var secondObserver = client.CreateObjectReference<ISignalRObserver>(secondLocalObserver);

            try
            {
                await user.AddConnection(connectionId, secondObserver);
                await user.RequestMessage();
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                duplicateDelivery.Task.IsCompleted.ShouldBeFalse("Acknowledged offline messages must not replay again.");
                await user.RemoveConnection(connectionId, secondObserver);
            }
            finally
            {
                client.DeleteObjectReference<ISignalRObserver>(secondObserver);
            }
        }
        finally
        {
            client.DeleteObjectReference<ISignalRObserver>(firstObserver);
        }
    }

    [Fact]
    public async Task OfflineUserQueueDropsOldestMessagesAtTheConfiguredBoundAsync()
    {
        const int queueLimit = 100;
        var client = _cluster.Cluster.Client;
        var user = NameHelperGenerator.GetSignalRUserGrain<SimpleTestHub>(
            client,
            $"bounded-user-{Guid.NewGuid():N}");

        for (var index = 0; index < queueLimit + 2; index++)
        {
            await user.SendToUser(new InvocationMessage($"bounded-{index}", Array.Empty<object?>()));
        }

        var connectionId = $"bounded-connection-{Guid.NewGuid():N}";
        var deliveredTargets = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var localObserver = new SignalRObserver(
            message =>
            {
                if (message is InvocationMessage invocation &&
                    deliveredTargets.TryAdd(invocation.Target, 0) &&
                    deliveredTargets.Count == queueLimit)
                {
                    allDelivered.TrySetResult();
                }

                return Task.CompletedTask;
            },
            onDeliveryAcknowledged: (deliveryId, _) => user.AcknowledgeMessage(deliveryId));
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);

        try
        {
            await user.AddConnection(connectionId, observer);
            await user.RequestMessage();
            await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            deliveredTargets.Count.ShouldBe(queueLimit);
            deliveredTargets.ContainsKey("bounded-0").ShouldBeFalse();
            deliveredTargets.ContainsKey("bounded-1").ShouldBeFalse();
            deliveredTargets.ContainsKey("bounded-2").ShouldBeTrue();
            deliveredTargets.ContainsKey("bounded-101").ShouldBeTrue();
        }
        finally
        {
            await user.RemoveConnection(connectionId, observer);
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    [Fact]
    public async Task ExpiredOfflineUserMessageIsNotReplayedAsync()
    {
        var client = _cluster.Cluster.Client;
        var user = NameHelperGenerator.GetSignalRUserGrain<SimpleTestHub>(
            client,
            $"expired-user-{Guid.NewGuid():N}");
        await user.SendToUser(new InvocationMessage("expired-message", Array.Empty<object?>()));
        await Task.Delay(TestDefaults.MessageRetention + TimeSpan.FromMilliseconds(500));

        var connectionId = $"expired-connection-{Guid.NewGuid():N}";
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var localObserver = new SignalRObserver(_ =>
        {
            delivered.TrySetResult();
            return Task.CompletedTask;
        });
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);

        try
        {
            await user.AddConnection(connectionId, observer);
            await user.RequestMessage();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            delivered.Task.IsCompleted.ShouldBeFalse("Expired offline messages must be removed instead of replayed.");
        }
        finally
        {
            await user.RemoveConnection(connectionId, observer);
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    [Fact]
    public async Task InvocationCompletionRemainsObservableAfterReactivationAsync()
    {
        var client = _cluster.Cluster.Client;
        var management = client.GetGrain<IManagementGrain>(0);
        var connectionId = $"invocation-connection-{Guid.NewGuid():N}";
        var invocationId = $"invocation/{Guid.NewGuid():N}";
        var invocation = NameHelperGenerator.GetInvocationGrain<SimpleTestHub>(client, invocationId);
        var completion = new CompletionMessage(invocationId, error: null, result: "durable-result", hasResult: true);

        try
        {
            await invocation.AddInvocation(null, new InvocationInfo(connectionId, invocationId, typeof(GrainPersistenceTests)));
            await invocation.TryCompleteResult(connectionId, completion);

            await management.ForceActivationCollection(TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromSeconds(2));
            var restoredReturnType = await invocation.TryGetReturnType();
            restoredReturnType.Result.ShouldBeTrue("Invocation registration was not restored after reactivation.");
            restoredReturnType.GetReturnType().ShouldBe(
                typeof(GrainPersistenceTests),
                "The persisted assembly-qualified return type was not restored after reactivation.");

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CompletionMessage? observed = null;
            await foreach (var chunk in invocation.WaitForCompletion(cancellation.Token)
                               .WithCancellation(cancellation.Token))
            {
                if (chunk.TryGetResult(out var result))
                {
                    observed = result;
                    break;
                }
            }

            observed.ShouldNotBeNull();
            observed.InvocationId.ShouldBe(invocationId);
            observed.Result.ShouldBe("durable-result");
        }
        finally
        {
            await invocation.RemoveInvocation();
        }
    }

    [Fact]
    public async Task EmptyInvocationStateDoesNotReportAReturnTypeAsync()
    {
        var invocation = NameHelperGenerator.GetInvocationGrain<SimpleTestHub>(
            _cluster.Cluster.Client,
            $"empty-invocation-{Guid.NewGuid():N}");

        var returnType = await invocation.TryGetReturnType();

        returnType.Result.ShouldBeFalse();
        returnType.Type.ShouldBeNull();
    }

    [Fact]
    public async Task InvocationCompletionStreamDoesNotBlockCompletionTurnAsync()
    {
        var client = _cluster.Cluster.Client;
        var connectionId = $"stream-connection-{Guid.NewGuid():N}";
        var invocationId = $"stream-invocation-{Guid.NewGuid():N}";
        var invocation = NameHelperGenerator.GetInvocationGrain<SimpleTestHub>(client, invocationId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await invocation.AddInvocation(null, new InvocationInfo(connectionId, invocationId, typeof(string)));
            var terminalTask = ReadTerminalCompletionAsync(invocation, cancellation.Token);
            await Task.Yield();

            await invocation.TryCompleteResult(
                connectionId,
                new CompletionMessage(invocationId, error: null, result: "stream-result", hasResult: true));

            var terminal = await terminalTask;
            terminal.InvocationId.ShouldBe(invocationId);
            terminal.Result.ShouldBe("stream-result");
        }
        finally
        {
            await invocation.RemoveInvocation();
        }
    }

    [Fact]
    public async Task ConnectionPartitionPersistsConnectionStateAfterDeactivationAsync()
    {
        var client = _cluster.Cluster.Client;
        var management = client.GetGrain<IManagementGrain>(0);
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(client);

        var connectionId = $"conn-{Guid.NewGuid():N}";
        var partitionId = await coordinator.GetPartitionForConnection(connectionId);
        var partition = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(client, partitionId);

        using var localObserver = new SignalRObserver(_ => Task.CompletedTask);
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);

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
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    [Fact]
    public async Task ConnectionPartitionRetainsMultipleConnectionsThroughSequentialEvictionsAsync()
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

        using var localObserverA = new SignalRObserver(_ => Task.CompletedTask);
        using var localObserverB = new SignalRObserver(_ => Task.CompletedTask);
        var observerA = client.CreateObjectReference<ISignalRObserver>(localObserverA);
        var observerB = client.CreateObjectReference<ISignalRObserver>(localObserverB);

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
            client.DeleteObjectReference<ISignalRObserver>(observerA);
            client.DeleteObjectReference<ISignalRObserver>(observerB);
        }
    }

    [Fact]
    public async Task ConnectionsForDistinctHubsDoNotInterfereAsync()
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

        using var localObserverA = new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedA.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        });

        using var localObserverB = new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedB.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        });
        using var localObserverC = new SignalRObserver(message =>
        {
            if (message is InvocationMessage invocation)
            {
                routedC.TrySetResult(invocation.Target!);
            }

            return Task.CompletedTask;
        });
        var observerA = client.CreateObjectReference<ISignalRObserver>(localObserverA);
        var observerB = client.CreateObjectReference<ISignalRObserver>(localObserverB);
        var observerC = client.CreateObjectReference<ISignalRObserver>(localObserverC);

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
            client.DeleteObjectReference<ISignalRObserver>(observerA);
            client.DeleteObjectReference<ISignalRObserver>(observerB);
            client.DeleteObjectReference<ISignalRObserver>(observerC);
        }
    }

    [Fact]
    public async Task GroupCoordinatorBatchMethodsShouldTrackMembershipAcrossTouchedPartitionsAsync()
    {
        var client = _cluster.Cluster.Client;
        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(client);
        var connectionId = $"group-batch-{Guid.NewGuid():N}";
        var groupNames = new[]
        {
            $"group-alpha-{Guid.NewGuid():N}",
            $"group-beta-{Guid.NewGuid():N}",
            $"group-gamma-{Guid.NewGuid():N}",
            $"group-delta-{Guid.NewGuid():N}"
        };

        using var localObserver = new SignalRObserver(_ => Task.CompletedTask);
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);

        try
        {
            var addedPartitions = await coordinator.AddConnectionToGroups(groupNames, connectionId, observer);
            addedPartitions.ShouldNotBeEmpty();

            var expectedPartitions = (await Task.WhenAll(groupNames.Select(coordinator.GetPartitionForGroup)))
                .Distinct()
                .OrderBy(partitionId => partitionId)
                .ToArray();
            addedPartitions.OrderBy(partitionId => partitionId).ShouldBe(expectedPartitions);

            foreach (var partitionId in expectedPartitions)
            {
                var partition = NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(client, partitionId);
                (await partition.HasConnection(connectionId)).ShouldBeTrue();
            }

            var removedPartitions = await coordinator.RemoveConnectionFromGroups(groupNames, connectionId, observer);
            removedPartitions.OrderBy(partitionId => partitionId).ShouldBe(expectedPartitions);

            foreach (var partitionId in expectedPartitions)
            {
                var partition = NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(client, partitionId);
                (await partition.HasConnection(connectionId)).ShouldBeFalse();
            }
        }
        finally
        {
            await coordinator.RemoveConnectionFromGroups(groupNames, connectionId, observer);
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    [Fact]
    public async Task GroupCoordinatorBatchRemoveShouldCleanupPartitionWhenAssignmentMetadataIsMissingAsync()
    {
        var client = _cluster.Cluster.Client;
        var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(client);
        var connectionId = $"group-cleanup-{Guid.NewGuid():N}";
        var groupName = $"group-drift-{Guid.NewGuid():N}";
        using var localObserver = new SignalRObserver(_ => Task.CompletedTask);
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);

        var partitionId = await coordinator.GetPartitionForGroup(groupName);
        var partition = NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(client, partitionId);

        try
        {
            var addedPartitions = await coordinator.AddConnectionToGroups([groupName], connectionId, observer);
            addedPartitions.ShouldContain(partitionId);
            (await partition.HasConnection(connectionId)).ShouldBeTrue();

            await coordinator.NotifyGroupRemoved(groupName);
            (await partition.HasConnection(connectionId)).ShouldBeTrue();

            var removedPartitions = await coordinator.RemoveConnectionFromGroups([groupName], connectionId, observer);
            removedPartitions.ShouldContain(partitionId);
            (await partition.HasConnection(connectionId)).ShouldBeFalse();
        }
        finally
        {
            await coordinator.RemoveConnectionFromGroups([groupName], connectionId, observer);
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }

    private static async Task<CompletionMessage> ReadTerminalCompletionAsync(
        ISignalRInvocationGrain invocation,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in invocation.WaitForCompletion(cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            if (chunk.TryGetResult(out var completion))
            {
                return completion;
            }
        }

        throw new InvalidOperationException("Invocation completion stream ended without a terminal result.");
    }

    private static async Task AssertRoutedAsync(Func<Task<bool>> sendAction, string reason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await sendAction())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        (await sendAction()).ShouldBeTrue($"Routing failed for {reason} after retries.");
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

    private static Task CompleteUserDeliveryAsync(HubMessage message, TaskCompletionSource completion)
    {
        if (message is InvocationMessage { Target: "user-probe" })
        {
            completion.TrySetResult();
        }

        return Task.CompletedTask;
    }
}
