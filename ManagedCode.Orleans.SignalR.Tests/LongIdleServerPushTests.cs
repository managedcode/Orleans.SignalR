using System;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
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
        _app = new TestWebApplication(
            _siloCluster,
            port: 8101,
            loggerAccessor: _loggerAccessor,
            configureServices: services =>
            {
                services.PostConfigure<HubOptions>(options =>
                {
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
                    options.KeepAliveInterval = TimeSpan.FromSeconds(5);
                });
            });
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

        using var keepAliveCts = new CancellationTokenSource();
        Task? keepAliveTask = null;

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            keepAliveTask = Task.Run(async () =>
            {
                while (!keepAliveCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await receiver.InvokeAsync<int>("Plus", 0, 0, keepAliveCts.Token);
                    }
                    catch
                    {
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), keepAliveCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, keepAliveCts.Token);

            var idleDuration = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"Waiting {idleDuration} to emulate a five-minute idle interval before server push.");
            await Task.Delay(idleDuration);
            receiver.State.ShouldBe(HubConnectionState.Connected, "Receiver disconnected during idle interval.");
            sender.State.ShouldBe(HubConnectionState.Connected, "Sender disconnected during idle interval.");

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

            // Force client reconnection after the collection window to simulate real SignalR behaviour.
            await receiver.StopAsync();
            await sender.StopAsync();

            await receiver.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();

            await sender.StartAsync();
            sender.ConnectionId.ShouldNotBeNull();

            routed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, payload);
            var content = await routed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            content.ShouldContain(sender.ConnectionId!);
            content.ShouldContain(payload);
        }
        finally
        {
            keepAliveCts.Cancel();
            if (keepAliveTask is not null)
            {
                try
                {
                    await keepAliveTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
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
