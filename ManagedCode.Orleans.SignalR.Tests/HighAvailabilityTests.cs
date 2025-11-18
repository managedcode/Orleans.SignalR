using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Orleans.TestingHost;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(HighAvailabilityCluster))]
public sealed class HighAvailabilityTests : IAsyncLifetime
{
    private readonly HighAvailabilityClusterFixture _cluster;
    private readonly ITestOutputHelper _output;
    private TestWebApplication? _app;

    private const int DisconnectScenarioConnections = 32;
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatGracePeriod = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(1);

    public HighAvailabilityTests(HighAvailabilityClusterFixture cluster, ITestOutputHelper output)
    {
        _cluster = cluster;
        _output = output;
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ORLEANS_SIGNALR_LOGLEVEL", "Warning");
        _app = new TestWebApplication(_cluster, port: 8300);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ClientsSurviveThirdAndFourthSiloShutdown()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var connections = await CreateConnectionsAsync(_app, 50);
        var cluster = _cluster.Cluster;


        try
        {
            await WarmUpConnectionsAsync(connections);
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline", _output);

            await cluster.StartAdditionalSiloAsync();
            connections.AddRange(await CreateConnectionsAsync(_app, 50 ));
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline", _output);
            await WarmUpConnectionsAsync(connections);



            await cluster.StartAdditionalSiloAsync();
            connections.AddRange(await CreateConnectionsAsync(_app, 100 ));
            await WarmUpConnectionsAsync(connections);
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline", _output);



            var extraSilos = cluster.Silos.Skip(2).ToArray();

            foreach (var silo in extraSilos)
            {
                _output.WriteLine($"[HA] Killing silo {silo.SiloAddress}.");
                await cluster.KillSiloAsync(silo);
                await cluster.WaitForLivenessToStabilizeAsync(true);
                await Task.Delay(HeartbeatGracePeriod);
                await WarmUpConnectionsAsync(connections);
                await BroadcastAndAwaitAsync(connections, connections[1], $"after-kill-{silo.InstanceNumber}", _output);
            }
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
        }
    }

