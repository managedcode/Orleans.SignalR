using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class PartitioningTests
{
    private static readonly TimeSpan WaitInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1);
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
    public async Task Default_Configuration_Should_Use_Connection_Partitioning()
    {
        // Arrange
        var connection = _apps[0].CreateSignalRClient(nameof(SimpleTestHub));
        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);

        // Act - Send a message (uses partitioned coordinator by default)
        var result = await connection.InvokeAsync<int>("All");
        
        // Assert
        result.ShouldBeGreaterThan(0);

        var coordinatorGrain = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
        var partitionCount = await coordinatorGrain.GetPartitionCount();
        var defaultPartitions = (int)new OrleansSignalROptions().ConnectionPartitionCount;

        defaultPartitions.ShouldBeGreaterThan(1);
        partitionCount.ShouldBe(defaultPartitions);

        // Cleanup
        await connection.StopAsync();
    }

    [Fact]
    public async Task Default_Group_Configuration_Should_Use_Group_Partitioning()
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
    public async Task Partitioned_SendToAll_Should_Reach_All_Connections()
    {
        // Arrange
        const int connectionsPerApp = 8;
        var totalConnections = connectionsPerApp * _apps.Count;
        var connections = new HubConnection[totalConnections];
        var connectionLabels = new string[totalConnections];
        var receivedMessages = new TaskCompletionSource<string>[totalConnections];

        try
        {
            for (int i = 0; i < totalConnections; i++)
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
                _testOutputHelper.WriteLine($"[{label}] started with id {connections[i].ConnectionId}.");
            }

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
    public async Task Partitioned_SendToGroup_Should_Only_Reach_Group_Members()
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
                received1.TrySetResult(true);
        });
        connection2.On<string>("SendAll", msg => 
        {
            messages2.Add(msg);
            if (msg.Contains("send message:"))
                received2.TrySetResult(true);
        });
        connection3.On<string>("SendAll", msg => messages3.Add(msg));

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
    public async Task Partitioned_Group_Membership_Cleans_Up_On_Disconnect()
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
            if (elapsed - lastLog >= LogInterval)
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

            await Task.Delay(WaitInterval);
        }

        if (progress is not null)
        {
            var status = await progress();
            _testOutputHelper.WriteLine($"Final status for '{description}': {status}");
        }

        return await condition();
    }
}
