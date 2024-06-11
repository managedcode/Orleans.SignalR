using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using ManagedCode.Orleans.SignalR.Server;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SiloCluster))]
public class StressTests
{
    private readonly TestWebApplication _firstApp;
    private readonly ITestOutputHelper _outputHelper;
    private readonly TestWebApplication _secondApp;
    private readonly SiloCluster _siloCluster;

    public StressTests(SiloCluster testApp, ITestOutputHelper outputHelper)
    {
        _siloCluster = testApp;
        _outputHelper = outputHelper;
        _firstApp = new TestWebApplication(_siloCluster, 8081);
        _secondApp = new TestWebApplication(_siloCluster, 8082);
    }

    private Task<HubConnection> CreateHubConnection(TestWebApplication app)
    {
        var hubConnection = app.CreateSignalRClient(nameof(StressTestHub));
        return Task.FromResult(hubConnection);
    }

    private async Task<HubConnection> CreateHubConnection(string user, TestWebApplication app, string hub)
    {
        var client = app.CreateHttpClient();
        var responseMessage = await client.GetAsync("/auth?user=" + user);
        var token = await responseMessage.Content.ReadAsStringAsync();
        var hubConnection = app.CreateSignalRClient(hub,
            configureConnection: options => { options.AccessTokenProvider = () => Task.FromResult(token); });
        return hubConnection;
    }


    [Fact]
    public async Task InvokeAsyncSignalRTest()
    {
        ConcurrentQueue<HubConnection> connections = new();
        ConcurrentDictionary<string, int> users = new();
        ConcurrentDictionary<string, int> groups = new();


        var allCount = 0;

        async Task CreateConnections(int number)
        {
            for (var i = 0; i < number; i++)
            {
                var server = Random.Shared.Next(0, 1) == 0 ? _firstApp : _secondApp;
                var user = Random.Shared.Next(0, 3) == 3 ? null : $"user{i}@email.com";
                var group = Random.Shared.Next(0, 4) > 2 ? null : $"group{i}";

                HubConnection connection = null;
                if (!string.IsNullOrEmpty(user))
                {
                    users[user] = 0;
                    connection = await CreateHubConnection(user, server,nameof(StressTestHub));
                }
                else
                {
                    connection = await CreateHubConnection(server);
                }

                connection.On("All", (string m) => { Interlocked.Increment(ref allCount); });

                await connection.StartAsync();
                connection.State.Should().Be(HubConnectionState.Connected);

                if (!string.IsNullOrEmpty(group))
                {
                    groups[group] = 0;
                    await connection.InvokeAsync("AddToGroup", "test");
                }

                connections.Enqueue(connection);
            }
        }

        _outputHelper.WriteLine("Connecting...");
        var sw = Stopwatch.StartNew();
        //await Task.WhenAll(Enumerable.Repeat(1000, 100).Select(CreateConnections));
        await Task.WhenAll(Enumerable.Repeat(100, 100).Select(CreateConnections));
        //await Task.WhenAll(Enumerable.Repeat(5, 5).Select(CreateConnections));


        sw.Stop();
        _outputHelper.WriteLine(
            $"Init time {sw.Elapsed}; connections {connections.Count}; users {users.Count}; groups {groups.Count}");
        await Task.Delay(TimeSpan.FromSeconds(1));
        sw.Reset();

        sw.Start();
        await connections.First().InvokeAsync<int>("All");

        while (allCount < connections.Count)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            _outputHelper.WriteLine($"All count {allCount}");
        }

        _outputHelper.WriteLine($"All count {allCount}");

        sw.Stop();
        _outputHelper.WriteLine(
            $"All connections: {connections.Count}; recived: {allCount} messages; time: {sw.Elapsed}");

        //--------------------------------
        //---SignalR
        //Init time 00:01:34.7682547; connections 100_000; users 1000; groups 1000
        //All count 100_000
        //All connections: 100_000; recived: 100_000 messages; time: 00:00:17.5744497


