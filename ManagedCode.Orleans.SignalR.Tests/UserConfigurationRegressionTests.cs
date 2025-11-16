using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
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

[Collection(nameof(UserConfigurationCluster))]
public class UserConfigurationRegressionTests : IAsyncLifetime
{
    private static readonly TimeSpan SignalRKeepAlive = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SignalRClientTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignalRHandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OrleansClientTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MessageRetention = TimeSpan.FromMinutes(1.1);

    private readonly UserConfigurationClusterFixture _cluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public UserConfigurationRegressionTests(UserConfigurationClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(
            _cluster,
            port: 8124,
            loggerAccessor: _loggerAccessor,
            configureServices: services =>
            {
                services.PostConfigure<HubOptions>(options =>
                {
                    options.KeepAliveInterval = SignalRKeepAlive;
                    options.ClientTimeoutInterval = SignalRClientTimeout;
                    options.HandshakeTimeout = SignalRHandshakeTimeout;
                });

                services.PostConfigure<OrleansSignalROptions>(options =>
                {
                    options.ClientTimeoutInterval = OrleansClientTimeout;
                    options.KeepEachConnectionAlive = false;
                    options.KeepMessageInterval = MessageRetention;
                    options.ConnectionPartitionCount = 1;
                    options.GroupPartitionCount = 1;
                    options.ConnectionsPerPartitionHint = 1_024;
                    options.GroupsPerPartitionHint = 64;
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
    public async Task Targeted_send_should_survive_idle_with_user_configuration()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var receiver = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));

        var routed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("Route", payload => routed.TrySetResult(payload));

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            var idleDuration = SignalRClientTimeout + TimeSpan.FromSeconds(15);
            _output.WriteLine($"Waiting {idleDuration} with user-provided configuration before sending targeted message.");
            await Task.Delay(idleDuration);

            receiver.State.ShouldBe(HubConnectionState.Connected, "Receiver disconnected before targeted send.");

            await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, "user-config-route");

            var completed = await Task.WhenAny(routed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(routed.Task, "Receiver did not get targeted send after idle interval under user configuration.");

            var payload = await routed.Task;
            payload.ShouldContain(sender.ConnectionId!);
            payload.ShouldContain("user-config-route");
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
    public async Task Iot_workload_should_process_group_broadcast_and_streaming_after_idle()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        const int deviceCount = 3;
        const int firstListenerIndex = 1;
        const string groupName = "iot-devices";
        var devices = new HubConnection[deviceCount];
        var controller = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var broadcastReceipts = new TaskCompletionSource<string>[deviceCount];
        var groupReceipts = new TaskCompletionSource<string>[deviceCount];

        for (var i = 0; i < deviceCount; i++)
        {
            broadcastReceipts[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            groupReceipts[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
            var index = i;
            connection.On<string>("PerfBroadcast", payload => broadcastReceipts[index].TrySetResult(payload));
            connection.On<string>("SendAll", payload =>
            {
                if (payload.Contains("iot-group-payload", StringComparison.Ordinal))
                {
                    groupReceipts[index].TrySetResult(payload);
                }
            });

            devices[i] = connection;
        }

        try
        {
            await controller.StartAsync();
            controller.ConnectionId.ShouldNotBeNull();

            foreach (var connection in devices)
            {
                await connection.StartAsync();
                connection.ConnectionId.ShouldNotBeNull();
            }

            await Task.WhenAll(devices.Select(device => device.InvokeAsync("AddToGroup", groupName)));
            await Task.Delay(TimeSpan.FromSeconds(1));

            var idleDuration = SignalRClientTimeout + TimeSpan.FromSeconds(15);
            _output.WriteLine($"[IoT] Waiting {idleDuration} before validating message fan-out.");
            await Task.Delay(idleDuration);

            controller.State.ShouldBe(HubConnectionState.Connected, "[IoT] Controller disconnected before validation.");
            foreach (var connection in devices)
            {
                connection.State.ShouldBe(HubConnectionState.Connected, "[IoT] Device disconnected before validation.");
            }

            for (var i = firstListenerIndex; i < deviceCount; i++)
            {
                broadcastReceipts[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await controller.InvokeAsync("BroadcastPayload", "iot-broadcast");
            for (var i = firstListenerIndex; i < deviceCount; i++)
            {
                var completed = await Task.WhenAny(broadcastReceipts[i].Task, Task.Delay(TimeSpan.FromSeconds(10)));
                completed.ShouldBe(broadcastReceipts[i].Task, $"Broadcast payload was not observed by device index {i} (state={devices[i].State}).");
                var broadcastPayload = await broadcastReceipts[i].Task;
                broadcastPayload.ShouldBe("iot-broadcast");
            }

            for (var i = firstListenerIndex; i < deviceCount; i++)
            {
                groupReceipts[i] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await controller.InvokeAsync("GroupSendAsync", groupName, "iot-group-payload");
            for (var i = firstListenerIndex; i < deviceCount; i++)
            {
                var completed = await Task.WhenAny(groupReceipts[i].Task, Task.Delay(TimeSpan.FromSeconds(10)));
                completed.ShouldBe(groupReceipts[i].Task, "Group payload did not reach every device.");
                var groupPayload = await groupReceipts[i].Task;
                groupPayload.ShouldContain("iot-group-payload");
            }

            TestWebApplication.StaticLogs[nameof(SimpleTestHub.UploadStreamChannelReader)] = new ConcurrentQueue<string>();
            var channel = Channel.CreateUnbounded<string>();
            var upload = devices[2].InvokeAsync("UploadStreamChannelReader", channel.Reader);
            await channel.Writer.WriteAsync("iot-stream-0");
            await channel.Writer.WriteAsync("iot-stream-1");
            channel.Writer.TryComplete();
            await upload;

            TestWebApplication.StaticLogs.TryGetValue(nameof(SimpleTestHub.UploadStreamChannelReader), out var streamLog).ShouldBeTrue();
            streamLog!.Count.ShouldBeGreaterThanOrEqualTo(2);

            var serverStream = new List<int>();
            await foreach (var value in devices[0].StreamAsync<int>("Counter", 3, 10))
            {
                serverStream.Add(value);
            }
            serverStream.ShouldBe(new[] { 0, 1, 2 });
        }
        finally
        {
            foreach (var connection in devices)
            {
                await connection.StopAsync();
                await connection.DisposeAsync();
            }

            await controller.StopAsync();
            await controller.DisposeAsync();
        }
    }
}
