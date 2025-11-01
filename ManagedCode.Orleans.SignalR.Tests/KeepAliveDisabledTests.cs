using System.Diagnostics;
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
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public KeepAliveDisabledTests(SmokeClusterFixture siloCluster, ITestOutputHelper testOutputHelper)
    {
        _siloCluster = siloCluster;
        _loggerAccessor.Output = testOutputHelper;
        _output = testOutputHelper;
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
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(2);
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

            var receiverDisconnected = await WaitForDisconnectAsync(receiver, TimeSpan.FromSeconds(5), "target receiver");
            _output.WriteLine(receiverDisconnected
                ? "Receiver disconnected after timeout as expected."
                : "Receiver remained connected after timeout window.");

            var senderDisconnected = await WaitForDisconnectAsync(sender, TimeSpan.FromSeconds(5), "target sender");
            _output.WriteLine(senderDisconnected
                ? "Sender disconnected after timeout as expected."
                : "Sender remained connected after timeout window.");
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

            var disconnected = await WaitForDisconnectAsync(connection, TimeSpan.FromSeconds(5), "group connection");
            _output.WriteLine(disconnected
                ? "Group connection disconnected after timeout as expected."
                : "Group connection remained connected after timeout window.");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    private async Task<bool> WaitForDisconnectAsync(HubConnection connection, TimeSpan timeout, string label)
    {
        if (connection.State == HubConnectionState.Disconnected)
        {
            _output.WriteLine($"{label} already disconnected.");
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Handler(Exception? _)
        {
            tcs.TrySetResult(true);
            return Task.CompletedTask;
        }

        connection.Closed += Handler;

        try
        {
            var watch = Stopwatch.StartNew();
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            watch.Stop();

            if (completedTask == tcs.Task)
            {
                await tcs.Task;
                _output.WriteLine($"{label} disconnected after {watch.Elapsed}.");
                return true;
            }

            _output.WriteLine($"{label} still connected after {watch.Elapsed}.");
            return false;
        }
        finally
        {
            connection.Closed -= Handler;
        }
    }
}
