using System;
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

[Collection(nameof(LongIdleClientCluster))]
public class LongIdleClientInvocationTests : IAsyncLifetime
{
    private readonly LongIdleClientClusterFixture _siloCluster;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    public LongIdleClientInvocationTests(LongIdleClientClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8100, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Client_can_invoke_after_simulated_five_minute_idle()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var connection = _app.CreateSignalRClient(nameof(SimpleTestHub));
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Handler(Exception? error)
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        }

        connection.Closed += Handler;

        try
        {
            await connection.StartAsync();
            connection.State.ShouldBe(HubConnectionState.Connected);

            var idleDuration = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(5);
            _output.WriteLine($"Waiting {idleDuration} to emulate a five-minute idle interval.");
            await Task.Delay(idleDuration);

            closed.Task.IsCompleted.ShouldBeFalse("Connection closed while waiting for idle window.");

            var sum = await connection.InvokeAsync<int>("Plus", 2, 3);
            sum.ShouldBe(5);
        }
        finally
        {
            connection.Closed -= Handler;
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }
}
