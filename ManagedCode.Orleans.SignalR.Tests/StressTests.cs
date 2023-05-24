using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
        _firstApp= new TestWebApplication(_siloCluster, 8081);
        _secondApp = new TestWebApplication(_siloCluster, 8082);
    }

    private Task<HubConnection> CreateHubConnection(TestWebApplication app)
    {
        var hubConnection = app.CreateSignalRClient(nameof(StressTestHub));
        return Task.FromResult(hubConnection);
    } 
    
    private async Task<HubConnection> CreateHubConnection(string user, TestWebApplication app)
    {
        var client = app.CreateHttpClient();
        var responseMessage = await client.GetAsync("/auth?user="+user);
        var token = await responseMessage.Content.ReadAsStringAsync();
        var hubConnection = app.CreateSignalRClient(nameof(StressTestHub),
            configureConnection: options => { options.AccessTokenProvider = () => Task.FromResult(token); });
        return hubConnection;
    }
    

    [Fact]
    public async Task InvokeAsyncSignalRTest()
    {
        ConcurrentQueue<HubConnection> connections = new();
        ConcurrentDictionary<string,int> users = new();
        ConcurrentDictionary<string,int> groups = new();

        var sw = Stopwatch.StartNew();

        int allCount= 0;
        
        async Task CreateConnections(int number)
        {
            for (int i = 0; i < number; i++)
            {
                var server = Random.Shared.Next(0, 1) == 0 ? _firstApp : _secondApp;
                var user = Random.Shared.Next(0, 3) == 3 ? null : $"user{i}@email.com";
                var group = Random.Shared.Next(0, 4) > 2 ? null : $"group{i}";

                HubConnection connection = null;
                if (!string.IsNullOrEmpty(user))
                {
                    users[user] = 0;
                    connection = await CreateHubConnection(user, server);
                }
                else
                {
                    connection = await CreateHubConnection(server);
                }

                connection.On("All", (string m) =>
                {
                    Interlocked.Increment(ref allCount);
                });
                
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


       
        await Task.WhenAll(Enumerable.Repeat(100, 1000).Select(CreateConnections));
        //await Task.WhenAll(Enumerable.Repeat(5, 5).Select(CreateConnections));



        sw.Stop();
        _outputHelper.WriteLine($"Init time {sw.Elapsed}; connections {connections.Count}; users {users.Count}; groups {groups.Count}");
        sw.Reset();
        
        // OK, this is bad =(
        
        //--------------------------------
        //---SignalR
        //Init time 00:01:16.6498487; connections 100000; users 10000; groups 10000
        //All count 100_000
        //All connections: 100000; recived: 100_000 messages; time: 00:00:06.6428702
        
        //---Orleans
        //Init time 00:01:38.7127252; connections 100000; users 10000; groups 10000
        //All count 100_000
        //All connections: 100000; recived: 100000 messages; time: 00:00:56.9603385
        
        
        //obsevers
        //---SignalR
        // Init time 00:00:07.3090994; connections 10_000; users 100; groups 100
        // All count 10000
        // All connections: 10_000; recived: 10_000 messages; time: 00:00:00.0300137
        //---Orleans

        
        sw.Start();


        await connections.First().InvokeAsync<int>("All");



        while (allCount < connections.Count)
        {
            await Task.Delay(10000);
            _outputHelper.WriteLine($"All count {allCount}");
        }
        _outputHelper.WriteLine($"All count {allCount}");
        
        sw.Stop();
        _outputHelper.WriteLine($"All connections: {connections.Count}; recived: {allCount} messages; time: {sw.Elapsed}");
    }
    
   
}