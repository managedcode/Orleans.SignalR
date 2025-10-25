using System.Collections.Concurrent;
using Shouldly;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class HubSmokeTests
{
    private const string HubName = nameof(SimpleTestHub);
    private readonly TestWebApplication _firstApp;
    private readonly TestWebApplication _secondApp;
    private readonly SmokeClusterFixture _cluster;
    private readonly ITestOutputHelper _output;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public HubSmokeTests(SmokeClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
        _firstApp = new TestWebApplication(_cluster, 8081);
        _secondApp = new TestWebApplication(_cluster, 8082);
    }

    [Fact]
    public async Task SingleConnection_CanInvokeServerMethod()
    {
        var connection = _firstApp.CreateSignalRClient(HubName);

        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);

        await Task.Delay(100);
        var result = await connection.InvokeAsync<int>("Plus", 2, 3);
        result.ShouldBe(5);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Broadcast_ReachesBothServers()
    {
        var message1 = string.Empty;
        var message2 = string.Empty;

        var connection1 = await StartConnectionAsync(_firstApp, msg => message1 = msg, null, _output);
        var connection2 = await StartConnectionAsync(_secondApp, msg => message2 = msg, null, _output);

        await Task.Delay(100);
        await connection1.InvokeAsync<int>("All");

        await WaitUntilAsync(() => !string.IsNullOrEmpty(message1) && !string.IsNullOrEmpty(message2));

        message1.ShouldBe("test");
        message2.ShouldBe("test");

        await DisposeAsync(connection1, connection2);
    }

    [Fact]
    public async Task GroupBroadcast_ReachesMembersAcrossSilos()
    {
        var messages = new ConcurrentDictionary<string, string>();

        var connection1 = await StartConnectionAsync(_firstApp, msg => messages["c1"] = msg, null, _output);
        var connection2 = await StartConnectionAsync(_secondApp, msg => messages["c2"] = msg, null, _output);

        await connection1.InvokeAsync("AddToGroup", "test-group");
        await connection2.InvokeAsync("AddToGroup", "test-group");

        await Task.Delay(100);
        await connection1.InvokeAsync("GroupSendAsync", "test-group", "hello");

        await WaitUntilAsync(() =>
            messages.TryGetValue("c1", out var m1) && m1.Contains("hello") &&
            messages.TryGetValue("c2", out var m2) && m2.Contains("hello"));

        await DisposeAsync(connection1, connection2);
    }

    [Fact]
    public async Task UserMessage_IsDeliveredToSpecificUser()
    {
        var httpClient = _firstApp.CreateHttpClient();
        var response = await httpClient.GetAsync("/auth?user=SmokeUser");
        var token = await response.Content.ReadAsStringAsync();

        var received = string.Empty;
        var connection = await StartConnectionAsync(
            _firstApp,
            msg => received = msg,
            options => options.AccessTokenProvider = () => Task.FromResult<string?>(token),
            _output);

        await Task.Delay(100);
        await connection.InvokeAsync("SentToUser", "SmokeUser", "payload");

        await WaitUntilAsync(() => received == "payload");

        await DisposeAsync(connection);
    }

    [Fact]
    public async Task ServerStreaming_CompletesWithinTimeout()
    {
        var connection = _firstApp.CreateSignalRClient(HubName);
        await connection.StartAsync();
        await WaitUntilAsync(() => connection.State == HubConnectionState.Connected && !string.IsNullOrEmpty(connection.ConnectionId));
        await Task.Delay(100);

        var stream = connection.StreamAsync<int>("Counter", 5, 10, CancellationToken.None);
        var collected = new List<int>();

        await foreach (var value in stream)
        {
            collected.Add(value);
        }

        collected.ShouldBe(new[] { 0, 1, 2, 3, 4 });

        await connection.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < limit)
        {
            if (condition())
                return;

            await Task.Delay(PollInterval);
        }

        condition().ShouldBeTrue($"Condition not met within {limit.TotalSeconds} seconds.");
    }

    private static async Task DisposeAsync(params HubConnection[] connections)
    {
        foreach (var connection in connections)
        {
            if (connection == null)
                continue;

            try
            {
                await connection.StopAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<HubConnection> StartConnectionAsync(
        TestWebApplication app,
        Action<string> onMessage,
        Action<HttpConnectionOptions>? configureConnection = null,
        ITestOutputHelper? output = null)
    {
        var connection = app.CreateSignalRClient(HubName, configureConnection: configureConnection);
        connection.On("SendAll", onMessage);
        connection.Closed += error =>
        {
            if (output is not null)
                output.WriteLine(error is null
                    ? "Connection closed without error"
                    : $"Connection closed: {error.Message}\n{error}");
            return Task.CompletedTask;
        };
        await connection.StartAsync();
        await WaitUntilAsync(() => connection.State == HubConnectionState.Connected && !string.IsNullOrEmpty(connection.ConnectionId));
        await Task.Delay(100);
        return connection;
    }
}
