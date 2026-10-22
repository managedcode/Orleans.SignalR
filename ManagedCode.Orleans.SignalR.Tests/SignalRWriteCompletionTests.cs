using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure;
using ManagedCode.Orleans.SignalR.Tests.TestApp;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(SmokeCluster))]
public sealed class SignalRWriteCompletionTests(SmokeClusterFixture cluster)
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _blockedObservation = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task ObserverDeliveryWaitsForTheNativeTransportFlushAsync()
    {
        var capture = new ConnectionObserverCapture();
        using var app = CreateApplication(capture);
        await using var transport = new BlockedSignalRTransport(app.Services);
        await using var client = transport.CreateClient();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = client.On("WriteCompletionProbe", (string value) => received.TrySetResult(value));
        await client.StartAsync();
        var observer = await capture.Ready.Task.WaitAsync(_timeout);
        transport.BlockNextFlush();
        var delivery = observer.OnNextAsync(new InvocationMessage("WriteCompletionProbe", ["observer-payload"]));
        try
        {
            await transport.Entered.WaitAsync(_timeout);
            (await received.Task.WaitAsync(_timeout)).ShouldBe("observer-payload");
            await AssertBlockedAsync(delivery);
        }
        finally
        {
            transport.Release();
        }
        await delivery.WaitAsync(_timeout);
        (await client.InvokeAsync<int>("Plus", 2, 3)).ShouldBe(5);
    }

    [Fact]
    public async Task LocalClientResultDoesNotCompleteBeforeItsNativeTransportFlushAsync()
    {
        var capture = new ConnectionObserverCapture();
        using var app = CreateApplication(capture);
        await using var transport = new BlockedSignalRTransport(app.Services);
        await using var client = transport.CreateClient();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = client.On("GetMessage", () =>
        {
            invoked.TrySetResult();
            return "client-result";
        });
        await client.StartAsync();
        await capture.Ready.Task.WaitAsync(_timeout);
        transport.BlockNextFlush();
        var manager = app.Services.GetRequiredService<HubLifetimeManager<SimpleTestHub>>();
        var result = manager.InvokeConnectionAsync<string>(client.ConnectionId!, "GetMessage", [], CancellationToken.None);
        try
        {
            await transport.Entered.WaitAsync(_timeout);
            await invoked.Task.WaitAsync(_timeout);
            await AssertBlockedAsync(result);
        }
        finally
        {
            transport.Release();
        }
        (await result.WaitAsync(_timeout)).ShouldBe("client-result");
        (await client.InvokeAsync<int>("Plus", 3, 4)).ShouldBe(7);
    }

    private TestWebApplication CreateApplication(ConnectionObserverCapture capture) =>
        new(cluster, configureServices: services =>
        {
            services.AddSingleton(capture);
            services.AddSignalR().AddHubOptions<SimpleTestHub>(options =>
            {
                options.AddFilter<ConnectionObserverCapture>();
                options.KeepAliveInterval = TimeSpan.FromMinutes(1);
            });
        });

    private static async Task AssertBlockedAsync(Task operation)
    {
        var observation = Task.Delay(_blockedObservation);
        (await Task.WhenAny(operation, observation)).ShouldBe(observation,
            "The owning operation must await the asynchronous native SignalR transport flush.");
    }
}
