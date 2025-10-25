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
    private const int BroadcastConnectionCount = 40;
    private const int BroadcastMessageCount = 2_500;

    private const int GroupConnectionCount = 40;
    private const int GroupCount = 40;
    private const int GroupMessagesPerGroup = 2_500;

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
        var orleans = await RunBroadcastScenarioAsync(useOrleans: true, basePort: 9400);
        var inMemory = await RunBroadcastScenarioAsync(useOrleans: false, basePort: 9500);

        _output.WriteLine(
            $"Broadcast comparison ({BroadcastConnectionCount} connections, {BroadcastMessageCount} broadcasts) => Orleans: {orleans.TotalMilliseconds:F0} ms, In-Memory: {inMemory.TotalMilliseconds:F0} ms");

        orleans.ShouldNotBe(TimeSpan.Zero);
        inMemory.ShouldNotBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Group_Performance_Comparison()
    {
        var orleans = await RunGroupScenarioAsync(useOrleans: true, basePort: 9600);
        var inMemory = await RunGroupScenarioAsync(useOrleans: false, basePort: 9700);

        _output.WriteLine(
            $"Group comparison ({GroupConnectionCount} connections, {GroupCount} groups, {GroupMessagesPerGroup} messages/group) => Orleans: {orleans.TotalMilliseconds:F0} ms, In-Memory: {inMemory.TotalMilliseconds:F0} ms");

        orleans.ShouldNotBe(TimeSpan.Zero);
        inMemory.ShouldNotBe(TimeSpan.Zero);
    }

    private async Task<TimeSpan> RunBroadcastScenarioAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var (connections, perAppCounts) = await CreateConnectionsAsync(apps, BroadcastConnectionCount);

        try
        {
            long received = 0;
            var totalConnections = connections.Count;
            long expected;
            if (useOrleans)
            {
                expected = BroadcastMessageCount * (long)totalConnections * totalConnections;
            }
            else
            {
                expected = BroadcastMessageCount * perAppCounts.Sum(count => (long)count * count);
            }

            foreach (var connection in connections)
            {
                connection.On<string>("SendAll", _ => Interlocked.Increment(ref received));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));

            var starting = Interlocked.Read(ref received);
            var stopwatch = Stopwatch.StartNew();
            _output.WriteLine($"Broadcast run ({BroadcastConnectionCount} connections × {BroadcastMessageCount:N0} messages).");

            var sendTasks = connections.Select(connection => Task.Run(async () =>
            {
                for (var iteration = 0; iteration < BroadcastMessageCount; iteration++)
                {
                    await connection.InvokeAsync<int>("All");
                }
            }));
            await Task.WhenAll(sendTasks);

            var progressStep = Math.Max(1_000, expected / 10);
            var lastReported = 0L;

            var completed = await WaitUntilAsync(
                () => Interlocked.Read(ref received) - starting >= expected,
                timeout: TimeSpan.FromMinutes(2),
                _output,
                () =>
                {
                    var delivered = Interlocked.Read(ref received) - starting;
                    if (delivered - lastReported >= progressStep || delivered == expected)
                    {
                        lastReported = delivered;
                        return $"{delivered:N0}/{expected:N0}";
                    }

                    return null;
                });

            stopwatch.Stop();

            var delivered = Interlocked.Read(ref received) - starting;
            if (!completed)
            {
                _output.WriteLine($"Broadcast scenario timed out with {delivered:N0}/{expected:N0} messages delivered.");
            }

            delivered.ShouldBe(expected, $"Expected all broadcasts to reach listeners (delivered {delivered:N0}/{expected:N0}).");
            var throughput = delivered / Math.Max(1, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"Broadcast delivered {delivered:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} msg/s.");

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeAsync(connections);
            DisposeApplications(apps);
        }
    }

    private async Task<TimeSpan> RunGroupScenarioAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var (connections, _) = await CreateConnectionsAsync(apps, GroupConnectionCount);

        try
        {
            var groupNames = Enumerable.Range(0, GroupCount).Select(i => $"perf-group-{i}").ToArray();
            var groupMembers = groupNames.ToDictionary(name => name, _ => new List<HubConnection>());

            long received = 0;

            for (var index = 0; index < connections.Count; index++)
            {
                var connection = connections[index];
                var groupName = groupNames[index % groupNames.Length];
                groupMembers[groupName].Add(connection);

                connection.On<string>("SendAll", _ => Interlocked.Increment(ref received));
                await connection.InvokeAsync("AddToGroup", groupName);
            }

            var activeGroups = groupMembers.Where(kvp => kvp.Value.Count > 0).ToArray();
            var expected = activeGroups.Sum(kvp => (long)kvp.Value.Count) * GroupMessagesPerGroup;
            expected.ShouldBeGreaterThan(0, "At least one connection must belong to a group.");

            await Task.Delay(TimeSpan.FromMilliseconds(250));

            var starting = Interlocked.Read(ref received);
            var stopwatch = Stopwatch.StartNew();
            _output.WriteLine($"Group run ({GroupConnectionCount} connections across {activeGroups.Length} active groups × {GroupMessagesPerGroup:N0} messages).");

            var sendTasks = activeGroups.Select(tuple => Task.Run(async () =>
            {
                var (group, members) = tuple;
                var sender = members[0];
                for (var message = 0; message < GroupMessagesPerGroup; message++)
                {
                    await sender.InvokeAsync("GroupSendAsync", group, $"payload-{message}");
                }
            }));
            await Task.WhenAll(sendTasks);

            var progressStep = Math.Max(1_000, expected / 10);
            var lastReported = 0L;

            var completed = await WaitUntilAsync(
                () => Interlocked.Read(ref received) - starting >= expected,
                timeout: TimeSpan.FromMinutes(2),
                _output,
                () =>
                {
                    var delivered = Interlocked.Read(ref received) - starting;
                    if (delivered - lastReported >= progressStep || delivered == expected)
                    {
                        lastReported = delivered;
                        return $"{delivered:N0}/{expected:N0}";
                    }

                    return null;
                });

            stopwatch.Stop();

            var delivered = Interlocked.Read(ref received) - starting;
            if (!completed)
            {
                _output.WriteLine($"Group scenario timed out with {delivered:N0}/{expected:N0} messages delivered.");
            }

            delivered.ShouldBe(expected, $"Expected all group messages to reach listeners (delivered {delivered:N0}/{expected:N0}).");
            var throughput = delivered / Math.Max(1, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"Group broadcast delivered {delivered:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} msg/s.");

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeAsync(connections);
            DisposeApplications(apps);
        }
    }

    private List<TestWebApplication> CreateApplications(int basePort, bool useOrleans)
    {
        var apps = new List<TestWebApplication>(4);
        for (var i = 0; i < 4; i++)
        {
            var app = new TestWebApplication(_cluster, basePort + i, useOrleans);
            apps.Add(app);
        }

        return apps;
    }

    private static async Task<(List<HubConnection> Connections, int[] PerAppCounts)> CreateConnectionsAsync(
        IReadOnlyList<TestWebApplication> apps,
        int count)
    {
        var connections = new List<HubConnection>(count);
        var perAppCounts = new int[apps.Count];

        for (var index = 0; index < count; index++)
        {
            var appIndex = index % apps.Count;
            var app = apps[appIndex];
            var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
            await connection.StartAsync();
            connections.Add(connection);
            perAppCounts[appIndex]++;
        }

        return (connections, perAppCounts);
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

    private static void DisposeApplications(IEnumerable<TestWebApplication> apps)
    {
        foreach (var app in apps)
        {
            app.Dispose();
        }
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout,
        ITestOutputHelper output,
        Func<string?>? progress = null)
    {
        var start = DateTime.UtcNow;
        var deadline = timeout.HasValue ? start + timeout.Value : (DateTime?)null;

        while (!deadline.HasValue || DateTime.UtcNow < deadline.Value)
        {
            if (condition())
            {
                return true;
            }

            if (progress is not null)
            {
                var status = progress();
                if (!string.IsNullOrEmpty(status))
                {
                    output.WriteLine($"Progress: {status}");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }
}
