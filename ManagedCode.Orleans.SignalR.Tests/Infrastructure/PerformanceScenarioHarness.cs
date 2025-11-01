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

internal sealed class PerformanceScenarioHarness
{
    private readonly LoadClusterFixture _cluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor? _loggerAccessor;

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

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} device echo";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections × {Settings.DeviceMessagesPerConnection} messages.");

            var stopwatch = Stopwatch.StartNew();

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

            var expected = (long)Settings.DeviceConnections * Settings.DeviceMessagesPerConnection;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalDelivered), expected, Settings.DeviceTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} deliveries.");

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(Settings.DeviceMessagesPerConnection, $"Device connection #{index} should receive all messages.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} msg/s.");

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

            var expectedDeliveries = expectedPerConnection.Sum();
            var senders = connections.Take(senderCount).ToArray();

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} broadcast";
            var stopwatch = Stopwatch.StartNew();

            var sendTasks = senders.Select((connection, senderIndex) => Task.Run(async () =>
            {
                for (var iteration = 0; iteration < Settings.BroadcastMessagesPerConnection; iteration++)
                {
                    var payload = $"broadcast:{senderIndex:D2}:{iteration:D5}";
                    await connection.InvokeAsync("BroadcastPayload", payload);
                }
            }));
            await Task.WhenAll(sendTasks);

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
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections across {groupMembers.Count} groups (size {Settings.GroupSize}).");

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

            var sendStopwatch = Stopwatch.StartNew();
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
                        await connection.InvokeAsync("GroupSendAsync", groupName, $"{marker}{idx:D2}:{iteration:D5}");
                    }
                }
            }));
            await Task.WhenAll(sendTasks);

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

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} streaming";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections streaming {Settings.StreamItemsPerConnection} items each.");

            var stopwatch = Stopwatch.StartNew();

            for (var index = 0; index < connections.Count; index++)
            {
                var connectionIndex = index;
                var connection = connections[index];
                var streamTask = Task.Run(async () =>
                {
                    await foreach (var item in connection.StreamAsync<int>("Counter", Settings.StreamItemsPerConnection, 0))
                    {
                        _ = item;
                        Interlocked.Increment(ref perConnection[connectionIndex]);
                        Interlocked.Increment(ref totalItems);
                    }
                });
                streamTasks.Add(streamTask);
            }

            var expected = (long)Settings.StreamConnections * Settings.StreamItemsPerConnection;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalItems), expected, Settings.StreamTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} delivery.");

            await Task.WhenAll(streamTasks);

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(Settings.StreamItemsPerConnection, $"Stream connection #{index} should receive all items.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} delivered {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} items/s.");

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

            var scenarioLabel = $"{(useOrleans ? "Orleans" : "In-Memory")} invocation";
            _output.WriteLine($"{scenarioLabel}: {connections.Count} connections × {Settings.InvocationMessagesPerConnection} invocations.");

            var stopwatch = Stopwatch.StartNew();

            var invocationTasks = connections.Select((connection, index) => Task.Run(async () =>
            {
                for (var iteration = 0; iteration < Settings.InvocationMessagesPerConnection; iteration++)
                {
                    var result = await connection.InvokeAsync<int>("Plus", iteration, index);
                    result.ShouldBe(iteration + index, "Plus result should match expected sum.");
                    Interlocked.Increment(ref perConnection[index]);
                    Interlocked.Increment(ref totalInvocations);
                }
            }));

            var workerTask = Task.WhenAll(invocationTasks);
            var expected = (long)Settings.InvocationConnections * Settings.InvocationMessagesPerConnection;
            var completed = await AwaitWithProgressAsync(() => Interlocked.Read(ref totalInvocations), expected, Settings.InvocationTimeout, scenarioLabel);
            completed.ShouldBeTrue($"Timed out awaiting {scenarioLabel} completion.");
            await workerTask;

            stopwatch.Stop();

            for (var index = 0; index < perConnection.Length; index++)
            {
                perConnection[index].ShouldBe(Settings.InvocationMessagesPerConnection, $"Invocation connection #{index} should complete all invocations.");
            }

            var throughput = expected / Math.Max(1.0, stopwatch.Elapsed.TotalSeconds);
            _output.WriteLine($"{scenarioLabel} completed {expected:N0}/{expected:N0} in {stopwatch.Elapsed}. Throughput ≈ {throughput:N0} calls/s.");

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

    private async Task<HubConnection> CreateUserConnectionAsync(TestWebApplication app, string hubName, string userId)
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

internal sealed record PerformanceScenarioSettings(
    int DeviceConnections,
    int DeviceMessagesPerConnection,
    int BroadcastConnections,
    int BroadcastMessagesPerConnection,
    int BroadcastSenderCount,
    int GroupConnections,
    int GroupSize,
    int GroupMessagesPerGroup,
    int GroupBroadcastersPerGroup,
    int StreamConnections,
    int StreamItemsPerConnection,
    int InvocationConnections,
    int InvocationMessagesPerConnection,
    TimeSpan DeviceTimeout,
    TimeSpan BroadcastTimeout,
    TimeSpan GroupTimeout,
    TimeSpan StreamTimeout,
    TimeSpan InvocationTimeout)
{
    public static PerformanceScenarioSettings CreatePerformance() => new(
        DeviceConnections: 400,
        DeviceMessagesPerConnection: 250,
        BroadcastConnections: 150,
        BroadcastMessagesPerConnection: 80,
        BroadcastSenderCount: 8,
        GroupConnections: 800,
        GroupSize: 200,
        GroupMessagesPerGroup: 180,
        GroupBroadcastersPerGroup: 8,
        StreamConnections: 200,
        StreamItemsPerConnection: 300,
        InvocationConnections: 300,
        InvocationMessagesPerConnection: 400,
        DeviceTimeout: TimeSpan.FromMinutes(5),
        BroadcastTimeout: TimeSpan.FromMinutes(5),
        GroupTimeout: TimeSpan.FromMinutes(5),
        StreamTimeout: TimeSpan.FromMinutes(5),
        InvocationTimeout: TimeSpan.FromMinutes(5));

    public static PerformanceScenarioSettings CreateStress() => new(
        DeviceConnections: 400,
        DeviceMessagesPerConnection: 150,
        BroadcastConnections: 120,
        BroadcastMessagesPerConnection: 60,
        BroadcastSenderCount: 6,
        GroupConnections: 600,
        GroupSize: 150,
        GroupMessagesPerGroup: 150,
        GroupBroadcastersPerGroup: 6,
        StreamConnections: 250,
        StreamItemsPerConnection: 250,
        InvocationConnections: 350,
        InvocationMessagesPerConnection: 300,
        DeviceTimeout: TimeSpan.FromMinutes(6),
        BroadcastTimeout: TimeSpan.FromMinutes(6),
        GroupTimeout: TimeSpan.FromMinutes(6),
        StreamTimeout: TimeSpan.FromMinutes(6),
        InvocationTimeout: TimeSpan.FromMinutes(6));
}
