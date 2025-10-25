using System.Diagnostics;
using System.Linq;
using Shouldly;
using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(LoadCluster))]
[Trait("Category", "Load")]
public class StressTests
{
    private const string StressGroup = "stress-group";
    private static readonly TimeSpan WaitInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1);

    private readonly TestWebApplication _firstApp;
    private readonly ITestOutputHelper _outputHelper;
    private readonly Random _random = new(42);
    private readonly TestWebApplication _secondApp;
    private readonly LoadClusterFixture _siloCluster;

    public StressTests(LoadClusterFixture testApp, ITestOutputHelper outputHelper)
    {
        _siloCluster = testApp;
        _outputHelper = outputHelper;
        _firstApp = new TestWebApplication(_siloCluster, 8081);
        _secondApp = new TestWebApplication(_siloCluster, 8082);
    }

    [Fact]
    public async Task InvokeAsyncSignalRTest()
    {
        const int batches = 3;
        const int connectionsPerBatch = 8;
        const int broadcastsPerSender = 120;
        var totalConnections = batches * connectionsPerBatch;
        var totalMessages = (long)totalConnections * broadcastsPerSender;

        _outputHelper.WriteLine($"Creating {totalConnections} connections for stress broadcast test.");
        var connections = new List<HubConnection>(totalConnections);
        var broadcastCount = 0L;

        for (var batch = 0; batch < batches; batch++)
        {
            var batchTasks = new List<Task<HubConnection>>(connectionsPerBatch);
            for (var index = 0; index < connectionsPerBatch; index++)
            {
                var connectionIndex = batch * connectionsPerBatch + index;
                batchTasks.Add(CreateStressConnectionAsync(connectionIndex, () => Interlocked.Increment(ref broadcastCount)));
            }

            var started = await Task.WhenAll(batchTasks);
            connections.AddRange(started);
            _outputHelper.WriteLine($"Started {connections.Count}/{totalConnections} connections.");
        }

        await Task.Delay(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        _outputHelper.WriteLine($"Invoking All() broadcast loop ({totalMessages:N0} messages).");

        var sendTasks = connections.Select(connection => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < broadcastsPerSender; iteration++)
            {
                await connection.InvokeAsync<int>("All");
            }
        }));
        await Task.WhenAll(sendTasks);

        var broadcastCompleted = await WaitUntilAsync(
            "all connections to observe broadcast flood",
            () => Task.FromResult(Volatile.Read(ref broadcastCount) >= totalMessages),
            timeout: TimeSpan.FromMinutes(5),
            progress: () => Task.FromResult($"received {Volatile.Read(ref broadcastCount):N0}/{totalMessages:N0} messages"));

        broadcastCompleted.ShouldBeTrue($"Broadcast flood was not observed by all connections within the allotted time (received {Volatile.Read(ref broadcastCount):N0}/{totalMessages:N0}).");

        stopwatch.Stop();
        _outputHelper.WriteLine($"Broadcast flood observed by all connections in {stopwatch.Elapsed} at {(totalMessages / Math.Max(1, stopwatch.Elapsed.TotalSeconds)):N0} msg/s.");

        await DisposeConnectionsAsync(connections);
    }

    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        _outputHelper.WriteLine("Clearing previous activations for clean state.");
        await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);

        var management = _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0);

        async Task<GrainCounts> FetchCountsAsync()
        {
            var holder = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
            var partition = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionPartitionGrain)}"));
            var groupHolder = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupGrain)}"));
            var groupPartition = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupPartitionGrain)}"));
            var invocation = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));
            var user = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRUserGrain)}"));

            return new GrainCounts(holder.Count, partition.Count, groupHolder.Count, groupPartition.Count, invocation.Count, user.Count);
        }

        var before = await FetchCountsAsync();
        _outputHelper.WriteLine($"Initial grain counts: {before}");

        var hubConnection = await CreateUserConnectionAsync("stress-user", _firstApp, nameof(SimpleTestHub));
        hubConnection.On("GetMessage", () => "connection1");

        await hubConnection.StartAsync();
        hubConnection.State.ShouldBe(HubConnectionState.Connected);
        _outputHelper.WriteLine($"Stress connection started with id {hubConnection.ConnectionId}.");

        for (var iteration = 0; iteration < 50; iteration++)
        {
            await hubConnection.InvokeAsync<int>("DoTest");
            await hubConnection.InvokeAsync("AddToGroup", StressGroup);
            await hubConnection.InvokeAsync("GroupSendAsync", StressGroup, $"payload-{iteration}");
        }

        var activationObserved = await WaitUntilAsync(
            "activated grains to appear after hub activity",
            async () =>
            {
                var counts = await FetchCountsAsync();
                if (counts.User >= 1
                    && counts.ConnectionTotal > 0
                    && counts.GroupTotal > 0)
                {
                    return true;
                }

                _outputHelper.WriteLine("Expected grains not yet activated, re-issuing hub activity to stimulate partitions.");
                await hubConnection.InvokeAsync<int>("DoTest");
                await hubConnection.InvokeAsync("AddToGroup", StressGroup);
                await hubConnection.InvokeAsync("GroupSendAsync", StressGroup, "activation-check");
                return false;
            },
            timeout: TimeSpan.FromMinutes(3),
            progress: async () => (await FetchCountsAsync()).ToString());

        activationObserved.ShouldBeTrue("Expected Orleans grains were not activated even after repeated hub activity.");

        var during = await FetchCountsAsync();
        _outputHelper.WriteLine($"Grain counts after activity: {during}");

        var randomResult = await hubConnection.InvokeAsync<int>("DoTest");
        randomResult.ShouldBeGreaterThan(0);

        await hubConnection.InvokeAsync("AddToGroup", StressGroup);
        await hubConnection.InvokeAsync("WaitForMessage", hubConnection.ConnectionId);

        await hubConnection.StopAsync();
        await hubConnection.DisposeAsync();
        _outputHelper.WriteLine("Stress connection disposed.");

        await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.FromSeconds(2));

        var deactivationObserved = await WaitUntilAsync(
            "all stress grains to deactivate",
            async () =>
            {
                var counts = await FetchCountsAsync();
                return counts.Holder == 0
                       && counts.GroupHolder == 0
                       && counts.Invocation == 0
                       && counts.User <= 1
                       && counts.ConnectionTotal <= 1
                       && counts.GroupTotal <= 1;
            },
            timeout: TimeSpan.FromSeconds(30),
            progress: async () => (await FetchCountsAsync()).ToString());

        deactivationObserved.ShouldBeTrue("Stress grains did not deactivate as expected.");

        var after = await FetchCountsAsync();
        _outputHelper.WriteLine($"Final grain counts: {after}");
    }

    private async Task<HubConnection> CreateStressConnectionAsync(int index, Func<long> onBroadcastReceived)
    {
        var useUser = _random.NextDouble() < 0.5;
        var app = _random.NextDouble() < 0.5 ? _firstApp : _secondApp;
        HubConnection connection;
        string? userId = null;

        if (useUser)
        {
            userId = $"stress-user-{index}";
            connection = await CreateUserConnectionAsync(userId, app, nameof(StressTestHub));
        }
        else
        {
            connection = app.CreateSignalRClient(nameof(StressTestHub));
        }

        connection.On("All", (string _) =>
        {
            var total = onBroadcastReceived();
            if (total % 10_000 == 0)
            {
                _outputHelper.WriteLine($"Received {total} broadcast messages so far.");
            }
        });

        connection.Closed += error =>
        {
            var reason = error is null ? "closed" : $"closed with error: {error.Message}";
            _outputHelper.WriteLine($"[stress#{index}] {reason} (ConnectionId={connection.ConnectionId ?? "n/a"}, user={userId ?? "anonymous"}).");
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);
        _outputHelper.WriteLine($"[stress#{index}] connected (ConnectionId={connection.ConnectionId}, user={userId ?? "anonymous"}).");

        if (_random.NextDouble() < 0.35)
        {
            await connection.InvokeAsync("AddToGroup", StressGroup);
            _outputHelper.WriteLine($"[stress#{index}] joined group {StressGroup}.");
        }

        return connection;
    }

    private async Task<HubConnection> CreateUserConnectionAsync(string user, TestWebApplication app, string hub)
    {
        var client = app.CreateHttpClient();
        var response = await client.GetAsync("/auth?user=" + user);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadAsStringAsync();

        var hubConnection = app.CreateSignalRClient(
            hub,
            configureConnection: options => options.AccessTokenProvider = () => Task.FromResult<string?>(token));

        return hubConnection;
    }

    private async Task DisposeConnectionsAsync(IEnumerable<HubConnection> connections)
    {
        var list = connections.ToList();
        _outputHelper.WriteLine($"Disposing {list.Count} stress connections...");
        foreach (var connection in list)
        {
            try
            {
                await connection.StopAsync();
            }
            catch (Exception ex)
            {
                _outputHelper.WriteLine($"Failed to stop {connection.ConnectionId}: {ex.Message}");
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<bool> WaitUntilAsync(
        string description,
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        Func<Task<string>>? progress = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        var start = DateTime.UtcNow;
        var lastLog = TimeSpan.Zero;

        while (DateTime.UtcNow - start < limit)
        {
            if (await condition())
            {
                _outputHelper.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                return true;
            }

            var elapsed = DateTime.UtcNow - start;
            if (elapsed - lastLog >= LogInterval)
            {
                if (progress is not null)
                {
                    var status = await progress();
                    _outputHelper.WriteLine($"Waiting for {description}... elapsed {elapsed:c}. Status: {status}");
                }
                else
                {
                    _outputHelper.WriteLine($"Waiting for {description}... elapsed {elapsed:c}.");
                }

                lastLog = elapsed;
            }

            await Task.Delay(WaitInterval);
        }

        if (progress is not null)
        {
            var status = await progress();
            _outputHelper.WriteLine($"Final status for '{description}': {status}");
        }

        return await condition();
    }

    private readonly record struct GrainCounts(
        int Holder,
        int Partition,
        int GroupHolder,
        int GroupPartition,
        int Invocation,
        int User)
    {
        public int ConnectionTotal => Holder + Partition;
        public int GroupTotal => GroupHolder + GroupPartition;

        public override string ToString() =>
            $"Connections={ConnectionTotal} (holder={Holder}, partition={Partition}); " +
            $"Groups={GroupTotal} (holder={GroupHolder}, partition={GroupPartition}); " +
            $"Invocation={Invocation}; Users={User}";
    }
}
