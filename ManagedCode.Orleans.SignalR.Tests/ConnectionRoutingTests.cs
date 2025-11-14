using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class ConnectionRoutingTests : IAsyncLifetime
{
    private readonly SmokeClusterFixture _siloCluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private TestWebApplication? _app;

    public ConnectionRoutingTests(SmokeClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8097, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Direct_messages_should_route_between_100_connections()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        const int connectionCount = 100;
        var connections = new HubConnection[connectionCount];
        var completions = new TaskCompletionSource<string>[connectionCount];
        var connectionIds = new string[connectionCount];

        try
        {
            for (var i = 0; i < connectionCount; i++)
            {
                var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
                var index = i;
                completions[index] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                connection.On<string>("Route", payload =>
                {
                    completions[index].TrySetResult(payload);
                });

                await connection.StartAsync();
                connections[index] = connection;
                connectionIds[index] = connection.ConnectionId ?? throw new InvalidOperationException($"Connection #{index} did not expose an id.");
                _output.WriteLine($"Connection #{index} started with id {connectionIds[index]}.");
            }

            var sendTasks = new List<Task>(connectionCount);
            for (var i = 0; i < connectionCount; i++)
            {
                var targetIndex = (i + 1) % connectionCount;
                var payload = $"hop-{i}";
                sendTasks.Add(connections[i].InvokeAsync("RouteToConnection", connectionIds[targetIndex], payload));
            }

            await Task.WhenAll(sendTasks);

            var receiveTask = Task.WhenAll(completions.Select(tcs => tcs.Task));
            var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(30)));
            completed.ShouldBe(receiveTask, "Not all routed messages arrived within the expected window.");

            var results = await receiveTask;
            for (var i = 0; i < connectionCount; i++)
            {
                var expectedSenderIndex = (i - 1 + connectionCount) % connectionCount;
                var expectedSender = connectionIds[expectedSenderIndex];
                results[i].ShouldContain(expectedSender);
                results[i].ShouldContain($"hop-{expectedSenderIndex}");
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                if (connection is null)
                {
                    continue;
                }

                try
                {
                    await connection.StopAsync();
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Failed to stop connection {connection.ConnectionId}: {ex.Message}");
                }

                await connection.DisposeAsync();
            }
        }
    }
}
