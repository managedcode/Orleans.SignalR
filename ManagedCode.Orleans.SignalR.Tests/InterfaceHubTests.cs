using FluentAssertions;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SiloCluster))]
public class InterfaceHubTests
{
    private readonly TestWebApplication _firstApp;
    private readonly ITestOutputHelper _outputHelper;
    private readonly TestWebApplication _secondApp;
    private readonly SiloCluster _siloCluster;

    public InterfaceHubTests(SiloCluster testApp, ITestOutputHelper outputHelper)
    {
        _siloCluster = testApp;
        _outputHelper = outputHelper;
        _firstApp = new TestWebApplication(_siloCluster, 8081);
        _secondApp = new TestWebApplication(_siloCluster, 8082);
    }

    private async Task<HubConnection> CreateHubConnection(TestWebApplication app)
    {
        var hubConnection = _firstApp.CreateSignalRClient(nameof(InterfaceTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
        return hubConnection;
    }
    
    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        List<string> messages1 = new();
        List<string> messages2 = new();
        
        var connection1 = await CreateHubConnection(_firstApp);
        var connection2 = await CreateHubConnection(_secondApp);
        
        connection1.On<int>("SendRandom", random => messages1.Add(random.ToString()));
        connection1.On<string>("SendMessage", messages => messages1.Add(messages));
        connection1.On("GetMessage",  () =>
        {
            return "connection1";
        });
        
        connection2.On<int>("SendRandom", random => messages2.Add(random.ToString()));
        connection2.On<string>("SendMessage", messages => messages2.Add(messages));
        connection2.On("GetMessage",  () => "connection2");

        var msg = await connection2.InvokeAsync<string>("WaitForMessage",connection1.ConnectionId);
        msg.Should().Be("connection1");
        
        
        var grain = _siloCluster.Cluster.Client.GetGrain<ITestGrain>("test");
        
        var msg1 = await grain.GetMessage(connection1.ConnectionId);
        var msg2 = await grain.GetMessage(connection2.ConnectionId);
        //var msg3 = await grain.GetMessage("non-existing");

        msg1.Should().Be(connection1.ConnectionId);
        msg2.Should().Be(connection2.ConnectionId);
        
        messages1.Should().HaveCount(0);
        messages2.Should().HaveCount(0);
        
        //push random
        await grain.PushRandom();
        
        await Task.Delay(TimeSpan.FromSeconds(5));
        
        messages1.Should().HaveCount(1);
        messages2.Should().HaveCount(1);
        
        messages1.Clear();
        messages2.Clear();
        
        //push message
        await grain.PushMessage("test");
        
        await Task.Delay(TimeSpan.FromSeconds(5));
        
        messages1.Should().HaveCount(1);
        messages2.Should().HaveCount(1);
        
        messages1.Clear();
        messages2.Clear();

        await connection1.StopAsync();
        await connection2.StopAsync();

    }

}