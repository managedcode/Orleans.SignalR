using System;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class OrleansHubLifetimeManagerShutdownTests : IAsyncLifetime
{
    private readonly SmokeClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public OrleansHubLifetimeManagerShutdownTests(SmokeClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8105, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        DisposeApp();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ApplicationStoppingShouldRemoveAllConnectionsFromCoordinator()
    {
        var app = EnsureApp();
        var first = app.CreateSignalRClient(nameof(SimpleTestHub));
        var second = app.CreateSignalRClient(nameof(SimpleTestHub));
        var firstProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstHandler = first.On("ShutdownProbe", () => firstProbe.TrySetResult(true));
        using var secondHandler = second.On("ShutdownProbe", () => secondProbe.TrySetResult(true));

        await first.StartAsync();
        await second.StartAsync();
        await first.InvokeAsync<int>("Plus", 2, 3);
        await second.InvokeAsync<int>("Plus", 3, 4);

        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
        var probeMessage = new InvocationMessage("ShutdownProbe", Array.Empty<object?>());

        var firstId = first.ConnectionId ?? throw new InvalidOperationException("First connection is missing its identifier.");
        var secondId = second.ConnectionId ?? throw new InvalidOperationException("Second connection is missing its identifier.");

        (await coordinator.SendToConnection(probeMessage, firstId)).ShouldBeTrue("Coordinator should reach the first connection before shutdown.");
        (await coordinator.SendToConnection(probeMessage, secondId)).ShouldBeTrue("Coordinator should reach the second connection before shutdown.");

        var firstDelivered = await Task.WhenAny(firstProbe.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        firstDelivered.ShouldBe(firstProbe.Task, "First connection never observed shutdown probe.");
        var secondDelivered = await Task.WhenAny(secondProbe.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        secondDelivered.ShouldBe(secondProbe.Task, "Second connection never observed shutdown probe.");

        _output.WriteLine("Disposing test host to trigger ApplicationStopping.");
        DisposeApp();
        await Task.Delay(TimeSpan.FromSeconds(1));

        var firstRemoved = await coordinator.SendToConnection(probeMessage, firstId);
        var secondRemoved = await coordinator.SendToConnection(probeMessage, secondId);
        firstRemoved.ShouldBeFalse("Coordinator still tracks the first connection after shutdown.");
        secondRemoved.ShouldBeFalse("Coordinator still tracks the second connection after shutdown.");

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ApplicationStoppingShouldFlushCoordinatorState()
    {
        var app = EnsureApp();
        var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
        var probe = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = connection.On("ShutdownProbe", () => probe.TrySetResult("pong"));

        await connection.StartAsync();
        await connection.InvokeAsync<int>("Plus", 1, 1);
        var connectionId = connection.ConnectionId ?? throw new InvalidOperationException("Connection id is missing after start.");
        _output.WriteLine("Connection established with id {0}.", connectionId);

        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(_siloCluster.Cluster.Client);
        var message = new InvocationMessage("ShutdownProbe", Array.Empty<object?>());

        var sendResult = await coordinator.SendToConnection(message, connectionId);
        sendResult.ShouldBeTrue("Coordinator could not reach active connection.");
        var delivered = await Task.WhenAny(probe.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        delivered.ShouldBe(probe.Task, "Probe invocation never reached the client.");

        DisposeApp();
        await Task.Delay(TimeSpan.FromSeconds(1));

        var staleResult = await coordinator.SendToConnection(message, connectionId);
        staleResult.ShouldBeFalse("Coordinator still tracks stale connection after shutdown.");

        await Task.Delay(TestDefaults.ClientTimeout + TimeSpan.FromSeconds(1));
        var repeatedResult = await coordinator.SendToConnection(message, connectionId);
        repeatedResult.ShouldBeFalse("Stale connection revived after expected timeout window.");

        await connection.DisposeAsync();
    }

    private TestWebApplication EnsureApp()
    {
        return _app ?? throw new InvalidOperationException("Test host is not initialised.");
    }

    private void DisposeApp()
    {
        if (_app is null)
        {
            return;
        }

        _app.Dispose();
        _app = null;
    }
}

[Collection(nameof(KeepAliveDisabledCluster))]
public class OrleansHubLifetimeManagerShutdownNoKeepAliveTests : IAsyncLifetime
{
    private readonly KeepAliveDisabledClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public OrleansHubLifetimeManagerShutdownNoKeepAliveTests(KeepAliveDisabledClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(
            _siloCluster,
            port: 8106,
            loggerAccessor: _loggerAccessor,
            configureServices: services =>
            {
                services.PostConfigure<OrleansSignalROptions>(options =>
                {
                    options.KeepEachConnectionAlive = false;
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(2);
                });
                services.PostConfigure<HubOptions>(options =>
                {
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                    options.KeepAliveInterval = null;
                });
            });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        DisposeHost();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ShutdownShouldRemoveConnectionsWithoutKeepAlive()
    {
        var app = EnsureApp();
        var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
        var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = connection.On("ShutdownProbe", () => probe.TrySetResult(true));

        await connection.StartAsync();
        await connection.InvokeAsync<int>("Plus", 1, 2);
        var connectionId = connection.ConnectionId ?? throw new InvalidOperationException("Connection identifier missing.");

        var clusterClient = _siloCluster.Cluster.Client;
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(clusterClient);
        var partitionId = await coordinator.GetPartitionForConnection(connectionId);
        var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain<SimpleTestHub>(clusterClient, partitionId);
        var message = new InvocationMessage("ShutdownProbe", Array.Empty<object?>());

        (await coordinator.SendToConnection(message, connectionId))
            .ShouldBeTrue("Coordinator failed to reach connection before shutdown when keep-alive disabled.");

        var delivered = await Task.WhenAny(probe.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        delivered.ShouldBe(probe.Task, "Connection did not receive coordinator probe before shutdown.");

        (await partitionGrain.SendToConnection(message, connectionId))
            .ShouldBeTrue("Partition grain failed to deliver probe before shutdown.");

        _output.WriteLine("Disposing disabled keep-alive host to trigger shutdown cleanup.");
        DisposeHost();
        await Task.Delay(TimeSpan.FromSeconds(1));

        var coordinatorResult = await coordinator.SendToConnection(message, connectionId);
        coordinatorResult.ShouldBeFalse("Coordinator still tracks connection after shutdown without keep-alive.");

        var partitionResult = await partitionGrain.SendToConnection(message, connectionId);
        partitionResult.ShouldBeFalse("Partition grain still tracks connection after shutdown without keep-alive.");

        await connection.DisposeAsync();
    }

    private TestWebApplication EnsureApp()
    {
        return _app ?? throw new InvalidOperationException("Test host is not initialised.");
    }

    private void DisposeHost()
    {
        if (_app is null)
        {
            return;
        }

        _app.Dispose();
        _app = null;
    }
}
