using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class KeepAliveTests : IAsyncLifetime
{
    private readonly SmokeClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public KeepAliveTests(SmokeClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8096, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task KeepAlive_should_prevent_idle_disconnect()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync();
            connection.State.ShouldBe(HubConnectionState.Connected);

            _output.WriteLine("Waiting for idle interval to verify keep-alive heartbeat.");
            await Task.Delay(TestDefaults.ClientTimeout + TimeSpan.FromSeconds(2));

            connection.State.ShouldBe(HubConnectionState.Connected, "Connection dropped despite keep-alive heartbeats.");
            closed.Task.IsCompleted.ShouldBeFalse("Keep-alive connection unexpectedly closed.");

            var sum = await connection.InvokeAsync<int>("Plus", 2, 3);
            sum.ShouldBe(5);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task KeepAlive_should_cleanup_grains_after_disconnect()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var management = _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));

        try
        {
            await connection.StartAsync();
            connection.State.ShouldBe(HubConnectionState.Connected);

            await Task.Delay(TestDefaults.KeepAliveInterval + TimeSpan.FromMilliseconds(500));
            await connection.InvokeAsync<int>("Plus", 1, 1);

            var during = await GetGrainCountsAsync(management);
            _output.WriteLine($"Grain counts during connection: {during}");
            (during.Connections + during.Partitions).ShouldBeGreaterThan(0);
            during.Heartbeat.ShouldBeGreaterThan(0);

            await connection.StopAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }

        await management.ForceActivationCollection(TimeSpan.Zero);

        var cleanupObserved = await WaitUntilAsync(
            async () =>
            {
                await management.ForceActivationCollection(TimeSpan.Zero);
                var counts = await GetGrainCountsAsync(management);
                return counts.Connections == 0
                       && counts.Partitions == 0
                       && counts.Heartbeat == 0
                       && counts.Invocation == 0;
            },
            timeout: TimeSpan.FromSeconds(20));

        cleanupObserved.ShouldBeTrue("Connection-specific grains were not cleaned up after disconnect.");
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(250);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(delay).ConfigureAwait(false);
        }

        return await predicate().ConfigureAwait(false);
    }

    private static async Task<GrainCounts> GetGrainCountsAsync(IManagementGrain management)
    {
        var holder = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
        var partition = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionPartitionGrain)}"));
        var heartbeat = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHeartbeatGrain)}"));
        var invocation = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));

        return new GrainCounts(holder.Count, partition.Count, heartbeat.Count, invocation.Count);
    }

    private sealed record GrainCounts(int Connections, int Partitions, int Heartbeat, int Invocation)
    {
        public override string ToString() => $"conn={Connections}, part={Partitions}, hb={Heartbeat}, inv={Invocation}";
    }
}
