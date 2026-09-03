using ManagedCode.Orleans.SignalR.Core.SignalR;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(HighAvailabilityCluster))]
public sealed class HighAvailabilityTests(HighAvailabilityClusterFixture cluster, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly HighAvailabilityClusterFixture _cluster = cluster;
    private readonly ITestOutputHelper _output = output;
    private readonly TestOutputHelperAccessor _loggerAccessor = new();
    private TestWebApplication? _app;

    private const int DisconnectScenarioConnections = 32;
    private static readonly TimeSpan _broadcastTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _heartbeatGracePeriod = TestDefaults.ClientTimeout + TimeSpan.FromSeconds(1);

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ORLEANS_SIGNALR_LOGLEVEL", "Warning");
        _loggerAccessor.Output = _output;
        _app = new TestWebApplication(_cluster, port: 8300, loggerAccessor: _loggerAccessor);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ClientsSurviveThirdAndFourthSiloShutdownAsync()
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
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline");

            await cluster.StartAdditionalSiloAsync();
            connections.AddRange(await CreateConnectionsAsync(_app, 50));
            await WarmUpConnectionsAsync(connections);
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline");

            await cluster.StartAdditionalSiloAsync();
            connections.AddRange(await CreateConnectionsAsync(_app, 100));
            await WarmUpConnectionsAsync(connections);
            await BroadcastAndAwaitAsync(connections, connections[0], "baseline");

            var extraSilos = cluster.Silos.Skip(2).ToArray();

            foreach (var silo in extraSilos)
            {
                _output.WriteLine($"[HA] Killing silo {silo.SiloAddress}.");
                await cluster.KillSiloAsync(silo);
                await cluster.WaitForLivenessToStabilizeAsync(true);
                await Task.Delay(_heartbeatGracePeriod);
                await WarmUpConnectionsAsync(connections);
                await BroadcastAndAwaitAsync(connections, connections[1], $"after-kill-{silo.InstanceNumber}");
            }
        }
        finally
        {
            await DisposeConnectionsAsync(connections);
        }
    }

    [Fact]
    public async Task ServerBroadcastIgnoresDisconnectedClientsAsync()
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
            await BroadcastAndAwaitAsync(connections, connections[0], "initial");

            foreach (var connection in connections.Take(connections.Count - survivorCount))
            {
                await connection.Connection.StopAsync();
                await connection.Connection.DisposeAsync();
                connection.MarkDisconnected();
            }

            var survivors = connections.Where(conn => conn.IsConnected).ToArray();
            survivors.Length.ShouldBe(survivorCount, "Expected remaining connected clients.");

            await BroadcastAndAwaitAsync(survivors, survivors[0], "after-disconnect");
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

    private async Task BroadcastAndAwaitAsync(
        IEnumerable<BroadcastConnection> connections,
        BroadcastConnection sender,
        string tag)
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
        var deliveries = await Task.WhenAll(connectionList.Select(conn => conn.WaitForReceiptAsync(_broadcastTimeout, payload)));
        var stalled = connectionList.Where((conn, index) => !deliveries[index]).ToArray();
        if (stalled.Length == 0)
        {
            return;
        }

        var stalledList = string.Join(", ",
            stalled.Select(conn => conn.Connection.ConnectionId ?? "<unknown>"));
        _output.WriteLine($"[HA] Broadcast '{tag}' stalled on {stalled.Length} connection(s): {stalledList}.");
        await ProbeStalledConnectionsAsync(stalled);
        throw new TimeoutException($"Connections [{stalledList}] did not observe broadcast '{tag}'.");
    }

    private async Task ProbeStalledConnectionsAsync(IReadOnlyCollection<BroadcastConnection> stalled)
    {
        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<SimpleTestHub>(_cluster.Cluster.Client);
        var probes = stalled.Select(async connection =>
        {
            var connectionId = connection.Connection.ConnectionId ?? "<unknown>";
            var payload = $"probe:{Guid.NewGuid():N}";
            connection.ResetReceipt();
            var routed = await coordinator.SendToConnection(
                new InvocationMessage("PerfBroadcast", [payload]),
                connectionId);
            var delivered = routed && await connection.WaitForReceiptAsync(TimeSpan.FromSeconds(5), payload);
            _output.WriteLine($"[HA] Targeted probe for {connectionId}: routed={routed}, delivered={delivered}.");
        });

        await Task.WhenAll(probes);
    }

    private static async Task EnsureAllConnectedAsync(IEnumerable<BroadcastConnection> connections)
    {
        foreach (var connection in connections)
        {
            await connection.EnsureConnectedAsync();
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
                Volatile.Read(ref _receipt).TrySetResult(message);
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

        public void ResetReceipt() => Interlocked.Exchange(ref _receipt, CreateReceipt());

        public async Task<bool> WaitForReceiptAsync(TimeSpan timeout, string expectedPayload)
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                var receipt = Volatile.Read(ref _receipt);
                var receivedPayload = await receipt.Task.WaitAsync(timeout);
                return string.Equals(receivedPayload, expectedPayload, StringComparison.Ordinal);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void MarkDisconnected()
        {
            IsConnected = false;
            Volatile.Read(ref _receipt).TrySetCanceled();
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

        private static TaskCompletionSource<string> CreateReceipt() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
