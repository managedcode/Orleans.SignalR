using System;
using System.Net.Http;
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
public class ReconnectionTests : IAsyncLifetime
{
    private readonly SmokeClusterFixture _siloCluster;
    private readonly ITestOutputHelper _output;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private TestWebApplication? _app;

    public ReconnectionTests(SmokeClusterFixture siloCluster, ITestOutputHelper output)
    {
        _siloCluster = siloCluster;
        _output = output;
        _loggerAccessor.Output = output;
    }

    public Task InitializeAsync()
    {
        _app = new TestWebApplication(_siloCluster, port: 8098, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reconnected_user_should_receive_pending_messages()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var userId = $"user-{Guid.NewGuid():N}";
        using var httpClient = _app.CreateHttpClient();
        var token = await httpClient.GetStringAsync($"/auth?user={userId}");

        HubConnection CreateAuthenticatedConnection()
        {
            return _app.CreateSignalRClient(
                nameof(SimpleTestHub),
                configureConnection: options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(token);
                });
        }

        var receiver = CreateAuthenticatedConnection();
        var sender = _app.CreateSignalRClient(nameof(SimpleTestHub));
        HubConnection? reconnected = null;

        try
        {
            await receiver.StartAsync();
            receiver.ConnectionId.ShouldNotBeNull();
            await sender.StartAsync();

            await receiver.StopAsync();
            await receiver.DisposeAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            const string payload = "reconnect-payload";
            await sender.InvokeAsync("SentToUser", userId, payload);

            reconnected = CreateAuthenticatedConnection();
            var replayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            reconnected.On<string>("SendAll", message => replayed.TrySetResult(message));

            await reconnected.StartAsync();
            var completed = await Task.WhenAny(replayed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            completed.ShouldBe(replayed.Task, "Reconnected user did not receive queued message.");

            var received = await replayed.Task;
            received.ShouldContain(payload);
        }
        finally
        {
            if (reconnected is not null)
            {
                await reconnected.StopAsync();
                await reconnected.DisposeAsync();
            }

            await sender.StopAsync();
            await sender.DisposeAsync();
        }
    }
}
