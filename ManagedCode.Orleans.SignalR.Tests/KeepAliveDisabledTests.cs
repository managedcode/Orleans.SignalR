using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class KeepAliveDisabledTests : IAsyncLifetime
{
    private readonly SmokeClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private TestWebApplication? _app;

    public KeepAliveDisabledTests(SmokeClusterFixture siloCluster, ITestOutputHelper testOutputHelper)
    {
        _siloCluster = siloCluster;
        _loggerAccessor.Output = testOutputHelper;
    }

    public async Task InitializeAsync()
    {
        _app = new TestWebApplication(
            _siloCluster,
            port: 8095,
            loggerAccessor: _loggerAccessor,
            configureServices: services =>
            {
                services.PostConfigure<OrleansSignalROptions>(options =>
                {
                    options.KeepEachConnectionAlive = false;
                });
            });

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _app?.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Targeted_connection_send_should_work_when_keep_alive_disabled()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("TestWebApplication was not initialized.");
        }

        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var receiver = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("SendAll", msg => received.TrySetResult(msg));

        try
        {
            await receiver.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();

            await sender.StartAsync();
            sender.ConnectionId.ShouldNotBeNull();

            await sender.InvokeAsync<int>("Connections", new[] { receiver.ConnectionId! });

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBe(received.Task, "Receiver did not observe direct send when keep-alive was disabled.");
            received.Task.Result.ShouldBe("test");
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Group_send_should_work_when_keep_alive_disabled()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("TestWebApplication was not initialized.");
        }

        var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var groupMessage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<string>("SendAll", msg =>
        {
            if (msg.Contains("send message:", StringComparison.Ordinal))
            {
                groupMessage.TrySetResult(msg);
            }
        });

        try
        {
            await connection.StartAsync();
            connection.ConnectionId.ShouldNotBeNull();

            const string groupName = "keepalive-group";
            await connection.InvokeAsync("AddToGroup", groupName);

            // Allow the hub to finish the "joined the group" broadcast.
            await Task.Delay(TimeSpan.FromSeconds(2));

            await connection.InvokeAsync("GroupSendAsync", groupName, "payload");

            var completed = await Task.WhenAny(groupMessage.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(groupMessage.Task, "Group broadcast did not arrive when keep-alive was disabled.");
            groupMessage.Task.Result.ShouldContain("payload");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }
}
