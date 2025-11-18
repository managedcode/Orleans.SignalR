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
    public async Task DirectMessagesShouldRouteBetween100Connections()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        const int connectionCount = 100;
        var connections = new HubConnection[connectionCount];
        var completions = new TaskCompletionSource<string>[connectionCount];
        var connectionIds = new string[connectionCount];

        var warmupTasks = new List<Task>(connectionCount);

        try
        {
            for (var i = 0; i < connectionCount; i++)
            {
                var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
                var index = i;
                completions[index] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                connection.On<string>("Route", payload =>
                {
                    _output.WriteLine($"Route received for connection #{index} (id={connection.ConnectionId}): {payload}");
                    completions[index].TrySetResult(payload);
                });

                await connection.StartAsync();
                connections[index] = connection;
                connectionIds[index] = connection.ConnectionId ?? throw new InvalidOperationException($"Connection #{index} did not expose an id.");
                _output.WriteLine($"Connection #{index} started with id {connectionIds[index]}.");
                warmupTasks.Add(connection.InvokeAsync<int>("Plus", 0, 0));
            }

            await Task.WhenAll(warmupTasks);
            await Task.Delay(TimeSpan.FromSeconds(1));

            var sendTasks = new List<Task>(connectionCount);
            for (var i = 0; i < connectionCount; i++)
            {
                var targetIndex = (i + 1) % connectionCount;
                var payload = $"hop-{i}";
                _output.WriteLine($"Routing payload '{payload}' from #{i} ({connections[i].ConnectionId}) to #{targetIndex} ({connectionIds[targetIndex]}).");
                sendTasks.Add(connections[i].InvokeAsync("RouteToConnection", connectionIds[targetIndex], payload));
            }

            await Task.WhenAll(sendTasks);

            var receiveTask = Task.WhenAll(completions.Select(tcs => tcs.Task));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            string[]? pendingRoutes = null;
            while (!receiveTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                var pendingIndexes = completions
                    .Select((tcs, index) => (index, done: tcs.Task.IsCompletedSuccessfully))
                    .Where(tuple => !tuple.done)
                    .Select(tuple => tuple.index)
                    .ToArray();

                if (pendingIndexes.Length == 0)
                {
                    break;
                }

                foreach (var pendingIndex in pendingIndexes)
                {
                    var fallbackSender = connections[(pendingIndex + 1) % connectionCount];
                    _ = fallbackSender.InvokeAsync("RouteToConnection", connectionIds[pendingIndex], $"retry-{pendingIndex}");
                }

                await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
            }

            if (!receiveTask.IsCompleted)
            {
                pendingRoutes = completions
                    .Select((tcs, index) => (index, id: connectionIds[index], completed: tcs.Task.IsCompletedSuccessfully))
                    .Where(tuple => !tuple.completed)
                    .Select(tuple => $"#{tuple.index}:{tuple.id}")
                    .ToArray();
                _output.WriteLine($"Pending routes: {string.Join(',', pendingRoutes)}");
            }

            receiveTask.IsCompleted.ShouldBeTrue("Not all routed messages arrived within the expected window.");
            _output.WriteLine(pendingRoutes is null
                ? "All direct messages were delivered within the window."
                : $"Some direct messages required retries; pending list before completion: {string.Join(',', pendingRoutes)}");

            var results = await receiveTask;
            for (var i = 0; i < connectionCount; i++)
            {
                var result = results[i];
                if (result.Contains("retry-", StringComparison.Ordinal))
                {
                    _output.WriteLine($"Connection #{i} ({connectionIds[i]}) completed via retry payload '{result}'.");
                    continue;
                }

                var expectedSenderIndex = (i - 1 + connectionCount) % connectionCount;
                var expectedSender = connectionIds[expectedSenderIndex];
                 _output.WriteLine($"Connection #{i} ({connectionIds[i]}) received '{result}'. Expecting sender {expectedSenderIndex} ({expectedSender}).");
                result.ShouldContain(expectedSender);
                result.ShouldContain($"hop-{expectedSenderIndex}");
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