    [Fact]
    public async Task ServerBroadcastIgnoresDisconnectedClients()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("Test host is not initialised.");
        }

        var connections = await CreateConnectionsAsync(_app, DisconnectScenarioConnections);
        var survivorCount = connections.Count / 2;

        try
        {
            await WarmUpConnectionsAsync(connections);
            await BroadcastAndAwaitAsync(connections, connections[0], "initial", _output);

            foreach (var connection in connections.Take(connections.Count - survivorCount))
            {
                await connection.Connection.StopAsync();
                await connection.Connection.DisposeAsync();
                connection.MarkDisconnected();
            }

            var survivors = connections.Where(conn => conn.IsConnected).ToArray();
            survivors.Length.ShouldBe(survivorCount, "Expected remaining connected clients.");

            await BroadcastAndAwaitAsync(survivors, survivors[0], "after-disconnect", _output);
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
        }
    }

    private static async Task<List<BroadcastConnection>> CreateConnectionsAsync(TestWebApplication app, int count)
    {
        var connections = new List<BroadcastConnection>(count);
        for (var index = 0; index < count; index++)
        {
            var connection = app.CreateSignalRClient(nameof(SimpleTestHub));
            var tracked = new BroadcastConnection(connection);
            await connection.StartAsync();
            connections.Add(tracked);
        }

        return connections;
    }

    private static async Task BroadcastAndAwaitAsync(
        IEnumerable<BroadcastConnection> connections,
        BroadcastConnection sender,
        string tag,
        ITestOutputHelper output,
        int attempt = 1)
    {
        var connectionList = connections as IList<BroadcastConnection> ?? connections.ToList();
        if (connectionList.Count == 0)
        {
            return;
        }

        var payload = $"{tag}:{Guid.NewGuid():N}";
        await EnsureAllConnectedAsync(connectionList);
        foreach (var connection in connectionList)
        {
            await connection.EnsureConnectedAsync();
            connection.ResetReceipt();
        }

        await sender.Connection.InvokeAsync("BroadcastPayload", payload);
        var deliveries = await Task.WhenAll(connectionList.Select(conn => conn.WaitForReceiptAsync(BroadcastTimeout, payload)));
        var stalled = connectionList.Where((conn, index) => !deliveries[index]).ToArray();
        if (stalled.Length == 0)
        {
            return;
        }

        var stalledList = string.Join(", ",
            stalled.Select(conn => conn.Connection.ConnectionId ?? "<unknown>"));
        output.WriteLine($"[HA] Broadcast '{tag}' stalled on {stalled.Length} connection(s): {stalledList}. Attempt {attempt}.");

        foreach (var stalledConnection in stalled)
        {
            await stalledConnection.RestartAsync();
        }

        if (attempt >= 3)
        {
            throw new TimeoutException($"Connections [{stalledList}] did not observe broadcast '{tag}' after {attempt} attempts.");
        }

        await Task.Delay(TestDefaults.ClientTimeout);
        await BroadcastAndAwaitAsync(connectionList, sender, tag, output, attempt + 1);
    }

    private static async Task EnsureAllConnectedAsync(IEnumerable<BroadcastConnection> connections)
    {
        foreach (var connection in connections)
        {
            await connection.EnsureConnectedAsync();
        }
    }

    private static async Task RestartAllConnectionsAsync(IEnumerable<BroadcastConnection> connections)
    {
        foreach (var connection in connections)
        {
            await connection.RestartAsync();
        }
    }

    private static async Task WarmUpConnectionsAsync(IEnumerable<BroadcastConnection> connections)
    {
        var tasks = connections.Select(async connection =>
        {
            await connection.EnsureConnectedAsync();
            await connection.Connection.InvokeAsync<int>("Plus", 0, 0);
        });

        await Task.WhenAll(tasks);
    }

    private static async Task DisposeConnectionsAsync(IEnumerable<BroadcastConnection> connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                await connection.Connection.StopAsync();
            }
            catch
            {
            }
            finally
            {
                await connection.Connection.DisposeAsync();
            }
        }
    }

    private sealed class BroadcastConnection
    {
        private TaskCompletionSource<string> _receipt = CreateReceipt();

        public BroadcastConnection(HubConnection connection)
        {
            Connection = connection;
            IsConnected = true;
            connection.On<string>("PerfBroadcast", message =>
            {
                _receipt.TrySetResult(message);
            });
            connection.Reconnecting += _ =>
            {
                IsConnected = false;
                return Task.CompletedTask;
            };
            connection.Reconnected += _ =>
            {
                IsConnected = true;
                return Task.CompletedTask;
            };
            connection.Closed += _ =>
            {
                IsConnected = false;
                return Task.CompletedTask;
            };
        }

        public HubConnection Connection { get; }
        public bool IsConnected { get; private set; }

        public void ResetReceipt() => _receipt = CreateReceipt();

        public async Task<bool> WaitForReceiptAsync(TimeSpan timeout, string payload)
        {
            if (!IsConnected)
            {
                return true;
            }

            try
            {
                await _receipt.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void MarkDisconnected()
        {
            IsConnected = false;
            _receipt.TrySetCanceled();
        }

        public async Task EnsureConnectedAsync()
        {
            if (Connection.State == HubConnectionState.Connected)
            {
                IsConnected = true;
                return;
            }

            try
            {
                await Connection.StopAsync();
            }
            catch
            {
            }

            await Connection.StartAsync();
            IsConnected = true;
        }

        public async Task RestartAsync()
        {
            try
            {
                await Connection.StopAsync();
            }
            catch
            {
            }

            await Connection.StartAsync();
            IsConnected = true;
        }

        private static TaskCompletionSource<string> CreateReceipt() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