        //--Obsevers
        //Init time 00:02:12.5033550; connections 100_000; users 1000; groups 1000
        //All count 100_000
        //All connections: 100_000; recived: 100_000 messages; time: 00:00:26.2132306
        //--------------------------------
    }
    
    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.FromMilliseconds(0));
        
        foreach (var silo in _siloCluster.Cluster.GetActiveSilos())
        {
            await _siloCluster.Cluster.RestartSiloAsync(silo);
        }
        
        foreach (var silo in _siloCluster.Cluster.GetActiveSilos())
        {
            await _siloCluster.Cluster.RestartSiloAsync(silo);
        }

        var signalRConnectionHolderGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
        var signalRGroupGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupGrain)}"));
        var signalRInvocationGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));
        var signalRUserGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRUserGrain)}"));

        signalRConnectionHolderGrainCount.Count.Should().BeGreaterOrEqualTo(0);
        signalRGroupGrainCount.Count.Should().BeGreaterOrEqualTo(0);
        signalRInvocationGrainCount.Count.Should().BeGreaterOrEqualTo(0);
        signalRUserGrainCount.Count.Should().BeGreaterOrEqualTo(0);
        

        var hubConnection = await CreateHubConnection("user", _firstApp, nameof(SimpleTestHub));
        hubConnection.On("GetMessage", () => "connection1");
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
        
        await hubConnection.InvokeAsync<int>("DoTest");
        await hubConnection.InvokeAsync("AddToGroup", "test");
        await hubConnection.InvokeAsync("WaitForMessage", hubConnection.ConnectionId);
        

        signalRConnectionHolderGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
        signalRGroupGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupGrain)}"));
        signalRInvocationGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));
        signalRUserGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRUserGrain)}"));

        signalRConnectionHolderGrainCount.Count.Should().BeGreaterOrEqualTo(1);
        signalRGroupGrainCount.Count.Should().BeGreaterOrEqualTo(1);
        signalRInvocationGrainCount.Count.Should().BeGreaterOrEqualTo(1);
        signalRUserGrainCount.Count.Should().BeGreaterOrEqualTo(1);
        _outputHelper.WriteLine($"ConnectionHolder:{signalRConnectionHolderGrainCount.Count};GroupGrain:{signalRGroupGrainCount.Count}; InvocationGrain:{signalRInvocationGrainCount.Count}; UserGrain:{signalRUserGrainCount.Count};");

        
        _outputHelper.WriteLine($"Invoke one more time. Connection is {hubConnection.State}");
        var rnd = await hubConnection.InvokeAsync<int>("DoTest");
        await hubConnection.InvokeAsync("AddToGroup", "test");
        await hubConnection.InvokeAsync("WaitForMessage", hubConnection.ConnectionId);

        rnd.Should().BeGreaterThan(0);
        
        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected); 
        await hubConnection.DisposeAsync();
        _outputHelper.WriteLine("Connection is stopped.");
        _outputHelper.WriteLine($"wait a minute");
        await Task.Delay(TimeSpan.FromMinutes(1));
        
        signalRConnectionHolderGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
        signalRGroupGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupGrain)}"));
        signalRInvocationGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));
        signalRUserGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRUserGrain)}"));

        _outputHelper.WriteLine($"ConnectionHolder:{signalRConnectionHolderGrainCount.Count};GroupGrain:{signalRGroupGrainCount.Count}; InvocationGrain:{signalRInvocationGrainCount.Count}; UserGrain:{signalRUserGrainCount.Count};");

    
        _outputHelper.WriteLine($"check...");

        await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.FromSeconds(10));

        signalRConnectionHolderGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRConnectionHolderGrain)}"));
        signalRGroupGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRGroupGrain)}"));
        signalRInvocationGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRInvocationGrain)}"));
        signalRUserGrainCount = await _siloCluster.Cluster.Client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create($"ManagedCode.{nameof(SignalRUserGrain)}"));


        signalRConnectionHolderGrainCount.Count.Should().Be(0);
        signalRGroupGrainCount.Count.Should().Be(0);
        signalRInvocationGrainCount.Count.Should().Be(0);
        signalRUserGrainCount.Count.Should().Be(0);

    }
}