using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public sealed class ConnectionRegistrationShutdownTests(SmokeClusterFixture cluster)
{
    [Fact]
    public async Task ShutdownDuringConnectionRegistrationDoesNotSubscribeADisposedObserverAsync()
    {
        var timeout = TimeSpan.FromSeconds(10);
        using var gate = ConnectionRegistrationCallFilter.Arm();
        await using var app = new TestWebApplication(cluster);
        await using var client = app.CreateSignalRClient(nameof(SimpleTestHub),
            configure: builder => builder.WithAutomaticReconnect([]));
        try
        {
            await client.StartAsync();
            var connectionId = await gate.WaitUntilPausedAsync(timeout);
            connectionId.ShouldBe(client.ConnectionId);
            app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            gate.Release();
            await gate.WaitForCleanupAsync(timeout);
            gate.NullObserverAttempts.ShouldBe(0,
                "Connection registration resumed after normal shutdown must never send a null observer to Orleans.");
            await client.StopAsync().WaitAsync(timeout);
            await app.DisposeAsync();

            var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(cluster.Cluster.Client);
            (await coordinator.SendToConnection(new InvocationMessage("ShutdownProbe", []), connectionId)).ShouldBeFalse();
        }
        finally
        {
            gate.Release();
        }
    }

}
