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
    public async Task AddressedMessageReachesOriginalConnectionAfterRoutingSiloStopsAsync()
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

        using var app = new TestWebApplication(fixture, configureServices: services => services.AddSingleton(remainingClient));
        await using var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
        await connection.StartAsync();
        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 1, 2)).ShouldBe(3);
        var connectionId = connection.ConnectionId.ShouldNotBeNull();
        var hub = app.Services.GetRequiredService<IHubContext<SimpleTestHub>>();
        await AssertDeliveryAsync(hub, connection, connectionId, "before routing silo loss");

        await cluster.KillSiloAsync(routingSilo);
        await cluster.WaitForLivenessToStabilizeAsync(true);

        (await connection.InvokeAsync<int>(nameof(SimpleTestHub.Plus), 2, 3)).ShouldBe(5);
        connection.ConnectionId.ShouldBe(connectionId);
        await AssertDeliveryAsync(hub, connection, connectionId, "after routing silo loss");
        connection.ConnectionId.ShouldBe(connectionId);
    }

    private static async Task AssertDeliveryAsync(IHubContext<SimpleTestHub> hub, HubConnection connection,
        string connectionId, string message)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.On<string>("Route", delivery => received.TrySetResult(delivery));
        await hub.Clients.Client(connectionId).SendAsync("Route", $"{connectionId}:{message}");
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(60))).ShouldBe($"{connectionId}:{message}");
    }

    private sealed class SingleSiloCluster() : ClusterFixtureBase(builder => builder.Options.InitialSilosCount = 1);
}
