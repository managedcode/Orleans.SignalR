using System.Diagnostics;
using System.Linq;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure;

public sealed class PerformanceScenarioHarness
{
    private readonly LoadClusterFixture _cluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor? _loggerAccessor;
    private const string DeviceScenarioKey = "device-echo";
    private const string BroadcastScenarioKey = "broadcast-fanout";
    private const string GroupScenarioKey = "group-broadcast";
    private const string StreamScenarioKey = "streaming";
    private const string InvocationScenarioKey = "invocation";

    public PerformanceScenarioSettings Settings { get; }

    public PerformanceScenarioHarness(
        LoadClusterFixture cluster,
        ITestOutputHelper output,
        TestOutputHelperAccessor? loggerAccessor = null,
        PerformanceScenarioSettings? settings = null)
    {
        _cluster = cluster;
        _output = output;
        _loggerAccessor = loggerAccessor;
        Settings = settings ?? PerformanceScenarioSettings.CreatePerformance();
    }

    public async Task<TimeSpan> RunDeviceEchoAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var connections = new List<HubConnection>(Settings.DeviceConnections);
        var perConnection = new long[Settings.DeviceConnections];
        var connectionIds = new string[Settings.DeviceConnections];
        long totalDelivered = 0;

        try
        {
            for (var index = 0; index < Settings.DeviceConnections; index++)
            {
                var appIndex = index % apps.Count;
                var app = apps[appIndex];
                var connectionIndex = index;
                var userId = $"device-{index:D4}";
                var connection = await CreateUserConnectionAsync(app, nameof(SimpleTestHub), userId);

                connection.On<string>("SendAll", _ =>
                {
                    Interlocked.Increment(ref perConnection[connectionIndex]);
                    Interlocked.Increment(ref totalDelivered);
                });

                await connection.StartAsync();
                connectionIds[connectionIndex] = connection.ConnectionId ?? throw new InvalidOperationException("ConnectionId not available after start.");
                connections.Add(connection);
            }

            var passes = Math.Max(1, Settings.DevicePasses);
            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} device echo";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections × {Settings.DeviceMessagesPerConnection} messages for {passes} passes.");

            var stopwatch = Stopwatch.StartNew();

            for (var pass = 0; pass < passes; pass++)
            {
                var sendTasks = new List<Task>(connections.Count);
                for (var index = 0; index < connections.Count; index++)
                {
                    var connectionIndex = index;
                    var connection = connections[connectionIndex];
                    var targetConnectionId = connectionIds[connectionIndex];
                    sendTasks.Add(Task.Run(async () =>
                    {
                        for (var iteration = 0; iteration < Settings.DeviceMessagesPerConnection; iteration++)
                        {
                            await connection.InvokeAsync<int>("Connections", new[] { targetConnectionId });
                        }
                    }));
                }
                await Task.WhenAll(sendTasks);
            }

            var expectedPerConnectionCount = Settings.DeviceMessagesPerConnection * passes;
            var expected = (long)Settings.DeviceConnections * expectedPerConnectionCount;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalDelivered), expected, Settings.DeviceTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} deliveries.");

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(expectedPerConnectionCount, $"Device connection #{index} should receive all messages across {passes} passes.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} msg/s.");
            PerformanceSummaryRecorder.RecordRun(DeviceScenarioKey, "Device Echo", useOrleans, stopwatch.Elapsed, throughput);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
            DisposeApplications(apps);
        }
    }

    public async Task<TimeSpan> RunBroadcastFanoutAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var connections = new List<HubConnection>(Settings.BroadcastConnections);
        var receipts = new long[Settings.BroadcastConnections];
        var perAppConnectionCounts = new int[apps.Count];
        var connectionAppIndices = new int[Settings.BroadcastConnections];
        long totalDelivered = 0;

        try
        {
            for (var index = 0; index < Settings.BroadcastConnections; index++)
            {
                var appIndex = index % apps.Count;
                var connectionIndex = index;
                var app = apps[appIndex];
                var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
                connection.On<string>("PerfBroadcast", _ =>
                {
                    Interlocked.Increment(ref receipts[connectionIndex]);
                    Interlocked.Increment(ref totalDelivered);
                });

                await connection.StartAsync();
                connections.Add(connection);
                perAppConnectionCounts[appIndex]++;
                connectionAppIndices[connectionIndex] = appIndex;
            }

            var senderCount = Math.Min(Settings.BroadcastSenderCount, connections.Count);
            if (senderCount == 0)
            {
                throw new InvalidOperationException("At least one broadcast sender is required.");
            }

            var perAppSenderCounts = new int[apps.Count];
            for (var senderIndex = 0; senderIndex < senderCount; senderIndex++)
            {
                perAppSenderCounts[senderIndex % apps.Count]++;
            }

            var connectionDistribution = string.Join(", ", perAppConnectionCounts.Select((count, index) => $"app#{index}:{count}"));
            var senderDistribution = string.Join(", ", perAppSenderCounts.Select((count, index) => $"app#{index}:{count}"));
            _output.WriteLine($"Broadcast setup: {connections.Count} connections ({connectionDistribution}). Sender sample {senderCount} ({senderDistribution}).");

            await Task.Delay(TimeSpan.FromSeconds(1));

            var expectedPerConnection = new long[connections.Count];
            for (var index = 0; index < connections.Count; index++)
            {
                var appIndex = connectionAppIndices[index];
                expectedPerConnection[index] = useOrleans
                    ? (long)senderCount * Settings.BroadcastMessagesPerConnection
                    : (long)perAppSenderCounts[appIndex] * Settings.BroadcastMessagesPerConnection;
            }

            var broadcastPasses = Math.Max(1, Settings.BroadcastPasses);
            for (var idx = 0; idx < expectedPerConnection.Length; idx++)
            {
                expectedPerConnection[idx] *= broadcastPasses;
            }

            var expectedDeliveries = expectedPerConnection.Sum();
            var senders = connections.Take(senderCount).ToArray();

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} broadcast";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections, {senderCount} senders, {broadcastPasses} passes.");
            var stopwatch = Stopwatch.StartNew();

            for (var pass = 0; pass < broadcastPasses; pass++)
            {
                var sendTasks = senders.Select((connection, senderIndex) => Task.Run(async () =>
                {
                    for (var iteration = 0; iteration < Settings.BroadcastMessagesPerConnection; iteration++)
                    {
                        var payload = $"broadcast:{senderIndex:D2}:{pass:D2}:{iteration:D5}";
                        await connection.InvokeAsync("BroadcastPayload", payload);
                    }
                }));
                await Task.WhenAll(sendTasks);
            }

            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalDelivered), expectedDeliveries, Settings.BroadcastTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} deliveries.");

            stopwatch.Stop();

            for (var index = 0; index < receipts.Length; index++)
            {
                receipts[index].ShouldBe(expectedPerConnection[index],
                    $"Broadcast connection #{index} should receive all fan-out messages.");
            }

            var throughput = expectedDeliveries / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expectedDeliveries:N0}/{expectedDeliveries:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} deliveries/s.");
            PerformanceSummaryRecorder.RecordRun(BroadcastScenarioKey, "Broadcast Fan-Out", useOrleans, stopwatch.Elapsed, throughput);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
            DisposeApplications(apps);
        }
    }

    public async Task<TimeSpan> RunGroupScenarioAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var connections = new List<HubConnection>(Settings.GroupConnections);
        var perConnection = new long[Settings.GroupConnections];
        var expectedPerConnection = new long[Settings.GroupConnections];
        var connectionAppIndices = new int[Settings.GroupConnections];
        var groupAssignments = new string[Settings.GroupConnections];
        var groupMarkers = new string[Settings.GroupConnections];
        var groupMembers = new Dictionary<string, List<int>>();
        long totalDelivered = 0;

        try
        {
            for (var index = 0; index < Settings.GroupConnections; index++)
            {
                var appIndex = index % apps.Count;
                var connectionIndex = index;
                var app = apps[appIndex];

                var groupName = $"perf-group-{index / Settings.GroupSize:D2}";
                groupAssignments[index] = groupName;
                connectionAppIndices[index] = appIndex;

                if (!groupMembers.TryGetValue(groupName, out var members))
                {
                    members = new List<int>();
                    groupMembers[groupName] = members;
                }
                members.Add(connectionIndex);

                var marker = $"group:{groupName}|payload|";
                groupMarkers[connectionIndex] = marker;
                var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
                connection.On<string>("SendAll", message =>
                {
                    if (message.Contains(marker, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref perConnection[connectionIndex]);
                        Interlocked.Increment(ref totalDelivered);
                    }
                });

                await connection.StartAsync();
                connections.Add(connection);
            }

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} group";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections across {groupMembers.Count} groups (size {Settings.GroupSize}) for {Math.Max(1, Settings.GroupPasses)} passes.");

            await Task.WhenAll(connections.Select((connection, index) => connection.InvokeAsync("AddToGroup", groupAssignments[index])));
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            foreach (var (groupName, members) in groupMembers)
            {
                var broadcasters = members.Take(Math.Min(Settings.GroupBroadcastersPerGroup, members.Count)).ToArray();
                if (broadcasters.Length == 0)
                {
                    continue;
                }

                var baseCount = Settings.GroupMessagesPerGroup / broadcasters.Length;
                var extra = Settings.GroupMessagesPerGroup % broadcasters.Length;

                for (var idx = 0; idx < broadcasters.Length; idx++)
                {
                    var broadcasterIndex = broadcasters[idx];
                    var messagesToSend = baseCount + (idx < extra ? 1 : 0);
                    var broadcasterApp = connectionAppIndices[broadcasterIndex];

                    if (useOrleans)
                    {
                        foreach (var memberIndex in members)
                        {
                            expectedPerConnection[memberIndex] += messagesToSend;
                        }
                    }
                    else
                    {
                        foreach (var memberIndex in members)
                        {
                            if (connectionAppIndices[memberIndex] == broadcasterApp)
                            {
                                expectedPerConnection[memberIndex] += messagesToSend;
                            }
                        }
                    }
                }
            }

            var groupPasses = Math.Max(1, Settings.GroupPasses);
            for (var idx = 0; idx < expectedPerConnection.Length; idx++)
            {
                expectedPerConnection[idx] *= groupPasses;
            }

            var sendStopwatch = Stopwatch.StartNew();

            for (var pass = 0; pass < groupPasses; pass++)
            {
                var sendTasks = groupMembers.Select(group => Task.Run(async () =>
                {
                    var members = group.Value;
                    var broadcasters = members.Take(Math.Min(Settings.GroupBroadcastersPerGroup, members.Count)).ToArray();
                    if (broadcasters.Length == 0)
                    {
                        return;
                    }

                    var baseCount = Settings.GroupMessagesPerGroup / broadcasters.Length;
                    var extra = Settings.GroupMessagesPerGroup % broadcasters.Length;

                    for (var idx = 0; idx < broadcasters.Length; idx++)
                    {
                        var broadcasterIndex = broadcasters[idx];
                        var messagesToSend = baseCount + (idx < extra ? 1 : 0);
                        var connection = connections[broadcasterIndex];
                        var marker = groupMarkers[broadcasterIndex];
                        var groupName = group.Key;
                        for (var iteration = 0; iteration < messagesToSend; iteration++)
                        {
                            await connection.InvokeAsync("GroupSendAsync", groupName, $"{marker}{pass:D2}:{idx:D2}:{iteration:D5}");
                        }
                    }
                }));
                await Task.WhenAll(sendTasks);
            }

            var expected = expectedPerConnection.Sum();
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalDelivered), expected, Settings.GroupTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} deliveries.");
            sendStopwatch.Stop();

            foreach (var group in groupMembers)
            {
                foreach (var member in group.Value)
                {
                    perConnection[member].ShouldBe(expectedPerConnection[member],
                        $"Group member #{member} should receive all group messages for {group.Key}.");
                }
            }

            var throughput = expected / Math.Max(1.0, sendStopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expected:N0}/{expected:N0} in {sendStopwatch.Elapsed}. Throughput ≈ {throughput:N0} deliveries/s.");
            PerformanceSummaryRecorder.RecordRun(GroupScenarioKey, "Group Broadcast", useOrleans, sendStopwatch.Elapsed, throughput);

            return sendStopwatch.Elapsed;
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
            DisposeApplications(apps);
        }
    }

    public async Task<TimeSpan> RunStreamingScenarioAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var connections = new List<HubConnection>(Settings.StreamConnections);
        var perConnection = new long[Settings.StreamConnections];
        var streamTasks = new List<Task>(Settings.StreamConnections);
        long totalItems = 0;

        try
        {
            for (var index = 0; index < Settings.StreamConnections; index++)
            {
                var app = apps[index % apps.Count];
                var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
                await connection.StartAsync();
                connections.Add(connection);
            }

            var streamPasses = Math.Max(1, Settings.StreamPasses);
            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} streaming";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections streaming {Settings.StreamItemsPerConnection} items each for {streamPasses} passes.");

            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < connections.Count; index++)
            {
                var connectionIndex = index;
                var connection = connections[index];
                var streamTask = Task.Run(async () =>
                {
                    for (var pass = 0; pass < streamPasses; pass++)
                    {
                        await foreach (var item in connection.StreamAsync<int>("Counter", Settings.StreamItemsPerConnection, 0))
                        {
                            _ = item;
                            Interlocked.Increment(ref perConnection[connectionIndex]);
                            Interlocked.Increment(ref totalItems);
                        }
                    }
                });
                streamTasks.Add(streamTask);
            }

            var expectedPerConnectionCount = Settings.StreamItemsPerConnection * streamPasses;
            var expected = (long)Settings.StreamConnections * expectedPerConnectionCount;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalItems), expected, Settings.StreamTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} delivery.");

            await Task.WhenAll(streamTasks);

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(expectedPerConnectionCount, $"Stream connection #{index} should receive all items.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} items/s.");
            PerformanceSummaryRecorder.RecordRun(StreamScenarioKey, "Streaming", useOrleans, stopwatch.Elapsed, throughput);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
            DisposeApplications(apps);
        }
    }

    public async Task<TimeSpan> RunInvocationScenarioAsync(bool useOrleans, int basePort)
    {
        var apps = CreateApplications(basePort, useOrleans);
        var connections = new List<HubConnection>(Settings.InvocationConnections);
        var perConnection = new long[Settings.InvocationConnections];
        long totalInvocations = 0;

        try
        {
            for (var index = 0; index < Settings.InvocationConnections; index++)
            {
                var app = apps[index % apps.Count];
                var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
                await connection.StartAsync();
                connections.Add(connection);
            }

            var invocationPasses = Math.Max(1, Settings.InvocationPasses);
            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} invocation";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections × {Settings.InvocationMessagesPerConnection} invocations for {invocationPasses} passes.");

            var stopwatch = Stopwatch.StartNew();

            var invocationTasks = connections.Select((connection, index) => Task.Run(async () =>
            {
                for (var pass = 0; pass < invocationPasses; pass++)
                {
                    for (var iteration = 0; iteration < Settings.InvocationMessagesPerConnection; iteration++)
                    {
                        var result = await connection.InvokeAsync<int>("Plus", iteration, index);
                        result.ShouldBe(iteration + index, "Plus result should match expected sum.");
                        Interlocked.Increment(ref perConnection[index]);
                        Interlocked.Increment(ref totalInvocations);
                    }
                }
            }));

            var workerTask = Task.WhenAll(invocationTasks);
            var expectedPerConnectionCount = Settings.InvocationMessagesPerConnection * invocationPasses;
            var expected = (long)Settings.InvocationConnections * expectedPerConnectionCount;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalInvocations), expected, Settings.InvocationTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} completion.");
            await workerTask;

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(expectedPerConnectionCount, $"Invocation connection #{index} should complete all invocations across all passes.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} completed {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} calls/s.");
            PerformanceSummaryRecorder.RecordRun(InvocationScenarioKey, "Invocation", useOrleans, stopwatch.Elapsed, throughput);

            return stopwatch.Elapsed;
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
            DisposeApplications(apps);
        }
    }

    private List<TestWebApplication> CreateApplications(int basePort, bool useOrleans)
    {
        var apps = new List<TestWebApplication>(4);
        for (var index = 0; index < 4; index++)
        {
            var app = new TestWebApplication(_cluster, basePort + index, useOrleans, _loggerAccessor);
            apps.Add(app);
        }

        return apps;
    }

    private static async Task<HubConnection> CreateUserConnectionAsync(TestWebApplication app, string hubName, string userId)
    {
        using var client = app.CreateHttpClient();
        var response = await client.GetAsync($"/auth?user={userId}");
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadAsStringAsync();

        return app.CreateSignalRClient(
            hubName,
            configureConnection: options => options.AccessTokenProvider = () => Task.FromResult<string?>(token));
    }

    private static async Task DisposeConnectionsAsync(IEnumerable<HubConnection> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                await connection.StopAsync();
            }
            catch
            {
                // ignored during cleanup
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

    private async Task<bool> AwaitWithProgressAsync(Func<long> currentValue, long expected, TimeSpan timeout, string scenario)
    {
        if (expected == 0)
        {
            _output.WriteLine($"{scenario} expected 0 deliverables; skipping wait.");
            return true;
        }

        var watch = Stopwatch.StartNew();
        var logInterval = TimeSpan.FromSeconds(10);
        var lastLog = TimeSpan.Zero;

        while (watch.Elapsed < timeout)
        {
            var current = currentValue();
            if (current >= expected)
            {
                _output.WriteLine($"{scenario} reached {current:N0}/{expected:N0} after {watch.Elapsed:c}.");
                return true;
            }

            if (watch.Elapsed - lastLog >= logInterval)
            {
                _output.WriteLine($"{scenario} progress {current:N0}/{expected:N0} after {watch.Elapsed:c}.");
                lastLog = watch.Elapsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        var final = currentValue();
        _output.WriteLine($"{scenario} timed out after {timeout:c}: {final:N0}/{expected:N0}.");
        return final >= expected;
    }
}

public sealed record PerformanceScenarioSettings(
    int DeviceConnections,
    int DeviceMessagesPerConnection,
    int DevicePasses,
    int BroadcastConnections,
    int BroadcastMessagesPerConnection,
    int BroadcastSenderCount,
    int BroadcastPasses,
    int GroupConnections,
    int GroupSize,
    int GroupMessagesPerGroup,
    int GroupBroadcastersPerGroup,
    int GroupPasses,
    int StreamConnections,
    int StreamItemsPerConnection,
    int StreamPasses,
    int InvocationConnections,
    int InvocationMessagesPerConnection,
    int InvocationPasses,
    TimeSpan DeviceTimeout,
    TimeSpan BroadcastTimeout,
    TimeSpan GroupTimeout,
    TimeSpan StreamTimeout,
    TimeSpan InvocationTimeout)
{
    public static PerformanceScenarioSettings CreatePerformance() => new(
        DeviceConnections: 150,
        DeviceMessagesPerConnection: 100,
        DevicePasses: 1,
        BroadcastConnections: 100,
        BroadcastMessagesPerConnection: 50,
        BroadcastSenderCount: 4,
        BroadcastPasses: 1,
        GroupConnections: 200,
        GroupSize: 50,
        GroupMessagesPerGroup: 60,
        GroupBroadcastersPerGroup: 4,
        GroupPasses: 1,
        StreamConnections: 100,
        StreamItemsPerConnection: 120,
        StreamPasses: 1,
        InvocationConnections: 140,
        InvocationMessagesPerConnection: 120,
        InvocationPasses: 1,
        DeviceTimeout: TimeSpan.FromMinutes(4),
        BroadcastTimeout: TimeSpan.FromMinutes(4),
        GroupTimeout: TimeSpan.FromMinutes(4),
        StreamTimeout: TimeSpan.FromMinutes(4),
        InvocationTimeout: TimeSpan.FromMinutes(4));

    public static PerformanceScenarioSettings CreateStress() => new(
        DeviceConnections: 160,
        DeviceMessagesPerConnection: 80,
        DevicePasses: 4,
        BroadcastConnections: 120,
        BroadcastMessagesPerConnection: 40,
        BroadcastSenderCount: 4,
        BroadcastPasses: 4,
        GroupConnections: 240,
        GroupSize: 60,
        GroupMessagesPerGroup: 70,
        GroupBroadcastersPerGroup: 4,
        GroupPasses: 4,
        StreamConnections: 120,
        StreamItemsPerConnection: 120,
        StreamPasses: 4,
        InvocationConnections: 180,
        InvocationMessagesPerConnection: 100,
        InvocationPasses: 4,
        DeviceTimeout: TimeSpan.FromMinutes(8),
        BroadcastTimeout: TimeSpan.FromMinutes(8),
        GroupTimeout: TimeSpan.FromMinutes(8),
        StreamTimeout: TimeSpan.FromMinutes(8),
        InvocationTimeout: TimeSpan.FromMinutes(8));
}
