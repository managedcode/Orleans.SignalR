using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[CollectionDefinition(nameof(DirectedRoutingRecovery), DisableParallelization = true)]
public sealed class DirectedRoutingRecovery;

[Collection(nameof(DirectedRoutingRecovery))]
public sealed class DirectedRoutingRecoveryTests
{
    [Fact]
    public async Task RemoteAddressedMessageReachesOriginalConnectionAfterRoutingSiloStopsAsync()
    {
        using var fixture = new SingleSiloCluster();
        var cluster = fixture.Cluster;
        var routingSilo = cluster.Silos.Single();
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(cluster.Client);
        (await coordinator.GetPartitionCount()).ShouldBe((int)TestDefaults.ConnectionPartitions);

        var remainingSilo = (InProcessSiloHandle)await cluster.StartAdditionalSiloAsync();
        await cluster.WaitForLivenessToStabilizeAsync(true);
        var remainingClient = remainingSilo.ServiceProvider.GetRequiredService<IClusterClient>();
        var management = remainingClient.GetGrain<IManagementGrain>(0);
        (await management.GetActivationAddress(coordinator)).ShouldBe(routingSilo.SiloAddress);

        using var receiver = new TestWebApplication(fixture, configureServices: services => services.AddSingleton(remainingClient));
        using var sender = new TestWebApplication(fixture, configureServices: services => services.AddSingleton(remainingClient));
        await using var connection = receiver.CreateSignalRClient(nameof(SimpleTestHub));
        await connection.StartAsync();
        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 1, 2)).ShouldBe(3);
        var connectionId = connection.ConnectionId.ShouldNotBeNull();
        var hub = sender.Services.GetRequiredService<IHubContext<SimpleTestHub>>();
        await AssertDeliveryAsync(hub, connection, connectionId, "before routing silo loss");

        await cluster.KillSiloAsync(routingSilo);
        await cluster.WaitForLivenessToStabilizeAsync(true);

        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 2, 3)).ShouldBe(5);
        connection.ConnectionId.ShouldBe(connectionId);
        await AssertDeliveryAsync(hub, connection, connectionId, "after routing silo loss");
        connection.ConnectionId.ShouldBe(connectionId);
        await AssertMixedDeliveryAsync(sender, hub, connection, connectionId);
    }

    private static async Task AssertMixedDeliveryAsync(TestWebApplication sender, IHubContext<SimpleTestHub> hub,
        HubConnection connection, string connectionId)
    {
        await using var localConnection = sender.CreateSignalRClient(nameof(SimpleTestHub));
        await localConnection.StartAsync();
        (await localConnection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 2, 3)).ShouldBe(5);
        var localId = localConnection.ConnectionId.ShouldNotBeNull();
        const string message = "mixed local and remote batch";
        var localDelivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteDelivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var localSubscription = localConnection.On<string>("Route", delivery => localDelivery.TrySetResult(delivery));
        using var remoteSubscription = connection.On<string>("Route", delivery => remoteDelivery.TrySetResult(delivery));
        await hub.Clients.Clients([localId, connectionId]).SendAsync("Route", message);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (await localDelivery.Task.WaitAsync(deadline.Token)).ShouldBe(message);
        (await remoteDelivery.Task.WaitAsync(deadline.Token)).ShouldBe(message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAddressedMessageReachesOriginalConnectionDuringBackplaneOutageAsync(bool batch)
    {
        using var fixture = new SingleSiloCluster();
        var cluster = fixture.Cluster;
        var routingSilo = cluster.Silos.Single();
        using var app = new TestWebApplication(fixture);
        await using var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
        await connection.StartAsync();
        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 1, 2)).ShouldBe(3);
        var connectionId = connection.ConnectionId.ShouldNotBeNull();
        var hub = app.Services.GetRequiredService<IHubContext<SimpleTestHub>>();
        await AssertDeliveryAsync(hub, connection, connectionId, "before backplane outage", batch);

        await cluster.KillSiloAsync(routingSilo);

        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 2, 3)).ShouldBe(5);
        connection.ConnectionId.ShouldBe(connectionId);
        await AssertDeliveryAsync(hub, connection, connectionId, "during backplane outage", batch);
        connection.ConnectionId.ShouldBe(connectionId);
    }

    private static async Task AssertDeliveryAsync(IHubContext<SimpleTestHub> hub, HubConnection connection,
        string connectionId, string message, bool batch = false)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.On<string>("Route", delivery => received.TrySetResult(delivery));
        var target = batch ? hub.Clients.Clients([connectionId]) : hub.Clients.Client(connectionId);
        await target.SendAsync("Route", $"{connectionId}:{message}");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(60))).ShouldBe($"{connectionId}:{message}");
    }

    private sealed class SingleSiloCluster() : ClusterFixtureBase(builder => builder.Options.InitialSilosCount = 1);
}
