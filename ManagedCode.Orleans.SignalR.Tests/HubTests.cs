using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using FluentAssertions;
using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SiloCluster))]
public class HubTests
{
    private readonly TestWebApplication _firstApp;
    private readonly ITestOutputHelper _outputHelper;
    private readonly TestWebApplication _secondApp;
    private readonly SiloCluster _siloCluster;

    public HubTests(SiloCluster testApp, ITestOutputHelper outputHelper)
    {
        _siloCluster = testApp;
        _outputHelper = outputHelper;
        _firstApp = new TestWebApplication(_siloCluster, 8081);
        _secondApp = new TestWebApplication(_siloCluster, 8082);
    }

    [Fact]
    public async Task InvokeAsyncAndOnTest()
    {
        var hubConnection = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);


        var message = string.Empty;

        hubConnection.On("DoTest", (string m) =>
        {
            message = m;
            _outputHelper.WriteLine(message);
        });

        var result = -1;
        result = await hubConnection.InvokeAsync<int>("DoTest");
        await Task.Delay(TimeSpan.FromSeconds(5));
        message.Should().Be("test");
        result.Should().BeGreaterThan(0);
        result.Should().BeLessOrEqualTo(100);

        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected);
    }
    
    [Fact]
    public async Task AuthInvokeAsyncAndOnTest()
    {
        var client = _firstApp.CreateHttpClient();
        var responseMessage = await client.GetAsync("/auth?user=TestUser");
        var token = await responseMessage.Content.ReadAsStringAsync();
        var hubConnection1 = _firstApp.CreateSignalRClient(nameof(SimpleTestHub), configureConnection: options =>
        {
            options.AccessTokenProvider = () => Task.FromResult(token);
        });
        var hubConnection2 = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));

        
        await hubConnection1.StartAsync();
        await hubConnection2.StartAsync();
        hubConnection1.State.Should().Be(HubConnectionState.Connected);
        hubConnection2.State.Should().Be(HubConnectionState.Connected);


        var message1 = string.Empty;
        hubConnection1.On("DoUser", (string m) =>
        {
            message1 = m;
            _outputHelper.WriteLine(message1);
        });
        
        var message2 = string.Empty;
        hubConnection2.On("DoUser", (string m) =>
        {
            message2 = m;
            _outputHelper.WriteLine(message2);
        });

        var u1 = await hubConnection1.InvokeAsync<string>("DoUser");
        var u2 = await hubConnection2.InvokeAsync<string>("DoUser");

        await Task.Delay(TimeSpan.FromSeconds(5));

        message1.Should().Be(u1);
        u2.Should().Be("no");

        await hubConnection1.DisposeAsync();
        await hubConnection2.DisposeAsync();
    }

    [Fact]
    public async Task InvokeAsyncAndOnForTwoServersTest()
    {
        var hubConnection1 = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection1.StartAsync();
        hubConnection1.State.Should().Be(HubConnectionState.Connected);
        
        var hubConnection2 = _secondApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection2.StartAsync();
        hubConnection2.State.Should().Be(HubConnectionState.Connected);


        List<string> messages1 = new();
        List<string> messages2 = new();

        hubConnection1.On("SendAll", (string m) =>
        {
            messages1.Add(m);
            _outputHelper.WriteLine(m);
        });
        hubConnection2.On("SendAll", (string m) =>
        {
            messages2.Add(m);
            _outputHelper.WriteLine(m);
        });
        
        await hubConnection1.InvokeAsync("AddToGroup", "test");
        await hubConnection2.InvokeAsync("AddToGroup", "test");
        
        
        await Task.Delay(TimeSpan.FromSeconds(5));


        messages1.Count.Should().Be(2);
        messages2.Count.Should().Be(1);
    }

    [Fact]
    public async Task AsyncEnumerableServerToClientStreamingTest()
    {
        int iterations = 100;
        var hubConnection = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);

        // Call "Cancel" on this CancellationTokenSource to send a cancellation message to
        // the server, which will trigger the corresponding token in the hub method.
        var cancellationTokenSource = new CancellationTokenSource();
        var stream = hubConnection.StreamAsync<int>(
            "Counter", iterations, 100, cancellationTokenSource.Token);

        int count = 0;
        await foreach (var number in stream)
        {
            count++;
            _outputHelper.WriteLine($"{count}/{iterations}");
        }

        _outputHelper.WriteLine("Streaming completed");

        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        } 
        while (count != iterations);
        cancellationTokenSource.Cancel();
        
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected);

        iterations.Should().Be(count);
    }
    
    [Fact]
    public async Task StreamAsChannelAsyncServerToClientStreamingTest()
    {
        int iterations = 100;
        var hubConnection = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
        
        // Call "Cancel" on this CancellationTokenSource to send a cancellation message to
        // the server, which will trigger the corresponding token in the hub method.
        var cancellationTokenSource = new CancellationTokenSource();
        var channel = await hubConnection.StreamAsChannelAsync<int>(
            "CounterReader", iterations, 100, cancellationTokenSource.Token);

        var count = 0;
        try
        {
            // Wait asynchronously for data to become available
            while (await channel.WaitToReadAsync())
            {
                // Read all currently available data synchronously, before waiting for more data
                while (channel.TryRead(out var msg))
                {
                    count++;
                    _outputHelper.WriteLine($"{count}/{iterations}");
                
                    if(count == iterations)
                        cancellationTokenSource.Cancel();
                }
            }
        }
        catch (System.OperationCanceledException e)
        {
            //skip
        }
       
        
        _outputHelper.WriteLine("Streaming completed");
        
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected);

        iterations.Should().Be(count);
    }

    [Fact]
    public async Task  AsyncEnumerableClientToServerStreamingTest()
    {
        TestWebApplication.StaticLogs.Clear();
        
        int iterations = 100;
        var hubConnection = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
        
        async IAsyncEnumerable<string> clientStreamData()
        {
            for (var i = 0; i < iterations; i++)
            {
                yield return i.ToString();
            }
            //After the for loop has completed and the local function exits the stream completion will be sent.
        }

        await hubConnection.SendAsync("UploadStream", clientStreamData());

        await Task.Delay(TimeSpan.FromSeconds(5));
        
        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected);
        
        TestWebApplication.StaticLogs.Count.Should().Be(1);
        TestWebApplication.StaticLogs["UploadStream"].Count.Should().Be(iterations);
    }
    
    [Fact]
    public async Task  ChannelWriterClientToServerStreamingTest()
    {
        TestWebApplication.StaticLogs.Clear();
        
        int iterations = 100;
        var hubConnection = _firstApp.CreateSignalRClient(nameof(SimpleTestHub));
        await hubConnection.StartAsync();
        hubConnection.State.Should().Be(HubConnectionState.Connected);
        
        var channel = Channel.CreateBounded<string>(10);
        await hubConnection.SendAsync("UploadStreamChannelReader", channel.Reader);
        for (var i = 0; i < iterations; i++)
        {
            await channel.Writer.WriteAsync(i.ToString());
        }
        channel.Writer.Complete();
        
        await Task.Delay(TimeSpan.FromSeconds(5));
        await hubConnection.StopAsync();
        hubConnection.State.Should().Be(HubConnectionState.Disconnected);
        
        TestWebApplication.StaticLogs.Count.Should().Be(1);
        TestWebApplication.StaticLogs["UploadStreamChannelReader"].Count.Should().Be(iterations);

    
    }
    
    [Fact]
    public async Task AllTest()
    {
        List<HubConnection> connections = new();
        ConcurrentDictionary<string ,string> messages = new();

        for (int i = 0; i < 10; i++) 
        {
            var hubConnection =  i % 2 == 0  ? _firstApp.CreateSignalRClient(nameof(SimpleTestHub)) : _secondApp.CreateSignalRClient(nameof(SimpleTestHub));
            hubConnection.On("SendAll", (string m) =>
            {
                messages[hubConnection.ConnectionId] = m;
            });
            await hubConnection.StartAsync();
            hubConnection.State.Should().Be(HubConnectionState.Connected);
            connections.Add(hubConnection);
        }

        await connections[0].InvokeAsync<int>("All");

        await Task.Delay(TimeSpan.FromSeconds(5));
        messages.Count.Should().Be(10);
        messages.Clear();
        
        await connections[0].InvokeAsync<int>("Connections", connections.Take(5).Select(s=>s.ConnectionId));
        await Task.Delay(TimeSpan.FromSeconds(5));
        messages.Count.Should().Be(5);
        
        await Task.WhenAll(connections.Select(f=>f.StopAsync()));
    }
    
    [Fact]
    public async Task OtherTest()
    {
        List<HubConnection> connections = new();
        ConcurrentDictionary<string ,string> messages = new();

        for (int i = 0; i < 10; i++) 
        {
            var hubConnection =  i % 2 == 0  ? _firstApp.CreateSignalRClient(nameof(SimpleTestHub)) : _secondApp.CreateSignalRClient(nameof(SimpleTestHub));
            hubConnection.On("SendAll", (string m) =>
            {
                messages[hubConnection.ConnectionId] = m;
            });
            await hubConnection.StartAsync();
            hubConnection.State.Should().Be(HubConnectionState.Connected);
            connections.Add(hubConnection);
        }

        await connections[0].InvokeAsync<int>("Others");

        await Task.Delay(TimeSpan.FromSeconds(5));
        messages.Count.Should().Be(10-1);
        
        await Task.WhenAll(connections.Select(f=>f.StopAsync()));
    }
    
    [Fact]
    public async Task AllExceptTest()
    {
        List<HubConnection> connections = new();
        ConcurrentDictionary<string ,string> messages = new();

        for (int i = 0; i < 10; i++) 
        {
            var hubConnection =  i % 2 == 0  ? _firstApp.CreateSignalRClient(nameof(SimpleTestHub)) : _secondApp.CreateSignalRClient(nameof(SimpleTestHub));
            hubConnection.On("SendAll", (string m) =>
            {
                messages[hubConnection.ConnectionId] = m;
            });
            await hubConnection.StartAsync();
            hubConnection.State.Should().Be(HubConnectionState.Connected);
            connections.Add(hubConnection);
        }

        await connections[0].InvokeAsync<int>("AllExcept", connections.Take(4).Select(s=>s.ConnectionId));

        await Task.Delay(TimeSpan.FromSeconds(5));
        messages.Count.Should().Be(10-4);
        
        await Task.WhenAll(connections.Select(f=>f.StopAsync()));
    }
    
     [Fact]
    public async Task GroupTest()
    {
        List<HubConnection> connections = new();
        ConcurrentDictionary<string ,string> messages = new();

        for (int i = 0; i < 10; i++) 
        {
            var hubConnection =  i % 2 == 0  ? _firstApp.CreateSignalRClient(nameof(SimpleTestHub)) : _secondApp.CreateSignalRClient(nameof(SimpleTestHub));
            hubConnection.On("SendAll", (string m) =>
            {
                messages[hubConnection.ConnectionId] =  m;
            });
            await hubConnection.StartAsync();
            hubConnection.State.Should().Be(HubConnectionState.Connected);
            connections.Add(hubConnection);
        }

        foreach (var connection in connections)
        {
            await connection.InvokeAsync("AddToGroup", "testGroup");
        }
        
        await connections[0].InvokeAsync("GroupSendAsync", "testGroup", "test");
       
        //all group members
        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(10);
        messages.Clear();
        
        //remove 2 connections from group
        await connections[0].InvokeAsync("RemoveFromGroup", "testGroup");
        await connections[1].InvokeAsync("RemoveFromGroup", "testGroup");
        
        //add to another 2 connections from group
        await connections[0].InvokeAsync("AddToGroup", "testGroup2");
        await connections[1].InvokeAsync("AddToGroup", "testGroup2");
       
        await Task.Delay(TimeSpan.FromSeconds(2));
        messages.Clear();
        
        await connections[0].InvokeAsync("GroupSendAsync", "testGroup", "test");
        await Task.Delay(TimeSpan.FromSeconds(4));
        messages.Count.Should().Be(8);
        messages.Clear();
        
        await connections[0].InvokeAsync("GroupSendAsync", "testGroup2", "test");
        await Task.Delay(TimeSpan.FromSeconds(4));
        messages.Count.Should().Be(2);
        messages.Clear();
        
        //exclude 4 connections for request
        await connections[0].InvokeAsync("SendGroupExceptAsync", "testGroup", "test", connections.Skip(4).Take(4).Select(s=>s.ConnectionId));

        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(4);
        messages.Clear();
        
        //send to all, but two was removed
        await connections[0].InvokeAsync("GroupSendAsync", "testGroup", "test");
        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(8);
        messages.Clear();
        
        //send to all grups
        await connections[0].InvokeAsync("ManyGroupSendAsync", new List<string>{"testGroup", "testGroup2"}, "test");
        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(10);
        messages.Clear();
        
        await Task.WhenAll(connections.Select(f=>f.StopAsync()));
    }

    [Fact]
    public async Task UsersTest()
    {
        List<HubConnection> connections = new();
        ConcurrentDictionary<string ,string> messages = new();

        for (int i = 0; i < 10; i++)
        {
            var client = i % 2 == 0 ? _firstApp.CreateHttpClient() : _secondApp.CreateHttpClient();
            var responseMessage = await client.GetAsync("/auth?user=TestUser"+i);
            var token = await responseMessage.Content.ReadAsStringAsync();

            for (int j = 0; j < 4; j++)
            {
                var hubConnection = j % 2 == 0
                    ? _firstApp.CreateSignalRClient(nameof(SimpleTestHub), configureConnection: options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(token);
                    })
                    : _secondApp.CreateSignalRClient(nameof(SimpleTestHub), configureConnection: options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(token);
                    });
                hubConnection.On("SendAll", (string m) =>
                {
                    messages[hubConnection.ConnectionId] =  m;
                });
                await hubConnection.StartAsync();
                hubConnection.State.Should().Be(HubConnectionState.Connected);
                connections.Add(hubConnection);
            }
           
        }
        

        //send message to user with 4 connections
        await connections[0].InvokeAsync("SentToUser", "TestUser4", "test");
        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(4);
        messages.Clear();
        
        //send message to user with 4 connections
        await connections[0].InvokeAsync("SentToUserIds", new List<string>{ "TestUser4", "TestUser5", "nonExist"}, "test");
        await Task.Delay(TimeSpan.FromSeconds(3));
        messages.Count.Should().Be(8);
        messages.Clear();

        await Task.WhenAll(connections.Select(f=>f.StopAsync()));
        
    }
}