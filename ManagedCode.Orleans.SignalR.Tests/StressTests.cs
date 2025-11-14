using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public abstract class StressTestBase<TFixture> : IAsyncLifetime where TFixture : LoadClusterFixture
{
    protected StressTestBase(TFixture cluster, ITestOutputHelper output)
    {
        Cluster = cluster;
        Output = output;
        LoggerAccessor.Output = output;
    }

    protected TFixture Cluster { get; }
    protected ITestOutputHelper Output { get; }
    protected TestOutputHelperAccessor LoggerAccessor { get; } = new();
    protected PerformanceScenarioSettings StressSettings { get; } = PerformanceScenarioSettings.CreateStress();
    protected IList<TestWebApplication> Apps { get; } = new List<TestWebApplication>();

    protected virtual bool RequiresWebApps => false;

    public virtual Task InitializeAsync()
    {
        if (RequiresWebApps)
        {
            for (var index = 0; index < 4; index++)
            {
                Apps.Add(new TestWebApplication(Cluster, 20_000 + index, loggerAccessor: LoggerAccessor));
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        if (RequiresWebApps)
        {
            foreach (var app in Apps)
            {
                app.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    protected PerformanceScenarioHarness CreateHarness() =>
        new(Cluster, Output, LoggerAccessor, StressSettings);

    protected async Task<HubConnection> CreateUserConnectionAsync(string user, TestWebApplication app, string hub)
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

    protected async Task<bool> WaitUntilAsync(
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
                Output.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                return true;
            }

            var elapsed = DateTime.UtcNow - start;
            if (elapsed - lastLog >= logInterval)
            {
                if (progress is not null)
                {
                    var status = await progress();
                    Output.WriteLine($"Waiting for {description}... elapsed {elapsed:c}. Status: {status}");
                }
                else
                {
                    Output.WriteLine($"Waiting for {description}... elapsed {elapsed:c}.");
                }

                lastLog = elapsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        if (progress is not null)
        {
            var status = await progress();
            Output.WriteLine($"Final status for '{description}': {status}");
        }

        return await condition();
    }
}

[Collection(nameof(LoadClusterDevice))]
[Trait("Category", "Load")]
public sealed class StressUserRoundtripTests : StressTestBase<LoadClusterDeviceFixture>
{
    public StressUserRoundtripTests(LoadClusterDeviceFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    [Fact]
    public async Task Stress_User_Roundtrip()
    {
        var harness = CreateHarness();
        await harness.RunDeviceEchoAsync(useOrleans: true, basePort: 30_000);
    }
}

[Collection(nameof(LoadClusterBroadcast))]
[Trait("Category", "Load")]
public sealed class StressBroadcastFanoutTests : StressTestBase<LoadClusterBroadcastFixture>
{
    public StressBroadcastFanoutTests(LoadClusterBroadcastFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    [Fact]
    public async Task Stress_Broadcast_Fanout()
    {
        var harness = CreateHarness();
        await harness.RunBroadcastFanoutAsync(useOrleans: true, basePort: 31_000);
    }
}

[Collection(nameof(LoadClusterGroup))]
[Trait("Category", "Load")]
public sealed class StressGroupBroadcastTests : StressTestBase<LoadClusterGroupFixture>
{
    public StressGroupBroadcastTests(LoadClusterGroupFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    [Fact]
    public async Task Stress_Group_Broadcast()
    {
        var harness = CreateHarness();
        await harness.RunGroupScenarioAsync(useOrleans: true, basePort: 32_000);
    }
}

[Collection(nameof(LoadClusterStreaming))]
[Trait("Category", "Load")]
public sealed class StressStreamingTests : StressTestBase<LoadClusterStreamingFixture>
{
    public StressStreamingTests(LoadClusterStreamingFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    [Fact]
    public async Task Stress_Streaming()
    {
        var harness = CreateHarness();
        await harness.RunStreamingScenarioAsync(useOrleans: true, basePort: 33_000);
    }
}

[Collection(nameof(LoadClusterInvocation))]
[Trait("Category", "Load")]
public sealed class StressInvocationTests : StressTestBase<LoadClusterInvocationFixture>
{
    public StressInvocationTests(LoadClusterInvocationFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    [Fact]
    public async Task Stress_Invocation()
    {
        var harness = CreateHarness();
        await harness.RunInvocationScenarioAsync(useOrleans: true, basePort: 34_000);
    }
}

[Collection(nameof(LoadClusterCascade))]
[Trait("Category", "Load")]
public sealed class StressCascadeTests : StressTestBase<LoadClusterCascadeFixture>
{
    public StressCascadeTests(LoadClusterCascadeFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
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

        Output.WriteLine($"Stress cascade durations: device={device}, broadcast={broadcast}, group={group}, stream={stream}, invocation={invocation}.");
    }
}

[Collection(nameof(LoadClusterActivation))]
[Trait("Category", "Load")]
public sealed class StressActivationTests : StressTestBase<LoadClusterActivationFixture>
{
    public StressActivationTests(LoadClusterActivationFixture cluster, ITestOutputHelper output)
        : base(cluster, output)
    {
    }

    protected override bool RequiresWebApps => true;

    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        Output.WriteLine("Clearing previous activations for clean state.");
        await Cluster.Cluster.Client.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var management = Cluster.Cluster.Client.GetGrain<IManagementGrain>(0);

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
        Output.WriteLine($"Initial grain counts: {before}");

        var harness = CreateHarness();
        var result = await harness.RunDeviceEchoAsync(true, 45_000);
        Output.WriteLine($"Device roundtrip took {result}.");

        var hubConnection = await CreateUserConnectionAsync("stress-user", Apps[0], nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.ShouldBe(HubConnectionState.Connected);
        Output.WriteLine($"Stress connection started with id {hubConnection.ConnectionId}.");

        await hubConnection.InvokeAsync<int>("All");
        await hubConnection.InvokeAsync("AddToGroup", "stress-group");
        await hubConnection.StopAsync();
        await hubConnection.DisposeAsync();
        Output.WriteLine("Stress connection disposed.");

        var during = await FetchCountsAsync();
        during.ConnectionTotal.ShouldBeGreaterThan(0, "Connection grains should activate after stress activity.");
        during.GroupTotal.ShouldBeGreaterThan(0, "Group grains should activate after stress activity.");
        during.User.ShouldBeGreaterThanOrEqualTo(1, "User grain should activate after stress activity.");
        Output.WriteLine($"Grain counts after activity: {during}");

        await Cluster.Cluster.Client.GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);

        var deactivationWatch = Stopwatch.StartNew();
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
        deactivationWatch.Stop();

        deactivationObserved.ShouldBeTrue("Stress grains did not deactivate as expected.");

        var after = await FetchCountsAsync();
        Output.WriteLine($"Final grain counts: {after} (deactivation check took {deactivationWatch.Elapsed}).");
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
