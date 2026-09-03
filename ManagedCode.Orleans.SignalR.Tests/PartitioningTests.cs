using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class PartitioningTests
{
    private static readonly TimeSpan _waitInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _logInterval = TimeSpan.FromSeconds(1);
    private const int ApplicationInstances = 4;

    private readonly ITestOutputHelper _testOutputHelper;
    private readonly SmokeClusterFixture _siloCluster;
    private readonly IReadOnlyList<TestWebApplication> _apps;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();

    public PartitioningTests(SmokeClusterFixture siloCluster, ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _siloCluster = siloCluster;
        _loggerAccessor.Output = testOutputHelper;
        _apps = Enumerable.Range(0, ApplicationInstances)
            .Select(index =>
            {
                var port = 8083 + index;
                var app = new TestWebApplication(_siloCluster, port, loggerAccessor: _loggerAccessor);
                _testOutputHelper.WriteLine($"Provisioned TestWebApplication #{index} at http://localhost:{port}.");
                return app;
            })
            .ToArray();
    }

    [Fact]
    public async Task DefaultConfigurationShouldUseConnectionPartitioningAsync()
    {
        // Arrange
        var connection = _apps[0].CreateSignalRClient(nameof(PartitionTestHub));
        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);

        // Act - Send a message (uses partitioned coordinator by default)
        var result = await connection.InvokeAsync<int>("All");

        // Assert
        result.ShouldBeGreaterThan(0);

        var coordinatorGrain = NameHelperGenerator.GetConnectionCoordinatorGrain<PartitionTestHub>(_siloCluster.Cluster.Client);
        var partitionCount = await coordinatorGrain.GetPartitionCount();
        var defaultPartitions = (int)new OrleansSignalROptions().ConnectionPartitionCount;

        defaultPartitions.ShouldBeGreaterThan(1);
        partitionCount.ShouldBe(defaultPartitions);

        // Cleanup
        await connection.StopAsync();
    }

    [Fact]
    public async Task DefaultGroupConfigurationShouldUseGroupPartitioningAsync()
    {
        // Arrange
        const int groupCount = 100;
        var connection = _apps[1].CreateSignalRClient(nameof(SimpleTestHub));
        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);

        // Act - Add connection to multiple groups
        var addTasks = Enumerable.Range(0, groupCount)
            .Select(i => connection.InvokeAsync("AddToGroup", $"group_{i}"))
            .ToArray();
        await Task.WhenAll(addTasks);

        // Assert - Send messages to different groups
        var sendTasks = Enumerable.Range(0, groupCount)
            .Select(i => connection.InvokeAsync("GroupSendAsync", $"group_{i}", $"Hello group_{i}!"))
            .ToArray();
        await Task.WhenAll(sendTasks);

        // Verify group coordinator is working with default configuration (partitioning enabled)
        var groupCoordinatorGrain = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
        var groupPartitionCount = await groupCoordinatorGrain.GetPartitionCount();

        var defaultGroupPartitions = (int)new OrleansSignalROptions().GroupPartitionCount;
        defaultGroupPartitions.ShouldBeGreaterThan(1);
        groupPartitionCount.ShouldBeGreaterThanOrEqualTo(defaultGroupPartitions);
        (groupPartitionCount & (groupPartitionCount - 1)).ShouldBe(0);

        // Cleanup
        await connection.StopAsync();
    }

    [Fact]
    public async Task PartitionedSendToAllShouldReachAllConnectionsAsync()
    {
        // Arrange
        const int connectionsPerApp = 100;
        var totalConnections = connectionsPerApp * _apps.Count;
        var connections = new HubConnection[totalConnections];
        var connectionLabels = new string[totalConnections];
        var receivedMessages = new TaskCompletionSource<string>[totalConnections];
        var startedPerApp = new int[_apps.Count];

        try
        {
            for (var i = 0; i < totalConnections; i++)
            {
                var appIndex = i % _apps.Count;
                var app = _apps[appIndex];
                var label = $"app#{appIndex}-conn#{i}";

                connections[i] = app.CreateSignalRClient(nameof(SimpleTestHub));
                receivedMessages[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                connectionLabels[i] = label;

                var index = i;
                connections[i].On<string>("SendAll", message =>
                {
                    receivedMessages[index].TrySetResult(message);
                });
                await connections[i].StartAsync();
                await connections[i].InvokeAsync<int>("Plus", 0, 0);
                startedPerApp[appIndex]++;
            }

            var startSummary = string.Join(", ",
                startedPerApp.Select((count, index) => $"app#{index}:{count}"));
            _testOutputHelper.WriteLine(
                $"Started {totalConnections} connections across {_apps.Count} apps ({startSummary}).");

            // Act
            await connections[0].InvokeAsync("All");
            _testOutputHelper.WriteLine(
                $"Broadcast invoked via All() from {connectionLabels[0]} (connection id {connections[0].ConnectionId}). Awaiting delivery to {totalConnections} connections across {_apps.Count} apps.");

            // Assert
            var allTasks = receivedMessages.Select(tcs => tcs.Task).ToArray();
            var completed = await WaitUntilAsync(
                "broadcast delivery to all connections",
                () => Task.FromResult(allTasks.Count(t => t.IsCompletedSuccessfully) == totalConnections),
                progress: () =>
                {
                    var receivedByApp = Enumerable.Range(0, _apps.Count)
                        .Select(appIndex =>
                        {
                            var delivered = allTasks
                                .Select((task, connectionIndex) => (task, connectionIndex))
                                .Count(tuple => tuple.task.IsCompletedSuccessfully && (tuple.connectionIndex % _apps.Count) == appIndex);
                            return $"app#{appIndex}:{delivered}/{connectionsPerApp}";
                        });

                    var status = string.Join(", ", receivedByApp);
                    return Task.FromResult(
                        $"received {allTasks.Count(t => t.IsCompletedSuccessfully)}/{totalConnections} messages ({status})");
                },
                timeout: TimeSpan.FromSeconds(15));

            completed.ShouldBeTrue("Not all connections observed the broadcast within the expected time.");

            var completedCount = allTasks.Count(t => t.IsCompletedSuccessfully);
            completedCount.ShouldBe(totalConnections);
        }
        finally
        {
            for (var i = 0; i < connections.Length; i++)
            {
                var connection = connections[i];
                if (connection is null)
                {
                    continue;
                }

                var label = connectionLabels[i] ?? $"conn#{i}";

                try
                {
                    await connection.StopAsync();
                }
                catch (Exception ex)
                {
                    _testOutputHelper.WriteLine($"[{label}] Failed to stop connection {connection.ConnectionId}: {ex.Message}");
                }

                await connection.DisposeAsync();
                _testOutputHelper.WriteLine($"[{label}] disposed connection {connection.ConnectionId}.");
            }
        }
    }

    [Fact]
    public async Task PartitionedSendToGroupShouldOnlyReachGroupMembersAsync()
    {
        // Arrange
        var connection1 = _apps[0].CreateSignalRClient(nameof(SimpleTestHub));
        var connection2 = _apps[1].CreateSignalRClient(nameof(SimpleTestHub));
        var connection3 = _apps[3 % _apps.Count].CreateSignalRClient(nameof(SimpleTestHub));

        var messages1 = new List<string>();
        var messages2 = new List<string>();
        var messages3 = new List<string>();
        var received1 = new TaskCompletionSource<bool>();
        var received2 = new TaskCompletionSource<bool>();

        connection1.On<string>("SendAll", msg =>
        {
            messages1.Add(msg);
            if (msg.Contains("send message:"))
            {
                received1.TrySetResult(true);
            }
        });
        connection2.On<string>("SendAll", msg =>
        {
            messages2.Add(msg);
            if (msg.Contains("send message:"))
            {
                received2.TrySetResult(true);
            }
        });
        connection3.On<string>("SendAll", messages3.Add);

        await connection1.StartAsync();
        _testOutputHelper.WriteLine($"[group] app#0 started connection {connection1.ConnectionId}.");
        await connection2.StartAsync();
        _testOutputHelper.WriteLine($"[group] app#1 started connection {connection2.ConnectionId}.");
        await connection3.StartAsync();
        _testOutputHelper.WriteLine($"[group] app#3 started connection {connection3.ConnectionId} (not in group).");

        // Add only connection1 and connection2 to the group
        await connection1.InvokeAsync("AddToGroup", "testGroup");
        await connection2.InvokeAsync("AddToGroup", "testGroup");
        _testOutputHelper.WriteLine($"[group] Added connections {connection1.ConnectionId} and {connection2.ConnectionId} to 'testGroup'.");

        await Task.Delay(500); // Give time for group operations

        // Act
        await connection1.InvokeAsync("GroupSendAsync", "testGroup", "Group message");

        // Assert
        await Task.WhenAny(Task.WhenAll(received1.Task, received2.Task), Task.Delay(2000));

        received1.Task.IsCompletedSuccessfully.ShouldBeTrue();
        received2.Task.IsCompletedSuccessfully.ShouldBeTrue();

        // Check that connection1 and connection2 received the group message
        messages1.ShouldContain(msg => msg.EndsWith("send message: Group message."));
        messages2.ShouldContain(msg => msg.EndsWith("send message: Group message."));

        // Connection3 should not receive any group messages
        messages3.ShouldNotContain(msg => msg.Contains("send message:") || msg.Contains("has joined"));

        // Cleanup
        await connection1.StopAsync();
        await connection2.StopAsync();
        await connection3.StopAsync();
    }

    [Fact]
    public async Task PartitionedGroupMembershipCleansUpOnDisconnectAsync()
    {
        const string groupName = "cleanup-group";

        var connection = _apps[0].CreateSignalRClient(nameof(SimpleTestHub));

        try
        {
            await connection.StartAsync();
            var connected = await WaitUntilAsync(
                "connection to acquire id",
                () => Task.FromResult(!string.IsNullOrEmpty(connection.ConnectionId)));
            connected.ShouldBeTrue();

            var connectionId = connection.ConnectionId ?? throw new InvalidOperationException("ConnectionId was not initialized.");

            await connection.InvokeAsync("AddToGroup", groupName);

            var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
            var partitionId = await coordinator.GetPartitionForGroup(groupName);
            var partition = NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(_siloCluster.Cluster.Client, partitionId);

            var tracked = await WaitUntilAsync(
                "connection to appear in partition",
                () => partition.HasConnection(connectionId),
                timeout: TimeSpan.FromSeconds(10));
            tracked.ShouldBeTrue();

            await connection.StopAsync();

            var released = await WaitUntilAsync(
                "partition to release disconnected connection",
                async () => !await partition.HasConnection(connectionId),
                progress: async () => await partition.HasConnection(connectionId)
                    ? "still tracked"
                    : "released",
                timeout: TimeSpan.FromSeconds(15));
            released.ShouldBeTrue();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task BatchGroupMembershipShouldAddAndRemoveAcrossMultipleGroupsAsync()
    {
        var groupNames = new[]
        {
            "batch-group-alpha",
            "batch-group-beta",
            "batch-group-gamma",
            "batch-group-delta"
        };

        var member = _apps[0].CreateSignalRClient(nameof(SimpleTestHub));
        var outsider = _apps[1].CreateSignalRClient(nameof(SimpleTestHub));
        var memberMessages = new List<string>();
        var outsiderMessages = new List<string>();

        member.On<string>("SendAll", memberMessages.Add);
        outsider.On<string>("SendAll", outsiderMessages.Add);

        try
        {
            await member.StartAsync();
            await outsider.StartAsync();

            var connectionId = member.ConnectionId ?? throw new InvalidOperationException("ConnectionId was not initialized.");

            await member.InvokeAsync("AddToGroups", groupNames);

            var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
            var partitionIds = (await Task.WhenAll(groupNames.Select(coordinator.GetPartitionForGroup)))
                .Distinct()
                .ToArray();
            var partitions = partitionIds
                .Select(partitionId => NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(_siloCluster.Cluster.Client, partitionId))
                .ToArray();

            var tracked = await WaitUntilAsync(
                "batched groups to appear in touched partitions",
                async () =>
                {
                    foreach (var partition in partitions)
                    {
                        if (!await partition.HasConnection(connectionId))
                        {
                            return false;
                        }
                    }

                    return true;
                },
                progress: async () =>
                {
                    var states = new List<string>(partitions.Length);
                    foreach (var partition in partitions)
                    {
                        states.Add(await partition.HasConnection(connectionId) ? "tracked" : "pending");
                    }

                    return string.Join(", ", states);
                });

            tracked.ShouldBeTrue();

            var beforeSends = groupNames
                .Select(groupName => outsider.InvokeAsync("GroupSendAsync", groupName, $"before:{groupName}"))
                .ToArray();
            await Task.WhenAll(beforeSends);

            var delivered = await WaitUntilAsync(
                "batched group delivery before removal",
                () => Task.FromResult(groupNames.All(groupName =>
                    memberMessages.Any(message => message.EndsWith($"send message: before:{groupName}.", StringComparison.Ordinal)))));

            delivered.ShouldBeTrue();
            outsiderMessages.ShouldNotContain(message => message.Contains("before:", StringComparison.Ordinal));

            await member.InvokeAsync("RemoveFromGroups", groupNames);

            var released = await WaitUntilAsync(
                "batched groups to release connection from touched partitions",
                async () =>
                {
                    foreach (var partition in partitions)
                    {
                        if (await partition.HasConnection(connectionId))
                        {
                            return false;
                        }
                    }

                    return true;
                });

            released.ShouldBeTrue();

            memberMessages.Clear();
            outsiderMessages.Clear();

            var afterSends = groupNames
                .Select(groupName => outsider.InvokeAsync("GroupSendAsync", groupName, $"after:{groupName}"))
                .ToArray();
            await Task.WhenAll(afterSends);

            await Task.Delay(TimeSpan.FromSeconds(1));

            memberMessages.ShouldBeEmpty();
            outsiderMessages.ShouldBeEmpty();
        }
        finally
        {
            await member.DisposeAsync();
            await outsider.DisposeAsync();
        }
    }

    [Fact]
    public async Task BatchGroupMembershipShouldCleanupTouchedPartitionsWhenDisconnectHappensMidJoinAsync()
    {
        var groupNamePrefix = $"batch-disconnect-group-{Guid.NewGuid():N}";
        var groupNames = Enumerable.Range(0, 512)
            .Select(index => $"{groupNamePrefix}-{index}")
            .ToArray();

        var connection = _apps[0].CreateSignalRClient(nameof(SimpleTestHub));
        using var joinGate = BatchGroupJoinCallFilter.Arm(groupNamePrefix);

        try
        {
            await connection.StartAsync();
            var connectionId = connection.ConnectionId ?? throw new InvalidOperationException("ConnectionId was not initialized.");

            var coordinator = NameHelperGenerator.GetGroupCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
            var partitionIds = (await Task.WhenAll(groupNames.Select(coordinator.GetPartitionForGroup)))
                .Distinct()
                .ToArray();
            partitionIds.Length.ShouldBeGreaterThan(0);

            var partitions = partitionIds
                .Select(partitionId => NameHelperGenerator.GetGroupPartitionGrain<SimpleTestHub>(_siloCluster.Cluster.Client, partitionId))
                .ToArray();

            var joinTask = connection.InvokeAsync("AddToGroups", groupNames);

            await joinGate.WaitUntilPausedAsync(TimeSpan.FromSeconds(10));
            (await GetTrackedPartitionCountAsync(partitions, connectionId)).ShouldBeGreaterThan(0);

            await connection.StopAsync();
            joinGate.Release();

            try
            {
                await joinTask;
            }
            catch (Exception ex) when (ex is HubException or InvalidOperationException or TaskCanceledException)
            {
                _testOutputHelper.WriteLine($"Join invocation ended after disconnect: {ex.GetType().Name}: {ex.Message}");
            }

            var released = await WaitUntilAsync(
                "all touched partitions to release disconnected connection after batch join",
                async () => await GetTrackedPartitionCountAsync(partitions, connectionId) == 0,
                progress: async () =>
                    $"tracked={await GetTrackedPartitionCountAsync(partitions, connectionId)}/{partitions.Length}",
                timeout: TimeSpan.FromSeconds(15));

            released.ShouldBeTrue();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task<int> GetTrackedPartitionCountAsync(
        IReadOnlyCollection<Core.Interfaces.ISignalRGroupPartitionGrain> partitions,
        string connectionId)
    {
        var tracked = 0;

        foreach (var partition in partitions)
        {
            if (await partition.HasConnection(connectionId))
            {
                tracked++;
            }
        }

        return tracked;
    }

    private async Task<bool> WaitUntilAsync(
        string description,
        Func<Task<bool>> condition,
        Func<Task<string>>? progress = null,
        TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var start = DateTime.UtcNow;
        var lastLog = TimeSpan.Zero;

        while (DateTime.UtcNow - start < limit)
        {
            if (await condition())
            {
                _testOutputHelper.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                return true;
            }

            var elapsed = DateTime.UtcNow - start;
            if (elapsed - lastLog >= _logInterval)
            {
                if (progress is not null)
                {
                    var status = await progress();
                    _testOutputHelper.WriteLine($"Waiting for {description}... elapsed {elapsed:c}. Status: {status}");
                }
                else
                {
                    _testOutputHelper.WriteLine($"Waiting for {description}... elapsed {elapsed:c}.");
                }

                lastLog = elapsed;
            }

            await Task.Delay(_waitInterval);
        }

        if (progress is not null)
        {
            var status = await progress();
            _testOutputHelper.WriteLine($"Final status for '{description}': {status}");
        }

        return await condition();
    }
}
