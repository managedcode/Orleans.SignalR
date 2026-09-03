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
    public async Task ServerCanPushAfterSimulatedFiveMinuteIdleAsync()
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
            var receiverConnectionId = receiver.ConnectionId;

            var idleDuration = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"Waiting {idleDuration} without application traffic, beyond the six-second activation collection age.");
            await Task.Delay(idleDuration);
            receiver.State.ShouldBe(HubConnectionState.Connected, "Receiver disconnected during idle interval.");
            sender.State.ShouldBe(HubConnectionState.Connected, "Sender disconnected during idle interval.");
            receiver.ConnectionId.ShouldBe(receiverConnectionId, "Receiver reconnected instead of preserving the original connection.");

            await sender.InvokeAsync("RouteToConnection", receiverConnectionId!, payload);
            var content = await routed.Task.WaitAsync(TestDefaults.ClientTimeout);
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
}
