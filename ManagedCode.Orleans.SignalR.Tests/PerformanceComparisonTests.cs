using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(LoadCluster))]
public class PerformanceComparisonTests
{
    private const int BroadcastConnectionCount = 60;
    private const int BroadcastMessageCount = 40;

    private const int GroupConnectionCount = 48;
    private const int GroupCount = 4;
    private const int GroupMessagesPerGroup = 25;

    private readonly LoadClusterFixture _cluster;
    private readonly ITestOutputHelper _output;

    public PerformanceComparisonTests(LoadClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
    }

    [Fact]
    public async Task Broadcast_Performance_Comparison()
    {
        var orleans = await RunBroadcastScenarioAsync(useOrleans: true, port: 9401);
        var inMemory = await RunBroadcastScenarioAsync(useOrleans: false, port: 9402);

        _output.WriteLine(
            $"Broadcast comparison ({BroadcastConnectionCount} connections, {BroadcastMessageCount} broadcasts) => Orleans: {orleans.TotalMilliseconds:F0} ms, In-Memory: {inMemory.TotalMilliseconds:F0} ms");

        orleans.ShouldNotBe(TimeSpan.Zero);
        inMemory.ShouldNotBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Group_Performance_Comparison()
    {
        var orleans = await RunGroupScenarioAsync(useOrleans: true, port: 9411);
        var inMemory = await RunGroupScenarioAsync(useOrleans: false, port: 9412);

        _output.WriteLine(
            $"Group comparison ({GroupConnectionCount} connections, {GroupCount} groups, {GroupMessagesPerGroup} messages/group) => Orleans: {orleans.TotalMilliseconds:F0} ms, In-Memory: {inMemory.TotalMilliseconds:F0} ms");

        orleans.ShouldNotBe(TimeSpan.Zero);
        inMemory.ShouldNotBe(TimeSpan.Zero);
    }

    private async Task<TimeSpan> RunBroadcastScenarioAsync(bool useOrleans, int port)
    {
        using var app = new TestWebApplication(_cluster, port, useOrleans);
        var connections = await CreateConnectionsAsync(app, BroadcastConnectionCount, "broadcast");

        try
        {
            long received = 0;
            var expected = BroadcastConnectionCount * BroadcastMessageCount;

            foreach (var connection in connections)
            {
                connection.On<string>("SendAll", _ => Interlocked.Increment(ref received));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));

            var starting = Interlocked.Read(ref received);

            var stopwatch = Stopwatch.StartNew();

            for (var message = 0; message < BroadcastMessageCount; message++)
            {
                var sender = connections[message % connections.Count];
                await sender.InvokeAsync<int>("All");
            }

            var completed = await WaitUntilAsync(
                () => Interlocked.Read(ref received) - starting >= expected,
                TimeSpan.FromSeconds(30));

            stopwatch.Stop();

            completed.ShouldBeTrue("Broadcast messages were not delivered to all connections.");
            (Interlocked.Read(ref received) - starting).ShouldBe(expected);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeAsync(connections);
        }
    }

    private async Task<TimeSpan> RunGroupScenarioAsync(bool useOrleans, int port)
    {
        using var app = new TestWebApplication(_cluster, port, useOrleans);
        var connections = await CreateConnectionsAsync(app, GroupConnectionCount, "group");

        try
        {
            var groupNames = Enumerable.Range(0, GroupCount).Select(i => $"perf-group-{i}").ToArray();
            var groupMembers = groupNames.ToDictionary(name => name, _ => new List<HubConnection>());

            long received = 0;
            var expected = (long)GroupConnectionCount * GroupMessagesPerGroup;

            for (var index = 0; index < connections.Count; index++)
            {
                var connection = connections[index];
                var groupName = groupNames[index % groupNames.Length];
                groupMembers[groupName].Add(connection);

                connection.On<string>("SendAll", _ => Interlocked.Increment(ref received));
                await connection.InvokeAsync("AddToGroup", groupName);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));

            var starting = Interlocked.Read(ref received);

            var stopwatch = Stopwatch.StartNew();

            foreach (var group in groupNames)
            {
                var sender = groupMembers[group].First();
                for (var message = 0; message < GroupMessagesPerGroup; message++)
                {
                    await sender.InvokeAsync("GroupSendAsync", group, $"payload-{message}");
                }
            }

            var completed = await WaitUntilAsync(
                () => Interlocked.Read(ref received) - starting >= expected,
                TimeSpan.FromSeconds(30));

            stopwatch.Stop();

            completed.ShouldBeTrue("Group messages were not delivered to all group members.");
            (Interlocked.Read(ref received) - starting).ShouldBe(expected);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeAsync(connections);
        }
    }

    private static async Task<List<HubConnection>> CreateConnectionsAsync(
        TestWebApplication app,
        int count,
        string label)
    {
        var connections = new List<HubConnection>(count);

        for (var index = 0; index < count; index++)
        {
            var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
            await connection.StartAsync();
            connections.Add(connection);
        }

        return connections;
    }

    private static async Task DisposeAsync(IEnumerable<HubConnection> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                await connection.StopAsync();
            }
            catch
            {
                // ignored — we're tearing down the test
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }
}
