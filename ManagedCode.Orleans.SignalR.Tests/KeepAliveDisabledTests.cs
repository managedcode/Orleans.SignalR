using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
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

[Collection(nameof(KeepAliveDisabledCluster))]
public class KeepAliveDisabledTests : IAsyncLifetime
{
    private readonly KeepAliveDisabledClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public KeepAliveDisabledTests(KeepAliveDisabledClusterFixture siloCluster, ITestOutputHelper testOutputHelper)
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
                services.PostConfigure<HubOptions>(options =>
                {
                    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                    options.KeepAliveInterval = null;
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
            var receivedMessage = await received.Task;
            receivedMessage.ShouldBe("test");

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
    public async Task Idle_connection_should_receive_direct_send_after_idle_window()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("TestWebApplication was not initialized.");
        }

        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var receiver = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var routed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("Route", payload => routed.TrySetResult(payload));

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            var idle = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"Waiting {idle} before sending targeted message without Orleans keep-alive.");
            await Task.Delay(idle);

            await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, "idle-route");

            var completed = await Task.WhenAny(routed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBe(routed.Task, "Receiver did not get targeted send after idle period.");
            var payload = await routed.Task;
            payload.ShouldContain(sender.ConnectionId!);
            payload.ShouldContain("idle-route");
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
    public async Task KeepAlive_disabled_should_preserve_user_delivery_after_idle_interval()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("TestWebApplication was not initialized.");
        }

        using var httpClient = _app.CreateHttpClient();
        var userId = $"user-{Guid.NewGuid():N}";
        var token = await httpClient.GetStringAsync($"/auth?user={userId}");

        HubConnection CreateAuthenticatedConnection()
        {
                return _app.CreateSignalRClient(
                    nameof(SimpleTestHub),
                    configureConnection: options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    });
        }

        var receiver = CreateAuthenticatedConnection();
        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var payload = $"keepalive-disabled-user-{Guid.NewGuid():N}";
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("SendAll", message =>
        {
            if (message.Contains(payload, StringComparison.Ordinal))
            {
                delivered.TrySetResult(message);
            }
        });

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();

            await Task.Delay(TestDefaults.ClientTimeout + TimeSpan.FromSeconds(1));

            await sender.InvokeAsync("SentToUser", userId, payload);
            var completed = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.ShouldBe(delivered.Task, "User-targeted send failed after idle interval when keep-alive disabled.");

            var content = await delivered.Task;
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

            var groupName = $"keepalive-group-{Guid.NewGuid():N}";
            await connection.InvokeAsync("AddToGroup", groupName);
            await connection.InvokeAsync<int>("Plus", 0, 0);

            // Allow the hub to finish the "joined the group" broadcast and cross the timeout window.
            await Task.Delay(TestDefaults.ClientTimeout + TimeSpan.FromSeconds(1));
            connection.State.ShouldBe(HubConnectionState.Connected, "Connection should remain active before group send.");

            await connection.InvokeAsync("GroupSendAsync", groupName, "payload");

            var groupPayload = await WaitForMessageAsync(groupMessage.Task, "keep-alive disabled group broadcast");
            groupPayload.ShouldContain("payload");

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

    [Fact]
    public async Task Active_targeted_send_should_not_drop_when_keep_alive_disabled()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("TestWebApplication was not initialized.");
        }

        const int messageCount = 6;
        var receiver = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var payloads = new List<string>(messageCount);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.On<string>("Route", message =>
        {
            lock (payloads)
            {
                payloads.Add(message);
                if (payloads.Count == messageCount)
                {
                    delivered.TrySetResult(true);
                }
            }
        });

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            for (var i = 0; i < messageCount; i++)
            {
                await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, $"keepalive-disabled-{i}");
                await Task.Delay(TimeSpan.FromMilliseconds(600));
            }

            var completed = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(delivered.Task, "Not all targeted sends arrived while keep-alive was disabled.");

            lock (payloads)
            {
                payloads.Count.ShouldBe(messageCount);
                payloads.Last().ShouldContain($"keepalive-disabled-{messageCount - 1}");
            }
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }
    }

    private static Task<string> WaitForMessageAsync(Task<string> task, string description)
    {
        return task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
