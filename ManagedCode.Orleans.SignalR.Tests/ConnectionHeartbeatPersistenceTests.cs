using System.Collections.Immutable;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(HeartbeatWriteFailureCluster))]
public sealed class ConnectionHeartbeatPersistenceTests(HeartbeatWriteFailureClusterFixture cluster)
{
    [Fact]
    public async Task IdenticalStartRetriesPersistenceAfterFirstWriteFailsAsync()
    {
        var client = cluster.Cluster.Client;
        var connectionId = $"heartbeat-retry-{Guid.NewGuid():N}";
        var heartbeat = NameHelperGenerator.GetConnectionHeartbeatGrain(
            client,
            typeof(SimpleTestHub).FullName!,
            connectionId);
        var heartbeatId = ((GrainReference)heartbeat).GrainId;
        using var localObserver = new SignalRObserver(_ => Task.CompletedTask);
        var observer = client.CreateObjectReference<ISignalRObserver>(localObserver);
        var registration = new ConnectionHeartbeatRegistration(
            typeof(SimpleTestHub).FullName!,
            false,
            0,
            observer,
            TimeSpan.FromSeconds(5),
            ImmutableArray<GrainId>.Empty,
            connectionId);

        try
        {
            SharedTestGrainStorage.ClearWriteEvidence(nameof(SignalRConnectionHeartbeatGrain), heartbeatId);
            await Should.ThrowAsync<OrleansException>(() => heartbeat.Start(registration));
            await heartbeat.Start(registration);

            SharedTestGrainStorage.HasSuccessfulWrite(nameof(SignalRConnectionHeartbeatGrain), heartbeatId)
                .ShouldBeTrue("The identical renewal must retry the failed durable write.");
        }
        finally
        {
            await heartbeat.Stop();
            client.DeleteObjectReference<ISignalRObserver>(observer);
        }
    }
}
