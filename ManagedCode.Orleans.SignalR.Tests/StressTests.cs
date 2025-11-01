using System.Linq;
using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Orleans.Runtime;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(LoadCluster))]
[Trait("Category", "Load")]
public class StressTests : IAsyncLifetime
{
    private readonly LoadClusterFixture _cluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly PerformanceScenarioSettings _stressSettings = PerformanceScenarioSettings.CreateStress();
    private readonly IList<TestWebApplication> _apps = new List<TestWebApplication>();

    public StressTests(LoadClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        for (var index = 0; index < 4; index++)
        {
            _apps.Add(new TestWebApplication(_cluster, 20_000 + index, loggerAccessor: _loggerAccessor));
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            app.Dispose();
        }

        return Task.CompletedTask;
    }

    private PerformanceScenarioHarness CreateHarness() =>
        new(_cluster, _output, _loggerAccessor, _stressSettings);

    [Fact]
    public async Task Stress_User_Roundtrip()
    {
        var harness = CreateHarness();
        await harness.RunDeviceEchoAsync(useOrleans: true, basePort: 30_000);
    }

    [Fact]
    public async Task Stress_Broadcast_Fanout()
    {
        var harness = CreateHarness();
        await harness.RunBroadcastFanoutAsync(useOrleans: true, basePort: 31_000);
    }

    [Fact]
    public async Task Stress_Group_Broadcast()
    {
        var harness = CreateHarness();
        await harness.RunGroupScenarioAsync(useOrleans: true, basePort: 32_000);
    }

    [Fact]
    public async Task Stress_Streaming()
    {
        var harness = CreateHarness();
        await harness.RunStreamingScenarioAsync(useOrleans: true, basePort: 33_000);
    }

    [Fact]
    public async Task Stress_Invocation()
    {
        var harness = CreateHarness();
        await harness.RunInvocationScenarioAsync(useOrleans: true, basePort: 34_000);
    }

    [Fact]
    public async Task Stress_All_Scenarios()
    {
        var harness = CreateHarness();
        var device = await harness.RunDeviceEchoAsync(true, 40_000);
        var broadcast = await harness.RunBroadcastFanoutAsync(true, 41_000);
        var group = await harness.RunGroupScenarioAsync(true, 42_000);
        var stream = await harness.RunStreamingScenarioAsync(true, 43_000);
        var invocation = await harness.RunInvocationScenarioAsync(true, 44_000);

        _output.WriteLine($"Stress cascade durations: device={device}, broadcast={broadcast}, group={group}, stream={stream}, invocation={invocation}.");
    }

    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        _output.WriteLine("Clearing previous activations for clean state.");
        await _cluster.Cluster.Client.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);

        var management = _cluster.Cluster.Client.GetGrain<IManagementGrain>(0);

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
        _output.WriteLine($"Initial grain counts: {before}");

        var hubConnection = await CreateUserConnectionAsync("stress-user", _apps[0], nameof(SimpleTestHub));
        hubConnection.On("GetMessage", () => "connection1");

        await hubConnection.StartAsync();
        hubConnection.State.ShouldBe(HubConnectionState.Connected);
        _output.WriteLine($"Stress connection started with id {hubConnection.ConnectionId}.");

        const int workflowIterations = 200;
        var workflowTasks = Enumerable.Range(0, workflowIterations)
            .Select(async iteration =>
            {
                await hubConnection.InvokeAsync<int>("DoTest");
                await hubConnection.InvokeAsync("AddToGroup", "stress-group");
                await hubConnection.InvokeAsync("GroupSendAsync", "stress-group", $"payload-{iteration}");
            });
        await Task.WhenAll(workflowTasks);

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        var during = await FetchCountsAsync();
        during.ConnectionTotal.ShouldBeGreaterThan(0, "Connection grains should activate after stress activity.");
        during.GroupTotal.ShouldBeGreaterThan(0, "Group grains should activate after stress activity.");
        during.User.ShouldBeGreaterThanOrEqualTo(1, "User grain should activate after stress activity.");
        _output.WriteLine($"Grain counts after activity: {during}");

        var randomResult = await hubConnection.InvokeAsync<int>("DoTest");
        randomResult.ShouldBeGreaterThan(0);

        await hubConnection.InvokeAsync("AddToGroup", "stress-group");
        await hubConnection.InvokeAsync("WaitForMessage", hubConnection.ConnectionId);

        await hubConnection.StopAsync();
        await hubConnection.DisposeAsync();
        _output.WriteLine("Stress connection disposed.");

        await _cluster.Cluster.Client.GetGrain<IManagementGrain>(0)
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
        _output.WriteLine($"Final grain counts: {after}");
    }

    private async Task<HubConnection> CreateUserConnectionAsync(string user, TestWebApplication app, string hub)
    {
        var client = app.CreateHttpClient();
        try
        {
            var response = await client.GetAsync("/auth?user=" + user);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadAsStringAsync();

            var hubConnection = app.CreateSignalRClient(
                hub,
                configureConnection: options => options.AccessTokenProvider = () => Task.FromResult<string?>(token));

            return hubConnection;
        }
        finally
        {
            client.Dispose();
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
        var logInterval = TimeSpan.FromSeconds(1);

        while (DateTime.UtcNow - start < limit)
        {
            if (await condition())
            {
                _output.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                return true;
            }

            var elapsed = DateTime.UtcNow - start;
            if (elapsed - lastLog >= logInterval)
            {
                if (progress is not null)
                {
                    var status = await progress();
                    _output.WriteLine($"Waiting for {description}... elapsed {elapsed:c}. Status: {status}");
                }
                else
                {
                    _output.WriteLine($"Waiting for {description}... elapsed {elapsed:c}.");
                }

                lastLog = elapsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        if (progress is not null)
        {
            var status = await progress();
            _output.WriteLine($"Final status for '{description}': {status}");
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
