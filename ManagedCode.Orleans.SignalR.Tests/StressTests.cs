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
        const int totalConnections = 6;
        const int broadcastsPerSender = 10;
        var expectedMessages = (long)totalConnections * broadcastsPerSender;

        _outputHelper.WriteLine($"Creating {totalConnections} connections for stress broadcast test.");
        var connections = new List<HubConnection>(totalConnections);
        var observedMessages = 0L;
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var connectionIndex = 0; connectionIndex < totalConnections; connectionIndex++)
        {
            var started = await CreateStressConnectionAsync(connectionIndex, () =>
            {
                var total = Interlocked.Increment(ref observedMessages);
                if (total >= expectedMessages)
                {
                    completionSource.TrySetResult();
                }

                return total;
            });
            connections.Add(started);
        }

        var stopwatch = Stopwatch.StartNew();
        var sendTasks = connections.Select(connection => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < broadcastsPerSender; iteration++)
            {
                await connection.InvokeAsync<int>("All");
            }
        }));

        await Task.WhenAll(sendTasks);

        var finishedTask = await Task.WhenAny(completionSource.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        finishedTask.ShouldBe(completionSource.Task, $"Timed out delivering {expectedMessages:N0} broadcast messages; observed {Interlocked.Read(ref observedMessages):N0}.");

        stopwatch.Stop();
        _outputHelper.WriteLine($"Broadcast loop completed in {stopwatch.Elapsed}. Observed {Interlocked.Read(ref observedMessages):N0}/{expectedMessages:N0} messages.");

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

        for (var iteration = 0; iteration < 6; iteration++)
        {
            await hubConnection.InvokeAsync<int>("DoTest");
            await hubConnection.InvokeAsync("AddToGroup", StressGroup);
            await hubConnection.InvokeAsync("GroupSendAsync", StressGroup, $"payload-{iteration}");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        var during = await FetchCountsAsync();
        during.ConnectionTotal.ShouldBeGreaterThan(0, "Connection grains should activate after stress activity.");
        during.GroupTotal.ShouldBeGreaterThan(0, "Group grains should activate after stress activity.");
        during.User.ShouldBeGreaterThanOrEqualTo(1, "User grain should activate after stress activity.");
        _outputHelper.WriteLine($"Grain counts after activity: {during}");

        var randomResult = await hubConnection.InvokeAsync<int>("DoTest");
        randomResult.ShouldBeGreaterThan(0);

        await hubConnection.InvokeAsync("AddToGroup", StressGroup);
        await hubConnection.InvokeAsync("WaitForMessage", hubConnection.ConnectionId);

        await hubConnection.StopAsync();
        await hubConnection.DisposeAsync();
        _outputHelper.WriteLine("Stress connection disposed.");

        await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);

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
            timeout: TimeSpan.FromSeconds(15),
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
