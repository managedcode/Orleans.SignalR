using System;
using System.Threading.Tasks;
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

[Collection(nameof(LongIdleServerCluster))]
public class LongIdleServerPushTests : IAsyncLifetime
{
    private readonly LongIdleServerClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public LongIdleServerPushTests(LongIdleServerClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8101, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Server_can_push_after_simulated_five_minute_idle()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var receiver = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var payload = "idle-server-push";
        var routed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("Route", message => routed.TrySetResult(message));

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            var idleDuration = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"Waiting {idleDuration} to emulate a five-minute idle interval before server push.");
            await Task.Delay(idleDuration);

            var management = _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0);
            await management.ForceActivationCollection(TimeSpan.Zero);
            _ = await WaitUntilAsync(
                async () =>
                {
                    var partitions = await management.GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionPartitionGrain)}"));
                    return partitions.Count == 0;
                },
                TimeSpan.FromSeconds(5));

            await Task.Delay(TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5));

            await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, payload);
            var completed = await Task.WhenAny(routed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBe(routed.Task, "Receiver did not observe targeted send after idle interval.");

            var content = await routed.Task;
            content.ShouldContain(sender.ConnectionId!);
            content.ShouldContain(payload);
        }
        finally
        {
            await receiver.StopAsync();
            await sender.StopAsync();
            await receiver.DisposeAsync();
            await sender.DisposeAsync();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(200);
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
}
