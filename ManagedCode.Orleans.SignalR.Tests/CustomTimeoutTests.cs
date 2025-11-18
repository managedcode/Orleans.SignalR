using System;
using System.Collections.Generic;
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

[Collection(nameof(SmokeCluster))]
public class CustomTimeoutTests
{
    private readonly SmokeClusterFixture _cluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();

    public CustomTimeoutTests(SmokeClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public static IEnumerable<object[]> TimeoutConfigurations()
    {
        yield return new object[] { "default-keepalive", 0.5, 5.0, true, 8120 };
        yield return new object[] { "extended-keepalive", 2.0, 10.0, true, 8121 };
        yield return new object[] { "keepalive-disabled", 2.0, 10.0, false, 8122 };
    }

    [Theory]
    [MemberData(nameof(TimeoutConfigurations))]
    public async Task DirectSendShouldSurviveIdleWithCustomTimeouts(
        string scenario,
        double keepAliveSeconds,
        double clientTimeoutSeconds,
        bool keepEachConnectionAlive,
        int port)
    {
        var keepAlive = TimeSpan.FromSeconds(keepAliveSeconds);
        var clientTimeout = TimeSpan.FromSeconds(clientTimeoutSeconds);

        using var app = new TestWebApplication(
            _cluster,
            port: port,
            loggerAccessor: _loggerAccessor,
            configureServices: services =>
            {
                services.PostConfigure<HubOptions>(options =>
                {
                    options.KeepAliveInterval = keepAlive;
                    options.ClientTimeoutInterval = clientTimeout;
                });

                services.PostConfigure<OrleansSignalROptions>(options =>
                {
                    options.KeepEachConnectionAlive = keepEachConnectionAlive;
                    options.ClientTimeoutInterval = clientTimeout;
                });
            });

        var receiver = app.CreateSignalRClient(nameof(SimpleTestHub));
        var sender = app.CreateSignalRClient(nameof(SimpleTestHub));
        var routed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string>("Route", payload => routed.TrySetResult(payload));

        try
        {
            await receiver.StartAsync();
            await sender.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            sender.ConnectionId.ShouldNotBeNull();

            var idle = clientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"[{scenario}] Waiting {idle} (keepAlive={keepAliveSeconds}s, timeout={clientTimeoutSeconds}s, KeepEachConnectionAlive={keepEachConnectionAlive}).");
            await Task.Delay(idle);

            await sender.InvokeAsync("RouteToConnection", receiver.ConnectionId!, $"custom-{scenario}");
            var completed = await Task.WhenAny(routed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(routed.Task, $"[{scenario}] Receiver did not observe targeted send after idle interval.");

            var payload = await routed.Task;
            payload.ShouldContain(sender.ConnectionId!);
            payload.ShouldContain($"custom-{scenario}");
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }
    }
}
