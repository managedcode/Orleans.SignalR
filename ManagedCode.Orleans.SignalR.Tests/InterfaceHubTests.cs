using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public class InterfaceHubTests
{
    private readonly TestWebApplication _firstApp;
    private readonly ITestOutputHelper _outputHelper;
    private readonly TestWebApplication _secondApp;
    private readonly SmokeClusterFixture _siloCluster;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private readonly TestOutputHelperAccessor _loggerAccessor = new();

    public InterfaceHubTests(SmokeClusterFixture testApp, ITestOutputHelper outputHelper)
    {
        _siloCluster = testApp;
        _outputHelper = outputHelper;
        _loggerAccessor.Output = outputHelper;
        _firstApp = new TestWebApplication(_siloCluster, 8081, loggerAccessor: _loggerAccessor);
        _secondApp = new TestWebApplication(_siloCluster, 8082, loggerAccessor: _loggerAccessor);
    }

    private async Task<HubConnection> CreateHubConnection(TestWebApplication app, string hubName = nameof(InterfaceTestHub))
    {
        var hubConnection = app.CreateSignalRClient(hubName);
        hubConnection.Closed += error =>
        {
            if (error is not null)
            {
                _outputHelper.WriteLine($"[{hubName}] connection closed with error: {error.Message}");
            }
            else
            {
                _outputHelper.WriteLine($"[{hubName}] connection closed gracefully.");
            }

            return Task.CompletedTask;
        };

        await hubConnection.StartAsync();
        await WaitUntilAsync(
            () => hubConnection.State == HubConnectionState.Connected && !string.IsNullOrEmpty(hubConnection.ConnectionId),
            description: $"connection to {hubName} to reach Connected state");

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        _outputHelper.WriteLine($"[{hubName}] connection established with id {hubConnection.ConnectionId}.");

        return hubConnection;
    }

    [Fact]
    public async Task BasicMessageFlowAcrossApps()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection1 = await CreateHubConnection(_firstApp, nameof(SimpleTestHub));
        var connection2 = await CreateHubConnection(_secondApp, nameof(SimpleTestHub));

        connection2.On<string>("SendAll", payload => received.TrySetResult(payload));

        await connection1.InvokeAsync<int>("All");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(received.Task, "Timed out waiting for broadcast to reach the second connection.");

        var message = await received.Task;
        message.ShouldBe("test");

        await DisposeAsync(new[] { connection1, connection2 });
    }

    [Fact]
    public async Task InvokeAsyncSignalRTest()
    {
        var connection1 = await CreateHubConnection(_firstApp);
        var connection2 = await CreateHubConnection(_secondApp);

        connection1.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection1 - GetMessage");
            return "connection1";
        });
        connection2.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection2 - GetMessage");
            return "connection2";
        });

        //invoke in SignalR
        var msg1 = await connection2.InvokeAsync<string>("WaitForMessage", connection1.ConnectionId);
        _outputHelper.WriteLine("mgs1");
        msg1.ShouldBe("connection1");

        var msg2 = await connection2.InvokeAsync<string>("WaitForMessage", connection2.ConnectionId);
        _outputHelper.WriteLine("mgs2");
        msg2.ShouldBe("connection2");

        await Assert.ThrowsAsync<HubException>(async () =>
            await connection2.InvokeAsync<string>("WaitForMessage", "non-existing"));

        _outputHelper.WriteLine("stopping...");
        await connection1.StopAsync();
        await connection2.StopAsync();
    }

    private static async Task DisposeAsync(IEnumerable<HubConnection> connections)
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
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task InvokeAsyncGrainTest()
    {
        var connection1 = await CreateHubConnection(_firstApp);
        var connection2 = await CreateHubConnection(_secondApp);
        var connection3 = await CreateHubConnection(_firstApp);
        var connection4 = await CreateHubConnection(_secondApp);

        connection1.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection1 - GetMessage");
            return "connection1";
        });

        connection2.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection2 - GetMessage");
            return "connection2";
        });

        connection3.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection3 - GetMessage");
            throw new Exception("oops3");
            return string.Empty;
        });

        connection4.On("GetMessage", () =>
        {
            _outputHelper.WriteLine("Connection4 - GetMessage");
            throw new Exception("oops4");
            return string.Empty;
        });

        //invoke in Grain
        var grain = _siloCluster.Cluster.Client.GetGrain<ITestGrain>("test");

        var msg1 = await grain.GetMessageInvoke(connection1.ConnectionId);
        _outputHelper.WriteLine("msg1");

        var msg2 = await grain.GetMessage(connection2.ConnectionId);
        _outputHelper.WriteLine("msg2");

        await Assert.ThrowsAsync<IOException>(async () => await grain.GetMessage("non-existing"));
        _outputHelper.WriteLine("throw");

        msg1.ShouldBe("connection1");
        msg2.ShouldBe("connection2");

        await Assert.ThrowsAsync<Exception>(async () => await grain.GetMessage(connection3.ConnectionId));
        _outputHelper.WriteLine("msg3-thorw");

        await Assert.ThrowsAsync<Exception>(async () => await grain.GetMessage(connection4.ConnectionId));
        _outputHelper.WriteLine("msg4-thorw");

        _outputHelper.WriteLine("stopping...");
        await connection1.StopAsync();
        await connection2.StopAsync();
    }

    [Fact]
    public async Task InvokeAsyncWithPingConnectionGrainTest()
    {
        var connection1 = await CreateHubConnection(_firstApp);
        var connection2 = await CreateHubConnection(_secondApp);

        connection1.On("GetMessage", () => "connection1");
        connection2.On("GetMessage", () => "connection2");

        connection1.State.ShouldBe(HubConnectionState.Connected);
        connection2.State.ShouldBe(HubConnectionState.Connected);

        var resilienceDelay = TestDefaults.KeepAliveInterval + TestDefaults.ClientTimeout;
        await Task.Delay(resilienceDelay);

        for (var i = 0; i < 3; i++)
        {
            //invoke in Grain
            var grain = _siloCluster.Cluster.Client.GetGrain<ITestGrain>("test" + i);

            var msg1 = await grain.GetMessageInvoke(connection1.ConnectionId);
            var msg2 = await grain.GetMessage(connection2.ConnectionId);

            await Assert.ThrowsAsync<IOException>(async () => await grain.GetMessage("non-existing"));

            msg1.ShouldBe("connection1");
            msg2.ShouldBe("connection2");
        }

        await Task.Delay(resilienceDelay);

        for (var i = 0; i < 4; i++)
        {
            //invoke in Grain
            var grain = _siloCluster.Cluster.Client.GetGrain<ITestGrain>("test" + i);

            var msg1 = await grain.GetMessageInvoke(connection1.ConnectionId);
            var msg2 = await grain.GetMessage(connection2.ConnectionId);

            await Assert.ThrowsAsync<IOException>(async () => await grain.GetMessage("non-existing"));

            msg1.ShouldBe("connection1");
            msg2.ShouldBe("connection2");
        }

        await connection1.StopAsync();
        await connection2.StopAsync();
    }

    [Fact]
    public async Task SignalRFromGrainTest()
    {
        List<string> messages1 = new();
        List<string> messages2 = new();

        var connection1 = await CreateHubConnection(_firstApp);
        var connection2 = await CreateHubConnection(_secondApp);

        connection1.On<int>("SendRandom", random => messages1.Add(random.ToString()));
        connection1.On<string>("SendMessage", messages1.Add);

        connection2.On<int>("SendRandom", random => messages2.Add(random.ToString()));
        connection2.On<string>("SendMessage", messages2.Add);

        var grain = _siloCluster.Cluster.Client.GetGrain<ITestGrain>("test");

        //push random
        await grain.PushRandom();

        await WaitUntilAsync(() => messages1.Count == 1 && messages2.Count == 1);

        messages1.Count.ShouldBe(1);
        messages2.Count.ShouldBe(1);

        messages1.Clear();
        messages2.Clear();

        //push message
        await grain.PushMessage("test");

        await WaitUntilAsync(() => messages1.Count == 1 && messages2.Count == 1);

        messages1.Count.ShouldBe(1);
        messages2.Count.ShouldBe(1);

        messages1.Clear();
        messages2.Clear();

        await connection1.StopAsync();
        await connection2.StopAsync();
    }

    private async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? description = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var delay = pollInterval ?? PollInterval;
        var start = DateTime.UtcNow;
        var lastLog = TimeSpan.Zero;

        while (DateTime.UtcNow - start < limit)
        {
            if (condition())
            {
                if (description is not null)
                {
                    _outputHelper.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                }

                return;
            }

            var elapsed = DateTime.UtcNow - start;
            if (description is not null && elapsed - lastLog >= TimeSpan.FromSeconds(1))
            {
                _outputHelper.WriteLine($"Waiting for {description}... elapsed {elapsed:c}.");
                lastLog = elapsed;
            }

            await Task.Delay(delay);
        }

        if (description is not null)
        {
            _outputHelper.WriteLine($"Timed out waiting for {description} after {limit.TotalSeconds} seconds.");
        }

        var message = description is null
            ? $"Condition was not met within {limit.TotalSeconds} seconds."
            : $"Condition '{description}' was not met within {limit.TotalSeconds} seconds.";

        condition().ShouldBeTrue(message);
    }
}
