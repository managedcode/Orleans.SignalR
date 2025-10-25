using System.Collections.Concurrent;
using System.Linq;
using Shouldly;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(LoadCluster))]
[Trait("Category", "Load")]
public class HubLoadTests
{
    private const string HubName = nameof(SimpleTestHub);
    private readonly LoadClusterFixture _cluster;
    private readonly TestWebApplication _firstApp;
    private readonly TestWebApplication _secondApp;
    private readonly ITestOutputHelper _output;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1);

    public HubLoadTests(LoadClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
        _firstApp = new TestWebApplication(_cluster, 8081);
        _secondApp = new TestWebApplication(_cluster, 8082);
    }

    [Fact]
    public async Task ManyConnectionsReceiveBroadcast()
    {
        const int connectionCount = 60;
        _output.WriteLine($"Starting broadcast load test with {connectionCount} connections.");
        var connections = await CreateConnectionsAsync(connectionCount, "broadcast");

        var received = new ConcurrentDictionary<string, int>();
        foreach (var (connection, index) in connections.Select((conn, idx) => (conn, idx)))
        {
            var id = connection.ConnectionId ?? $"#{index}";
            connection.On("SendAll", (string _) =>
            {
                var total = received.AddOrUpdate(id, 1, (_, current) => current + 1);
                if (total == 1)
                {
                    _output.WriteLine($"[{id}] received first broadcast.");
                }
            });
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        _output.WriteLine("Triggering All() broadcast from the first connection.");
        await connections[0].InvokeAsync<int>("All");

        await WaitUntilAsync(
            "all connections to receive broadcast",
            () => received.Count == connectionCount,
            progress: () => $"received {received.Count}/{connectionCount}");

        foreach (var kvp in received)
        {
            kvp.Value.ShouldBeGreaterThanOrEqualTo(1);
        }

        await DisposeAsync(connections);
    }

    [Fact]
    public async Task GroupBroadcastScalesAcrossPartitions()
    {
        const int groupSize = 48;
        const string groupName = "load-group";
        _output.WriteLine($"Starting group broadcast test with {groupSize} connections.");

        var connections = await CreateConnectionsAsync(groupSize, "group");
        var messages = new ConcurrentDictionary<string, int>();

        foreach (var (connection, index) in connections.Select((conn, idx) => (conn, idx)))
        {
            var id = connection.ConnectionId ?? $"#{index}";
            connection.On("SendAll", (string _) =>
            {
                var total = messages.AddOrUpdate(id, 1, (_, current) => current + 1);
                if (total == 1)
                {
                    _output.WriteLine($"[{id}] received first group message.");
                }
            });

            await connection.InvokeAsync("AddToGroup", groupName);
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        _output.WriteLine($"Sending message to group '{groupName}'.");
        await connections[^1].InvokeAsync("GroupSendAsync", groupName, "payload");

        await WaitUntilAsync(
            $"each of the {groupSize} group members to receive the message",
            () => messages.Count == groupSize,
            progress: () => $"messages received for {messages.Count}/{groupSize} connections");

        foreach (var count in messages.Values)
        {
            count.ShouldBeGreaterThanOrEqualTo(1);
        }

        await DisposeAsync(connections);
    }

    [Fact]
    public async Task UserFanOutUnderLoad()
    {
        const int users = 12;
        const int connectionsPerUser = 3;
        var totalConnections = users * connectionsPerUser;

        _output.WriteLine($"Starting user fan-out test for {users} users ({totalConnections} connections).");

        var userIds = Enumerable.Range(0, users).Select(i => $"LoadUser{i}").ToArray();
        var tokenTasks = userIds.Select(async (userId, index) =>
        {
            var app = index % 2 == 0 ? _firstApp : _secondApp;
            var client = app.CreateHttpClient();
            var response = await client.GetAsync($"/auth?user={userId}");
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"Obtained token for {userId}.");
            return (userId, token);
        });

        var tokens = await Task.WhenAll(tokenTasks);

        var userMessages = new ConcurrentDictionary<string, int>();
        var connectionTasks = new List<Task<HubConnection>>(totalConnections);

        var connectionIndex = 0;
        foreach (var (userId, token) in tokens)
        {
            for (var c = 0; c < connectionsPerUser; c++)
            {
                var app = (connectionIndex++ % 2 == 0) ? _firstApp : _secondApp;
                connectionTasks.Add(StartUserConnectionAsync(app, userId, token, c, userMessages));
            }
        }

        var connections = await DrainBatchAsync(connectionTasks, totalConnections);

        await Task.Delay(TimeSpan.FromSeconds(1));
        _output.WriteLine("Broadcasting to all users.");
        await connections[0].InvokeAsync(
            "SentToUserIds",
            userIds,
            "fanout");

        await WaitUntilAsync(
            $"each of the {users} users to receive messages on all connections",
            () => userMessages.Count == users && userMessages.Values.All(count => count >= connectionsPerUser),
            TimeSpan.FromSeconds(45),
            () => string.Join(", ", userMessages.Select(kvp => $"{kvp.Key}:{kvp.Value}/{connectionsPerUser}")));

        foreach (var count in userMessages.Values)
        {
            count.ShouldBe(connectionsPerUser);
        }

        await DisposeAsync(connections);
    }

    private async Task<List<HubConnection>> CreateConnectionsAsync(int count, string labelPrefix)
    {
        const int batchSize = 20;

        var tasks = new List<Task<HubConnection>>(batchSize);
        var connections = new List<HubConnection>(count);

        for (var index = 0; index < count; index++)
        {
            tasks.Add(StartAnonymousConnectionAsync(index, labelPrefix));

            if (tasks.Count == batchSize || index == count - 1)
            {
                var batch = await Task.WhenAll(tasks);
                connections.AddRange(batch);
                _output.WriteLine($"Established {connections.Count}/{count} {labelPrefix} connections.");
                tasks.Clear();
            }
        }

        return connections;
    }

    private Task<HubConnection> StartAnonymousConnectionAsync(int index, string labelPrefix)
    {
        var app = index % 2 == 0 ? _firstApp : _secondApp;
        var connection = app.CreateSignalRClient(HubName);
        connection.On<string>("SendAll", _ => { });

        connection.Closed += error =>
        {
            var reason = error is null ? "closed" : $"closed with error: {error.Message}";
            _output.WriteLine($"[{labelPrefix}#{index}] {reason} (ConnectionId={connection.ConnectionId ?? "n/a"}).");
            return Task.CompletedTask;
        };

        return StartConnectionAsync(connection, $"{labelPrefix}#{index}");
    }

    private Task<HubConnection> StartUserConnectionAsync(
        TestWebApplication app,
        string userId,
        string token,
        int connectionIndex,
        ConcurrentDictionary<string, int> userMessages)
    {
        var label = $"{userId}#{connectionIndex}";
        var connection = app.CreateSignalRClient(
            HubName,
            configureConnection: options => options.AccessTokenProvider = () => Task.FromResult<string?>(token));

        connection.On("SendAll", (string _) =>
        {
            var total = userMessages.AddOrUpdate(userId, 1, (_, current) => current + 1);
            if (total == 1)
            {
                _output.WriteLine($"[{label}] received first user message.");
            }
        });

        connection.Closed += error =>
        {
            var reason = error is null ? "closed" : $"closed with error: {error.Message}";
            _output.WriteLine($"[{label}] {reason} (ConnectionId={connection.ConnectionId ?? "n/a"}).");
            return Task.CompletedTask;
        };

        return StartConnectionAsync(connection, label);
    }

    private async Task<List<HubConnection>> DrainBatchAsync(
        IEnumerable<Task<HubConnection>> connectionTasks,
        int expectedCount)
    {
        const int batchSize = 20;

        var connections = new List<HubConnection>(expectedCount);
        var buffer = new List<Task<HubConnection>>(batchSize);

        foreach (var task in connectionTasks)
        {
            buffer.Add(task);
            if (buffer.Count == batchSize)
            {
                var batch = await Task.WhenAll(buffer);
                connections.AddRange(batch);
                _output.WriteLine($"Established {connections.Count}/{expectedCount} user connections.");
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            var batch = await Task.WhenAll(buffer);
            connections.AddRange(batch);
            _output.WriteLine($"Established {connections.Count}/{expectedCount} user connections.");
        }

        return connections;
    }

    private async Task<HubConnection> StartConnectionAsync(HubConnection connection, string label)
    {
        await connection.StartAsync();
        connection.State.ShouldBe(HubConnectionState.Connected);
        _output.WriteLine($"[{label}] connected (ConnectionId={connection.ConnectionId}).");
        return connection;
    }

    private async Task DisposeAsync(IReadOnlyCollection<HubConnection> connections)
    {
        _output.WriteLine($"Disposing {connections.Count} connections...");
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
                _output.WriteLine($"Failed to stop {connection.ConnectionId}: {ex.Message}");
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task WaitUntilAsync(
        string description,
        Func<bool> condition,
        TimeSpan? timeout = null,
        Func<string>? progress = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var start = DateTime.UtcNow;
        var lastLog = TimeSpan.Zero;

        while (DateTime.UtcNow - start < limit)
        {
            if (condition())
            {
                _output.WriteLine($"Condition '{description}' satisfied after {(DateTime.UtcNow - start):c}.");
                return;
            }

            var elapsed = DateTime.UtcNow - start;
            if (elapsed - lastLog >= LogInterval)
            {
                var status = progress?.Invoke();
                _output.WriteLine(status is null
                    ? $"Waiting for {description}... elapsed {elapsed:c}."
                    : $"Waiting for {description}... elapsed {elapsed:c}. Status: {status}");
                lastLog = elapsed;
            }

            await Task.Delay(PollInterval);
        }

        var finalStatus = progress?.Invoke();
        if (finalStatus is not null)
        {
            _output.WriteLine($"Final status for '{description}': {finalStatus}");
        }

        condition().ShouldBeTrue($"Condition '{description}' not met within {limit.TotalSeconds} seconds.");
    }
}
